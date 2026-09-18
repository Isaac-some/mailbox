namespace MailArchiver.Models.ViewModels
{
    public class CsvImportResultViewModel
    {
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }
        public int PendingVerificationCount { get; set; }
        public int FormatWarningCount { get; set; }
        public int WarningCount { get; set; }
        public int VerificationSuccessCount { get; set; }
        public int VerificationFailedCount { get; set; }
        public int VerificationFormatFailureCount { get; set; }
        public int VerificationAuthFailureCount { get; set; }
        public int VerificationNetworkFailureCount { get; set; }
        public int VerificationRateLimitCount { get; set; }
        public string? JobId { get; set; }
        public string? Status { get; set; }
        public string? ErrorMessage { get; set; }

        public List<CsvImportCreatedRow> CreatedRows { get; set; } = new();
        public List<CsvImportCreatedRow> UpdatedRows { get; set; } = new();
        public List<CsvImportSkippedRow> SkippedRows { get; set; } = new();
        public List<CsvImportFailedRow> FailedRows { get; set; } = new();
    }

    public class CsvImportCreatedRow
    {
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class CsvImportSkippedRow
    {
        public string Email { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public class CsvImportFailedRow
    {
        public string FileName { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public class CsvParsedRow
    {
        public string SourceFileName { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? Domain { get; set; }
        public ProviderType Provider { get; set; } = ProviderType.IMAP;
        public MailProviderKind MailProviderKind { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? OAuthRefreshToken { get; set; }
        public string? OAuthGrantedScopes { get; set; }
        public string? OAuthRedirectUri { get; set; }
        public string? Username { get; set; }
        public string? ImapServer { get; set; }
        public int? ImapPort { get; set; }
        public bool? UseSSL { get; set; }
        public string? ImportWarning { get; set; }
    }
}
