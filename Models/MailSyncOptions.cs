namespace MailArchiver.Models
{
    public class MailSyncOptions
    {
        public const string MailSync = "MailSync";

        public bool Enabled { get; set; } = false;

        // Preferred global quick-sync interval. Per-account minute values still
        // override this setting when explicitly configured.
        public int? IntervalSeconds { get; set; }
        public int PollIntervalSeconds { get; set; } = 5;
        public int FailureRetrySeconds { get; set; } = 300;

        // Legacy fallback for installations that have not configured IntervalSeconds.
        public int IntervalMinutes { get; set; } = 5;
        public int? FullSyncIntervalHours { get; set; }
        // Upper bound for the first sync window. Zero disables the bound.
        public int LookbackDays { get; set; } = 30;
        // Keep the desktop archive bounded. Zero disables the count cap.
        public int MaxStoredEmailsPerAccount { get; set; } = 30;
        // The lightweight mailbox UI only needs received mail. When enabled, sync
        // INBOX plus provider junk folders, but skip sent, drafts, trash, and archives.
        public bool SyncInboxOnly { get; set; } = true;
        public int TimeoutMinutes { get; set; } = 60;
        public int ConnectionTimeoutSeconds { get; set; } = 180;
        public int CommandTimeoutSeconds { get; set; } = 300;
        public bool AlwaysForceFullSync { get; set; } = false;
        public bool IgnoreSelfSignedCert { get; set; } = false;
        public int MaxConcurrentSyncs { get; set; } = 4;
        public int InterAccountDelaySeconds { get; set; } = 0;
        // Spread accounts after a restart so thousands of accounts do not all
        // become due in the same poll cycle.
        public int StartupStaggerSeconds { get; set; } = 21600;
    }
}
