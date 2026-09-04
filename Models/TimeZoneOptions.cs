namespace MailArchiver.Models
{
    public class TimeZoneOptions
    {
        // Archive timestamps are normalized to UTC so source offsets never leak
        // into storage and existing UTC data remains valid.
        public string StorageTimeZoneId { get; set; } = "Etc/UTC";

        // Product policy: all user-facing dates and scheduled-send input use
        // Beijing time, regardless of the computer/browser timezone.
        public string DisplayTimeZoneId { get; set; } = "Asia/Shanghai";
    }
}
