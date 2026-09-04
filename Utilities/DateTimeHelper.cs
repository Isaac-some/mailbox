using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Utilities
{
    public class DateTimeHelper
    {
        private readonly TimeZoneInfo _storageTimeZone;
        private readonly TimeZoneInfo _displayTimeZone;

        public DateTimeHelper(IOptions<TimeZoneOptions> timeZoneOptions)
        {
            _storageTimeZone = ResolveTimeZone(timeZoneOptions.Value.StorageTimeZoneId, TimeZoneInfo.Utc);
            var beijingFallback = TimeZoneInfo.CreateCustomTimeZone(
                "Asia/Shanghai",
                TimeSpan.FromHours(8),
                "Beijing Time",
                "Beijing Time");
            _displayTimeZone = ResolveTimeZone(
                timeZoneOptions.Value.DisplayTimeZoneId,
                beijingFallback);
        }

        /// <summary>
        /// Converts a DateTimeOffset from any timezone to the normalized archive timezone.
        /// </summary>
        /// <param name="dateTimeOffset">The DateTimeOffset to convert</param>
        /// <returns>DateTime in the configured archive timezone</returns>
        public DateTime ConvertToDisplayTimeZone(DateTimeOffset dateTimeOffset)
        {
            return TimeZoneInfo.ConvertTime(dateTimeOffset, _storageTimeZone).DateTime;
        }

        /// <summary>
        /// Converts a DateTime to the normalized archive timezone (assumes an unspecified value is already normalized).
        /// </summary>
        /// <param name="dateTime">The DateTime to convert</param>
        /// <returns>DateTime in the configured archive timezone</returns>
        public DateTime ConvertToDisplayTimeZone(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                return TimeZoneInfo.ConvertTimeFromUtc(dateTime, _storageTimeZone);
            }
            else if (dateTime.Kind == DateTimeKind.Local)
            {
                return TimeZoneInfo.ConvertTime(dateTime, _storageTimeZone);
            }
            else
            {
                // Unspecified - assume it's already in the correct timezone
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
            }
        }

        /// <summary>
        /// Converts an archived wall-clock value from the normalized storage
        /// timezone to the configured UI timezone (Beijing by default).
        /// </summary>
        public DateTime ConvertArchiveTimeToDisplayTimeZone(DateTime dateTime)
        {
            var utc = dateTime.Kind switch
            {
                DateTimeKind.Utc => dateTime,
                DateTimeKind.Local => dateTime.ToUniversalTime(),
                _ => TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
                    _storageTimeZone)
            };
            return TimeZoneInfo.ConvertTimeFromUtc(utc, _displayTimeZone);
        }

        public DateTime ConvertUtcToDisplayTime(DateTime dateTime)
            => TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(dateTime), _displayTimeZone);

        public DateTime ConvertDisplayInputToUtc(DateTime dateTime)
            => TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), _displayTimeZone);

        public static DateTime EnsureUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
                
            if (dateTime.Kind == DateTimeKind.Local)
                return dateTime.ToUniversalTime();
                
            return dateTime; // Already UTC
        }

        /// <summary>
        /// Builds a <see cref="DateTimeOffset"/> for a <see cref="DateTime"/> value that is
        /// stored in the configured archive timezone (e.g. <c>ArchivedEmail.SentDate</c> after
        /// it round-tripped through PostgreSQL <c>timestamp without time zone</c>, which strips
        /// the <see cref="DateTimeKind"/>). The returned offset is the archive timezone's UTC
        /// offset for the given instant, so that downstream consumers (e.g. MimeKit's
        /// <c>MimeMessage.Date</c>) emit a correct <c>Date:</c> header with the proper offset.
        /// </summary>
        /// <param name="dateTime">
        /// A <see cref="DateTime"/> interpreted as local time in the configured archive timezone.
        /// </param>
        /// <returns>
        /// A <see cref="DateTimeOffset"/> whose wall-clock time matches <paramref name="dateTime"/>
        /// and whose offset reflects the configured archive timezone.
        /// </returns>
        public DateTimeOffset ToDisplayTimeZoneOffset(DateTime dateTime)
        {
            var unspecified = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, _storageTimeZone.GetUtcOffset(unspecified));
        }

        /// <summary>
        /// Inverse of <see cref="ConvertToDisplayTimeZone(DateTime)"/>.
        /// Interprets a DateTime stored in the configured archive timezone (or with
        /// <see cref="DateTimeKind.Unspecified"/> because it has round-tripped through
        /// PostgreSQL, which strips the kind information for <c>timestamp without time zone</c>
        /// columns) and returns the equivalent UTC DateTime.
        /// Values explicitly marked as <see cref="DateTimeKind.Utc"/> are passed through
        /// unchanged; <see cref="DateTimeKind.Local"/> values are converted via the OS.
        /// </summary>
        /// <param name="dateTime">The DateTime value to convert</param>
        /// <returns>The equivalent UTC DateTime (Kind=Utc)</returns>
        public DateTime ConvertFromDisplayTimeZoneToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
                return dateTime;

            if (dateTime.Kind == DateTimeKind.Local)
                return dateTime.ToUniversalTime();

            // Unspecified - assume it is in the configured archive timezone
            var unspecified = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, _storageTimeZone);
        }

        private static TimeZoneInfo ResolveTimeZone(string? id, TimeZoneInfo fallback)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(
                    string.IsNullOrWhiteSpace(id) ? fallback.Id : id);
            }
            catch (TimeZoneNotFoundException)
            {
                return fallback;
            }
            catch (InvalidTimeZoneException)
            {
                return fallback;
            }
        }

    }
}
