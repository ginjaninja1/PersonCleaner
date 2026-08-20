using MediaBrowser.Common.Configuration;
using SQLitePCL.pretty;
using System;
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

        public static void RequireExisting(string path)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException("PersonCleaner archive does not exist at " + path + ". Create or migrate it offline before starting Emby.");
        }

        public static void ValidateObjects(IDatabaseConnection db, string component, params string[] names)
        {
            foreach (var name in names)
            {
                using (var statement = db.PrepareStatement("SELECT 1 FROM sqlite_master WHERE name=@name AND type IN ('table','view') LIMIT 1"))
                {
                    statement.TryBind("@name", name);
                    var found = false;
                    foreach (var ignored in statement.ExecuteQuery()) { found = true; break; }
                    if (!found) throw OfflineMigrationRequired(component + " object is missing: " + name);
                }
            }
        }

        public static void ValidateVersion(IDatabaseConnection db, string component, string table, int expected)
        {
            using (var statement = db.PrepareStatement("SELECT version FROM " + table + " LIMIT 1"))
            {
                foreach (var row in statement.ExecuteQuery())
                {
                    var actual = row.GetInt(0);
                    if (actual != expected) throw OfflineMigrationRequired(component + " schema version is " + actual + "; expected " + expected);
                    return;
                }
            }
            throw OfflineMigrationRequired(component + " schema version is missing");
        }

        public static void ValidateMigrations(IDatabaseConnection db, int requiredThrough)
        {
            for (var version = 1; version <= requiredThrough; version++)
            {
                using (var statement = db.PrepareStatement("SELECT 1 FROM archive_schema_migration WHERE version=@version LIMIT 1"))
                {
                    statement.TryBind("@version", version);
                    var found = false;
                    foreach (var ignored in statement.ExecuteQuery()) { found = true; break; }
                    if (!found) throw OfflineMigrationRequired("archive migration " + version + " has not been applied");
                }
            }
        }

        private static InvalidOperationException OfflineMigrationRequired(string detail) =>
            new InvalidOperationException("PersonCleaner archive schema validation failed: " + detail + ". Stop Emby and apply the schema change offline.");
    }
}
