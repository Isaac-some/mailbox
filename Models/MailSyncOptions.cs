namespace MailArchiver.Models
{
    public class MailSyncOptions
    {
        public const string MailSync = "MailSync";

        // Upper bound for the first sync window. Zero disables the bound.
        public int LookbackDays { get; set; } = 7;
        public int InitialMessagesPerCategory { get; set; } = 10;
        public int ExpandedMessagesPerCategory { get; set; } = 30;
        // Retained for installations that still bind the legacy setting. Category
        // limits are authoritative for the mailbox UI.
        public int MaxStoredEmailsPerAccount { get; set; } = 30;
        public bool SyncInboxOnly { get; set; } = false;
        public int ConnectionTimeoutSeconds { get; set; } = 180;
        public int CommandTimeoutSeconds { get; set; } = 300;
        public bool IgnoreSelfSignedCert { get; set; } = false;
        public int MaxConcurrentSyncs { get; set; } = 4;
    }
}
