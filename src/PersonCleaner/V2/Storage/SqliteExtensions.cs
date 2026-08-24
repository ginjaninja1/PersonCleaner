using SQLitePCL.pretty;
using System.Collections.Generic;

namespace PersonCleaner.V2.Storage
{
    internal static class SqliteExtensions
    {
        public static void Bind(this IStatement statement, string name, string value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value == null) p.BindNull(); else p.Bind(value); } }
        public static void Bind(this IStatement statement, string name, long value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) p.Bind(value); }
        public static void Bind(this IStatement statement, string name, long? value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value.HasValue) p.Bind(value.Value); else p.BindNull(); } }
        public static void Bind(this IStatement statement, string name, int value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) p.Bind(value); }
        public static void Bind(this IStatement statement, string name, int? value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value.HasValue) p.Bind(value.Value); else p.BindNull(); } }
        public static void Bind(this IStatement statement, string name, double value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) p.Bind(value); }
        public static void Bind(this IStatement statement, string name, double? value)
        { if (statement.BindParameters.TryGetValue(name, out var p)) { if (value.HasValue) p.Bind(value.Value); else p.BindNull(); } }
        public static IEnumerable<IResultSet> Rows(this IStatement statement)
        { while (statement.MoveNext()) yield return statement.Current; }
    }
}
