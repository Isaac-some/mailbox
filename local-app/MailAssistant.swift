import Cocoa
import Security
import WebKit

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    private enum WindowMode: String {
        case desktop
        case compact
    }

    private enum PreferenceKey {
        static let windowMode = "windowMode"
        static let alwaysOnTop = "alwaysOnTop"
        static let desktopWidth = "desktopWindowWidth"
        static let desktopHeight = "desktopWindowHeight"
        static let compactWidth = "compactWindowWidth"
        static let compactHeight = "compactWindowHeight"
    }

    private enum ToolbarIdentifier {
        static let main = NSToolbar.Identifier("MailAssistant.MainToolbar")
        static let windowMode = NSToolbarItem.Identifier("MailAssistant.WindowMode")
        static let alwaysOnTop = NSToolbarItem.Identifier("MailAssistant.AlwaysOnTop")
    }

    private let appName = "邮箱助手"
    private let localPort = 5180
    private let defaults = UserDefaults.standard
    private var window: NSWindow!
    private var webView: WKWebView!
    private var server: Process?
    private var isQuitting = false
    private var isApplyingWindowFrame = false
    private var windowMode: WindowMode = .desktop
    private var alwaysOnTop = false
    private var desktopWindowMenuItem: NSMenuItem?
    private var compactWindowMenuItem: NSMenuItem?
    private var alwaysOnTopMenuItem: NSMenuItem?
    private var windowModeToolbarItem: NSToolbarItem?
    private var alwaysOnTopToolbarItem: NSToolbarItem?
    private var downloadDestinations: [ObjectIdentifier: URL] = [:]

    private var dataDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("MailAssistant", isDirectory: true)
    }

    private var resetMarker: URL {
        dataDirectory.appendingPathComponent("factory-reset.request")
    }

    private var webKitDataDirectory: URL {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("WebKit", isDirectory: true)
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.mailbox.assistant", isDirectory: true)
    }

    private var httpStorageDirectory: URL {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("HTTPStorages", isDirectory: true)
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.mailbox.assistant", isDirectory: true)
    }

    private var cacheDirectory: URL {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.mailbox.assistant", isDirectory: true)
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

        let windowMenuItem = NSMenuItem(title: "窗口", action: nil, keyEquivalent: "")
        let windowMenu = NSMenu(title: "窗口")

        let desktopItem = NSMenuItem(
            title: "桌面窗口",
            action: #selector(useDesktopWindow(_:)),
            keyEquivalent: "")
        desktopItem.target = self
        desktopWindowMenuItem = desktopItem
        windowMenu.addItem(desktopItem)

        let compactItem = NSMenuItem(
            title: "手机窄窗",
            action: #selector(useCompactWindow(_:)),
            keyEquivalent: "m")
        compactItem.keyEquivalentModifierMask = [.command, .option]
        compactItem.target = self
        compactWindowMenuItem = compactItem
        windowMenu.addItem(compactItem)

        windowMenu.addItem(.separator())

        let pinItem = NSMenuItem(
            title: "始终置顶",
            action: #selector(toggleAlwaysOnTop(_:)),
            keyEquivalent: "p")
        pinItem.keyEquivalentModifierMask = [.command, .option]
        pinItem.target = self
        alwaysOnTopMenuItem = pinItem
        windowMenu.addItem(pinItem)

        windowMenuItem.submenu = windowMenu
        mainMenu.addItem(windowMenuItem)
        NSApp.windowsMenu = windowMenu

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
        window.isReleasedWhenClosed = false
        window.isRestorable = false
        window.delegate = self
        window.contentView = webView

        let toolbar = NSToolbar(identifier: ToolbarIdentifier.main)
        toolbar.delegate = self
        toolbar.displayMode = .iconOnly
        toolbar.allowsUserCustomization = false
        window.toolbar = toolbar
        window.toolbarStyle = .unifiedCompact

        windowMode = WindowMode(rawValue: defaults.string(forKey: PreferenceKey.windowMode) ?? "") ?? .desktop
        alwaysOnTop = defaults.bool(forKey: PreferenceKey.alwaysOnTop)
        applyWindowMode(windowMode, animated: false, centered: true)
        applyAlwaysOnTop()
        window.makeKeyAndOrderFront(nil)
        webView.loadHTMLString("<html><body style='font:16px -apple-system;display:flex;align-items:center;justify-content:center;height:100vh;color:#667085'>正在打开邮箱助手...</body></html>", baseURL: nil)
    }

    @objc private func useDesktopWindow(_ sender: Any?) {
        applyWindowMode(.desktop, animated: true, centered: false)
    }

    @objc private func useCompactWindow(_ sender: Any?) {
        applyWindowMode(.compact, animated: true, centered: false)
    }

    @objc private func toggleWindowMode(_ sender: Any?) {
        applyWindowMode(windowMode == .compact ? .desktop : .compact, animated: true, centered: false)
    }

    @objc private func toggleAlwaysOnTop(_ sender: Any?) {
        alwaysOnTop.toggle()
        defaults.set(alwaysOnTop, forKey: PreferenceKey.alwaysOnTop)
        applyAlwaysOnTop()
    }

    private func applyWindowMode(_ mode: WindowMode, animated: Bool, centered: Bool) {
        windowMode = mode
        defaults.set(mode.rawValue, forKey: PreferenceKey.windowMode)

        let minimumSize = minimumFrameSize(for: mode)
        window.minSize = minimumSize
        let targetSize = fittedFrameSize(savedFrameSize(for: mode), minimum: minimumSize)
        let targetFrame = positionedFrame(size: targetSize, centered: centered)

        isApplyingWindowFrame = true
        window.setFrame(targetFrame, display: true, animate: animated)
        isApplyingWindowFrame = false
        updateWindowControls()
    }

    private func applyAlwaysOnTop() {
        window.level = alwaysOnTop ? NSWindow.Level.floating : NSWindow.Level.normal
        updateWindowControls()
    }

    private func minimumFrameSize(for mode: WindowMode) -> NSSize {
        mode == .compact
            ? NSSize(width: 360, height: 560)
            : NSSize(width: 960, height: 650)
    }

    private func savedFrameSize(for mode: WindowMode) -> NSSize {
        let widthKey = mode == .compact ? PreferenceKey.compactWidth : PreferenceKey.desktopWidth
        let heightKey = mode == .compact ? PreferenceKey.compactHeight : PreferenceKey.desktopHeight
        let defaultSize = mode == .compact
            ? NSSize(width: 420, height: 860)
            : NSSize(width: 1280, height: 820)
        let savedWidth = defaults.double(forKey: widthKey)
        let savedHeight = defaults.double(forKey: heightKey)
        return NSSize(
            width: savedWidth > 0 ? savedWidth : defaultSize.width,
            height: savedHeight > 0 ? savedHeight : defaultSize.height)
    }

    private func fittedFrameSize(_ requested: NSSize, minimum: NSSize) -> NSSize {
        let visibleFrame = window.screen?.visibleFrame ?? NSScreen.main?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: requested.width, height: requested.height)
        return NSSize(
            width: min(max(requested.width, minimum.width), visibleFrame.width),
            height: min(max(requested.height, minimum.height), visibleFrame.height))
    }

    private func positionedFrame(size: NSSize, centered: Bool) -> NSRect {
        let visibleFrame = window.screen?.visibleFrame ?? NSScreen.main?.visibleFrame
            ?? NSRect(origin: .zero, size: size)
        let origin: NSPoint
        if centered {
            origin = NSPoint(
                x: visibleFrame.midX - size.width / 2,
                y: visibleFrame.midY - size.height / 2)
        } else {
            let currentCenter = NSPoint(x: window.frame.midX, y: window.frame.midY)
            origin = NSPoint(
                x: min(max(currentCenter.x - size.width / 2, visibleFrame.minX), visibleFrame.maxX - size.width),
                y: min(max(currentCenter.y - size.height / 2, visibleFrame.minY), visibleFrame.maxY - size.height))
        }
        return NSRect(origin: origin, size: size)
    }

    private func persistCurrentWindowSize() {
        guard !isApplyingWindowFrame else { return }
        let widthKey = windowMode == .compact ? PreferenceKey.compactWidth : PreferenceKey.desktopWidth
        let heightKey = windowMode == .compact ? PreferenceKey.compactHeight : PreferenceKey.desktopHeight
        defaults.set(window.frame.width, forKey: widthKey)
        defaults.set(window.frame.height, forKey: heightKey)
    }

    private func updateWindowControls() {
        desktopWindowMenuItem?.state = windowMode == .desktop ? .on : .off
        compactWindowMenuItem?.state = windowMode == .compact ? .on : .off
        alwaysOnTopMenuItem?.state = alwaysOnTop ? .on : .off

        windowModeToolbarItem?.label = windowMode == .compact ? "桌面窗口" : "手机窄窗"
        windowModeToolbarItem?.toolTip = windowMode == .compact ? "切换到桌面窗口" : "切换到手机窄窗"
        windowModeToolbarItem?.image = NSImage(
            systemSymbolName: windowMode == .compact ? "rectangle" : "rectangle.portrait",
            accessibilityDescription: windowModeToolbarItem?.toolTip)

        alwaysOnTopToolbarItem?.label = alwaysOnTop ? "取消置顶" : "始终置顶"
        alwaysOnTopToolbarItem?.toolTip = alwaysOnTop ? "取消始终置顶" : "始终置顶"
        alwaysOnTopToolbarItem?.image = NSImage(
            systemSymbolName: alwaysOnTop ? "pin.fill" : "pin",
            accessibilityDescription: alwaysOnTopToolbarItem?.toolTip)
    }

    func windowDidResize(_ notification: Notification) {
        persistCurrentWindowSize()
    }

    func windowWillClose(_ notification: Notification) {
        persistCurrentWindowSize()
    }

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.flexibleSpace, ToolbarIdentifier.windowMode, ToolbarIdentifier.alwaysOnTop]
    }

    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.flexibleSpace, ToolbarIdentifier.windowMode, ToolbarIdentifier.alwaysOnTop]
    }

    func toolbar(
        _ toolbar: NSToolbar,
        itemForItemIdentifier itemIdentifier: NSToolbarItem.Identifier,
        willBeInsertedIntoToolbar flag: Bool
    ) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: itemIdentifier)
        item.target = self
        item.isBordered = true
        switch itemIdentifier {
        case ToolbarIdentifier.windowMode:
            item.action = #selector(toggleWindowMode(_:))
            windowModeToolbarItem = item
        case ToolbarIdentifier.alwaysOnTop:
            item.action = #selector(toggleAlwaysOnTop(_:))
            alwaysOnTopToolbarItem = item
        default:
            return nil
        }
        updateWindowControls()
        return item
    }

    private func startServer() {
        do {
            try migrateExistingDataIfNeeded()
            if FileManager.default.fileExists(atPath: resetMarker.path) {
                try resetLocalData()
            }
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: dataDirectory.appendingPathComponent("keys", isDirectory: true), withIntermediateDirectories: true)
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
            environment["MAIL_ASSISTANT_LOCAL_APP"] = "1"
            environment["MAIL_ASSISTANT_DATA_DIRECTORY"] = dataDirectory.path
            environment["MAIL_ASSISTANT_FACTORY_RESET_MARKER"] = resetMarker.path
            environment["ReleaseNotes__AppVersion"] = Bundle.main.object(
                forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "2.3.1"
            environment["DOTNET_ROOT"] = runtime.deletingLastPathComponent().path
            environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
            environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false"
            environment["ConnectionStrings__DefaultConnection"] = "Data Source=\(dataDirectory.appendingPathComponent("mail-archive.sqlite").path)"
            environment["DataProtection__KeyPath"] = dataDirectory.appendingPathComponent("keys", isDirectory: true).path
            environment["CredentialEncryption__KeyFilePath"] = credentialKeyPath.path
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

    private func migrateExistingDataIfNeeded() throws {
        let fileManager = FileManager.default
        guard !fileManager.fileExists(atPath: dataDirectory.path) else { return }

        let base = dataDirectory.deletingLastPathComponent()
        let candidates = try fileManager.contentsOfDirectory(
            at: base,
            includingPropertiesForKeys: [.isDirectoryKey, .contentModificationDateKey],
            options: [.skipsHiddenFiles])
            .filter { candidate in
                guard candidate.lastPathComponent != dataDirectory.lastPathComponent else { return false }
                var isDirectory: ObjCBool = false
                guard fileManager.fileExists(atPath: candidate.path, isDirectory: &isDirectory), isDirectory.boolValue else {
                    return false
                }
                return fileManager.fileExists(atPath: candidate.appendingPathComponent("mail-archive.sqlite").path)
                    && fileManager.fileExists(atPath: candidate.appendingPathComponent("credential-encryption.key").path)
            }

        guard candidates.count == 1, let existingDataDirectory = candidates.first else { return }
        try fileManager.moveItem(at: existingDataDirectory, to: dataDirectory)
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
private enum MailAssistantMain {
    static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.run()
    }
}
