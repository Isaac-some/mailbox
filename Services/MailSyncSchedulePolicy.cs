namespace MailArchiver.Services
{
    public static class MailSyncSchedulePolicy
    {
        private static readonly DateTime EpochUtc =
            new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static TimeSpan ResolveSyncInterval(
            int? accountIntervalMinutes,
            int? defaultIntervalSeconds,
            int legacyDefaultIntervalMinutes)
        {
            if (accountIntervalMinutes.HasValue)
            {
                return TimeSpan.FromMinutes(Math.Max(1, accountIntervalMinutes.Value));
            }

            if (defaultIntervalSeconds.HasValue)
            {
                return TimeSpan.FromSeconds(Math.Max(1, defaultIntervalSeconds.Value));
            }

            return TimeSpan.FromMinutes(Math.Max(1, legacyDefaultIntervalMinutes));
        }

        public static TimeSpan ResolvePollInterval(int configuredSeconds)
        {
            return TimeSpan.FromSeconds(Math.Max(1, configuredSeconds));
        }

        public static TimeSpan ResolveFailureRetryInterval(int configuredSeconds)
        {
            return TimeSpan.FromSeconds(Math.Max(30, configuredSeconds));
        }

        /// <summary>
        /// Produces a stable, evenly distributed startup offset. Account IDs are
        /// usually sequential, so using an ID modulo the period would create a
        /// burst immediately after a restart instead of spreading the work.
        /// </summary>
        public static TimeSpan ResolveStartupStagger(int accountId, int configuredSeconds)
        {
            var period = Math.Max(0, configuredSeconds);
            if (period == 0)
                return TimeSpan.Zero;

            unchecked
            {
                var hash = (uint)accountId;
                hash ^= hash >> 16;
                hash *= 0x7feb352d;
                hash ^= hash >> 15;
                hash *= 0x846ca68b;
                hash ^= hash >> 16;
                return TimeSpan.FromSeconds(hash % (uint)period);
            }
        }

        public static DateTime ResolveInitialNextRun(
            int accountId,
            DateTime lastSync,
            DateTime nowUtc,
            int startupStaggerSeconds)
        {
            if (lastSync <= EpochUtc)
                return nowUtc;

            return nowUtc.Add(ResolveStartupStagger(accountId, startupStaggerSeconds));
        }
    }
}
