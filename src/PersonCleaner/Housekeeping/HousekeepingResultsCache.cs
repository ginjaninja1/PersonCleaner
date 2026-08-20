using System;

namespace PersonCleaner.Housekeeping
{
    internal static class HousekeepingResultsCache
    {
        private static HousekeepingResultRow[] rows = Array.Empty<HousekeepingResultRow>();
        public static HousekeepingResultRow[] Rows => rows;
        public static void Replace(HousekeepingResultRow[] value) => rows = value ?? Array.Empty<HousekeepingResultRow>();
    }
}
