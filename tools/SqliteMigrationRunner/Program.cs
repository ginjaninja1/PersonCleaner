using System.Runtime.InteropServices;

if (args.Length != 2) throw new ArgumentException("Usage: SqliteMigrationRunner <database> <sql-file>");
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
    name == "emby-sqlite3" ? NativeLibrary.Load(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Emby-Server", "system", "sqlite3.dll")) : IntPtr.Zero);

var rc = Native.sqlite3_open_v2(args[0], out var db, 2, null);
if (rc != 0) throw new InvalidOperationException("SQLite open failed: " + rc);
try
{
    Execute(db, "PRAGMA busy_timeout=60000; PRAGMA wal_checkpoint(TRUNCATE);");
    Console.WriteLine("Applying " + Path.GetFileName(args[1]) + " with Emby's SQLite runtime...");
    var started = DateTime.UtcNow;
    Execute(db, File.ReadAllText(args[1]));
    Execute(db, "PRAGMA wal_checkpoint(TRUNCATE);");
    Console.WriteLine("Committed in " + (DateTime.UtcNow-started).TotalSeconds.ToString("0.0") + " seconds.");
}
finally { Native.sqlite3_close_v2(db); }

static void Execute(IntPtr db,string sql)
{
    var rc=Native.sqlite3_exec(db,sql,IntPtr.Zero,IntPtr.Zero,out var error);
    if(rc==0)return;
    var message=error==IntPtr.Zero ? "SQLite error "+rc : Marshal.PtrToStringUTF8(error);
    if(error!=IntPtr.Zero)Native.sqlite3_free(error);
    throw new InvalidOperationException(message);
}

internal static class Native
{
    [DllImport("emby-sqlite3",CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename,out IntPtr db,int flags,[MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);
    [DllImport("emby-sqlite3",CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,IntPtr callback,IntPtr context,out IntPtr error);
    [DllImport("emby-sqlite3",CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("emby-sqlite3",CallingConvention=CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr value);
}
