using MediaBrowser.Common.Configuration;
using System.IO;

namespace PersonCleaner.Storage
{
    internal static class ArchiveDatabase
    {
        public const string FileName = "personcleaner-archive.db";
        private const string LegacyFileName = "tvdb-archive.db";
        public static string ResolvePath(IApplicationPaths paths)
        {
            var current = Path.Combine(paths.DataPath, FileName);
            var legacy = Path.Combine(paths.DataPath, LegacyFileName);
            // Never rename a SQLite database from plugin construction. Emby creates
            // several task instances concurrently, and SQLite/inspection tools may
            // legitimately hold the database or its WAL sidecars open. Prefer the
            // new name when it already exists; otherwise keep using the complete,
            // hard-won legacy archive in place.
            return File.Exists(current) ? current : File.Exists(legacy) ? legacy : current;
        }
    }
}
