import Cocoa
import Security
import WebKit

final class AppDelegate: NSObject, NSApplicationDelegate, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    private let appName = "邮箱助手"
    private let localPort = 5180
    private var window: NSWindow!
    private var webView: WKWebView!
    private var server: Process?
    private var serverPassword = ""
    private var isQuitting = false
    private var downloadDestinations: [ObjectIdentifier: URL] = [:]

    private var dataDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("KouziMailAssistant", isDirectory: true)
    }

    private var resetMarker: URL {
        dataDirectory.appendingPathComponent("factory-reset.request")
    }

    private var webKitDataDirectory: URL {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("WebKit", isDirectory: true)
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.kouzi.mailassistant", isDirectory: true)
    }

    private var httpStorageDirectory: URL {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("HTTPStorages", isDirectory: true)
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.kouzi.mailassistant", isDirectory: true)
    }

    private var cacheDirectory: URL {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.kouzi.mailassistant", isDirectory: true)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        configureMainMenu()
        configureWindow()
        NSApp.activate(ignoringOtherApps: true)

        DispatchQueue.main.async { [weak self] in
            self?.startServer()
        }
    }

    func applicationShouldHandleReopen(
        _ sender: NSApplication,
        hasVisibleWindows flag: Bool
    ) -> Bool {
        if !flag {
            window.makeKeyAndOrderFront(nil)
        }
        NSApp.activate(ignoringOtherApps: true)
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        isQuitting = true
        server?.terminationHandler = nil
        server?.terminate()
        return .terminateNow
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    private func configureMainMenu() {
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu(title: appName)
        appMenu.addItem(withTitle: "退出\(appName)", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        let editMenuItem = NSMenuItem(title: "编辑", action: nil, keyEquivalent: "")
        let editMenu = NSMenu(title: "编辑")
        editMenu.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        NSApp.mainMenu = mainMenu
    }

    private func configureWindow() {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = self
        webView.uiDelegate = self
        webView.setValue(false, forKey: "drawsBackground")

        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = appName
        window.minSize = NSSize(width: 960, height: 650)
        window.isReleasedWhenClosed = false
        window.isRestorable = false
        window.contentView = webView
        window.center()
        window.makeKeyAndOrderFront(nil)
        webView.loadHTMLString("<html><body style='font:16px -apple-system;display:flex;align-items:center;justify-content:center;height:100vh;color:#667085'>正在打开邮箱助手...</body></html>", baseURL: nil)
    }

    private func startServer() {
        do {
            if FileManager.default.fileExists(atPath: resetMarker.path) {
                try resetLocalData()
            }
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: dataDirectory.appendingPathComponent("keys", isDirectory: true), withIntermediateDirectories: true)
            serverPassword = randomBase64(byteCount: 32)
            let credentialKeyPath = dataDirectory.appendingPathComponent("credential-encryption.key")
            if !FileManager.default.fileExists(atPath: credentialKeyPath.path) {
                let credentialKey = randomBase64(byteCount: 32)
                try credentialKey.write(to: credentialKeyPath, atomically: true, encoding: .utf8)
                try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: credentialKeyPath.path)
            }

            guard let serverAssembly = Bundle.main.url(forResource: "MailArchiver", withExtension: "dll", subdirectory: "server"),
                  let runtime = Bundle.main.url(forResource: "dotnet", withExtension: nil, subdirectory: "dotnet") else {
                showFatal("应用服务文件缺失，无法启动。")
                return
            }

            let serverDirectory = serverAssembly.deletingLastPathComponent()
            let process = Process()
            process.executableURL = runtime
            process.arguments = [serverAssembly.path]
            process.currentDirectoryURL = serverDirectory
            var environment = ProcessInfo.processInfo.environment
            environment["ASPNETCORE_ENVIRONMENT"] = "Local"
            environment["ASPNETCORE_CONTENTROOT"] = serverDirectory.path
            environment["ASPNETCORE_URLS"] = "http://127.0.0.1:\(localPort)"
            environment["KOUZI_LOCAL_APP"] = "1"
            environment["KOUZI_DATA_DIRECTORY"] = dataDirectory.path
            environment["KOUZI_FACTORY_RESET_MARKER"] = resetMarker.path
            environment["DOTNET_ROOT"] = runtime.deletingLastPathComponent().path
            environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
            environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false"
            environment["ConnectionStrings__DefaultConnection"] = "Data Source=\(dataDirectory.appendingPathComponent("mail-archive.sqlite").path)"
            environment["DataProtection__KeyPath"] = dataDirectory.appendingPathComponent("keys", isDirectory: true).path
            environment["CredentialEncryption__KeyFilePath"] = credentialKeyPath.path
            environment["Authentication__Username"] = "local"
            environment["Authentication__Password"] = serverPassword
            applyDetectedMailProxy(to: &environment)
            process.environment = environment
            process.terminationHandler = { [weak self] _ in
                DispatchQueue.main.async { self?.serverStopped() }
            }
            try process.run()
            server = process
            waitForServer(attempt: 0)
        } catch {
            showFatal("无法启动本机服务：\(error.localizedDescription)")
        }
    }

    private func applyDetectedMailProxy(to environment: inout [String: String]) {
        let configURL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/gw/vortex.json")
        guard let data = try? Data(contentsOf: configURL),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["connected"] as? Bool == true,
              let portNumber = object["proxy_port"] as? NSNumber else {
            return
        }

        let port = portNumber.intValue
        guard port > 0 && port <= 65535 else { return }
        environment["MailProxy__Enabled"] = "true"
        environment["MailProxy__Type"] = "Socks5"
        environment["MailProxy__Host"] = "127.0.0.1"
        environment["MailProxy__Port"] = String(port)
    }

    private func serverStopped() {
        guard !isQuitting else { return }
        if FileManager.default.fileExists(atPath: resetMarker.path) {
            do {
                try resetLocalData()
                startServer()
            } catch {
                showFatal("恢复出厂设置未完成：\(error.localizedDescription)")
            }
            return
        }
        showFatal("本机服务已停止。请重新打开应用。")
    }

    private func resetLocalData() throws {
        let directories = [dataDirectory, webKitDataDirectory, httpStorageDirectory, cacheDirectory]
        for directory in directories where FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    private func waitForServer(attempt: Int) {
        guard attempt < 60 else {
            showFatal("本机服务启动超时。")
            return
        }
        let url = URL(string: "http://127.0.0.1:\(localPort)/Auth/Login")!
        URLSession.shared.dataTask(with: url) { [weak self] _, response, _ in
            guard let self else { return }
            if (response as? HTTPURLResponse)?.statusCode == 200 {
                DispatchQueue.main.async {
                    self.webView.load(URLRequest(url: url))
                }
            } else {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                    self.waitForServer(attempt: attempt + 1)
                }
            }
        }.resume()
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard webView.url?.path.lowercased().hasPrefix("/auth/login") == true else { return }
        let username = jsonString("local")
        let password = jsonString(serverPassword)
        let script = """
        (() => {
          const form = document.querySelector('form[action="/Auth/Login"], form');
          const username = document.querySelector('input[name="Username"]');
          const password = document.querySelector('input[name="Password"]');
          const remember = document.querySelector('input[name="RememberMe"]');
          if (!form || !username || !password) return;
          username.value = \(username);
          password.value = \(password);
          if (remember) remember.checked = true;
          form.submit();
        })();
        """
        webView.evaluateJavaScript(script)
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url else {
            decisionHandler(.cancel)
            return
        }
        if navigationAction.shouldPerformDownload {
            decisionHandler(.download)
            return
        }
        if url.host == "127.0.0.1" || url.host == "localhost" || url.scheme == "about" {
            decisionHandler(.allow)
        } else {
            NSWorkspace.shared.open(url)
            decisionHandler(.cancel)
        }
    }

    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationResponse: WKNavigationResponse,
        decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void
    ) {
        let disposition = (navigationResponse.response as? HTTPURLResponse)?
            .value(forHTTPHeaderField: "Content-Disposition")?
            .lowercased()
        if disposition?.contains("attachment") == true || !navigationResponse.canShowMIMEType {
            decisionHandler(.download)
        } else {
            decisionHandler(.allow)
        }
    }

    func webView(
        _ webView: WKWebView,
        navigationAction: WKNavigationAction,
        didBecome download: WKDownload
    ) {
        download.delegate = self
    }

    func webView(
        _ webView: WKWebView,
        navigationResponse: WKNavigationResponse,
        didBecome download: WKDownload
    ) {
        download.delegate = self
    }

    func download(
        _ download: WKDownload,
        decideDestinationUsing response: URLResponse,
        suggestedFilename: String,
        completionHandler: @escaping (URL?) -> Void
    ) {
        do {
            let downloadsDirectory = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
            try FileManager.default.createDirectory(
                at: downloadsDirectory,
                withIntermediateDirectories: true)
            let destination = uniqueDownloadURL(
                in: downloadsDirectory,
                suggestedFilename: suggestedFilename)
            downloadDestinations[ObjectIdentifier(download)] = destination
            completionHandler(destination)
        } catch {
            completionHandler(nil)
            showDownloadError(error.localizedDescription)
        }
    }

    func downloadDidFinish(_ download: WKDownload) {
        guard let destination = downloadDestinations.removeValue(forKey: ObjectIdentifier(download)) else {
            return
        }
        NSWorkspace.shared.activateFileViewerSelecting([destination])
    }

    func download(
        _ download: WKDownload,
        didFailWithError error: Error,
        resumeData: Data?
    ) {
        downloadDestinations.removeValue(forKey: ObjectIdentifier(download))
        showDownloadError(error.localizedDescription)
    }

    func webView(
        _ webView: WKWebView,
        runOpenPanelWith parameters: WKOpenPanelParameters,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping ([URL]?) -> Void
    ) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = parameters.allowsDirectories
        panel.allowsMultipleSelection = parameters.allowsMultipleSelection

        panel.beginSheetModal(for: window) { response in
            completionHandler(response == .OK ? panel.urls : nil)
        }
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptAlertPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping () -> Void
    ) {
        let alert = NSAlert()
        alert.messageText = message
        alert.addButton(withTitle: "确定")
        alert.beginSheetModal(for: window) { _ in
            completionHandler()
        }
    }

    func webView(
        _ webView: WKWebView,
        runJavaScriptConfirmPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping (Bool) -> Void
    ) {
        let alert = NSAlert()
        alert.messageText = message
        alert.addButton(withTitle: "确认")
        alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { response in
            completionHandler(response == .alertFirstButtonReturn)
        }
    }

    private func showFatal(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "\(appName)无法继续"
        alert.informativeText = message
        alert.addButton(withTitle: "退出")
        alert.runModal()
        NSApp.terminate(nil)
    }

    private func uniqueDownloadURL(in directory: URL, suggestedFilename: String) -> URL {
        let sanitized = (suggestedFilename as NSString).lastPathComponent
        let filename = sanitized.isEmpty ? "邮箱助手下载.csv" : sanitized
        let extensionName = (filename as NSString).pathExtension
        let baseName = (filename as NSString).deletingPathExtension
        var candidate = directory.appendingPathComponent(filename)
        var suffix = 2

        while FileManager.default.fileExists(atPath: candidate.path) {
            let numberedName = extensionName.isEmpty
                ? "\(baseName) \(suffix)"
                : "\(baseName) \(suffix).\(extensionName)"
            candidate = directory.appendingPathComponent(numberedName)
            suffix += 1
        }
        return candidate
    }

    private func showDownloadError(_ detail: String) {
        let alert = NSAlert()
        alert.messageText = "文件下载失败"
        alert.informativeText = detail
        alert.addButton(withTitle: "确定")
        alert.beginSheetModal(for: window)
    }

    private func randomBase64(byteCount: Int) -> String {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
    }

    private func jsonString(_ value: String) -> String {
        let data = try! JSONSerialization.data(withJSONObject: [value])
        return String(data: data, encoding: .utf8)!.dropFirst().dropLast().description
    }

}

@main
private enum KouziMailAssistantMain {
    static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.run()
    }
}
