using SQLitePCL.pretty;
using System;
using System.Collections.Generic;

namespace PersonCleaner.Storage
{
    // Kept local because these helpers live in Emby.Sqlite.dll and are not part
    // of the public plugin reference pack. They use only the shipped pretty API.
    internal static class SqliteCompatibilityExtensions
    {
        public static void TryBind(this IStatement statement, string name, string value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value == null) p.BindNull(); else p.Bind(value); } }
        public static void TryBind(this IStatement statement, string name, long value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) p.Bind(value); }
        public static void TryBind(this IStatement statement, string name, long? value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value.HasValue) p.Bind(value.Value); else p.BindNull(); } }
        public static void TryBind(this IStatement statement, string name, int value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) p.Bind(value); }
        public static void TryBind(this IStatement statement, string name, int? value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value.HasValue) p.Bind(value.Value); else p.BindNull(); } }
        public static void TryBind(this IStatement statement, string name, double value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) p.Bind(value); }
        public static IEnumerable<IResultSet> ExecuteQuery(this IStatement statement)
        { while (statement.MoveNext()) yield return statement.Current; }
    }
}
