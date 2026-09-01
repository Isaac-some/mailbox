namespace MailArchiver.Models
{
    public class CsvImportOptions
    {
        public const string CsvImport = "CsvImport";
        // Zero means no artificial account-count cap. Uploads are still bounded
        // by MaxFileSizeBytes; database lookups are chunked to keep query
        // parameters bounded even when a file contains many rows.
        public int MaxRows { get; set; } = 0;
        public long MaxFileSizeBytes { get; set; } = 10_000_000;
    }
}
