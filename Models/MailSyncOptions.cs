namespace MailArchiver.Models
{
    public class MailSyncOptions
    {
        public const string MailSync = "MailSync";

        // Upper bound for the first sync window. Zero disables the bound.
        public int LookbackDays { get; set; } = 30;
        // Keep the desktop archive bounded. Zero disables the count cap.
        public int MaxStoredEmailsPerAccount { get; set; } = 30;
        // The lightweight mailbox UI only needs received mail. When enabled, sync
        // INBOX plus provider junk folders, but skip sent, drafts, trash, and archives.
        public bool SyncInboxOnly { get; set; } = true;
        public int ConnectionTimeoutSeconds { get; set; } = 180;
        public int CommandTimeoutSeconds { get; set; } = 300;
        public bool IgnoreSelfSignedCert { get; set; } = false;
        public int MaxConcurrentSyncs { get; set; } = 4;
    }
}
