using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
Console.WriteLine(JsonSerializer.Serialize(args.Select(path => {
    string assemblyVersion = null;
    try { assemblyVersion = AssemblyName.GetAssemblyName(path).Version?.ToString(); }
    catch (BadImageFormatException) { }
    return new { path, assemblyVersion, fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion, productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion };
})));
