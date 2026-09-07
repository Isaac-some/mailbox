using MailArchiver.Models;

namespace MailArchiver.ViewModels;

public sealed class NetworkDiagnosticsViewModel
{
    public string Target { get; set; } = "https://graph.microsoft.com";
    public string? Error { get; set; }
    public NetworkDiagnosticResult? Result { get; set; }
}
