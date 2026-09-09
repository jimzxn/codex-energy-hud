using System.Runtime.InteropServices;

namespace CodexHud.Core.Native;

/// <summary>Small read-only SQLite adapter; opens the original WAL-aware database without copying it.</summary>
internal sealed class SqliteReader : IDisposable
{
    private const string NativeLibraryName = "codexhud-sqlite";
    private IntPtr _database;
    private static readonly Lazy<IntPtr> SqliteLibrary = new(() =>
        NativeLibrary.Load(OperatingSystem.IsWindows() ? "winsqlite3.dll"
            : OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib"
            : throw new DllNotFoundException("SQLite is not configured for this operating system.")));

    static SqliteReader()
    {
        // One resolver for this assembly, scoped to SQLite. Other platform imports use .NET's
        // normal loader. The system library handle intentionally lives for the process lifetime.
        NativeLibrary.SetDllImportResolver(typeof(SqliteReader).Assembly,
            (name, _, _) => name == NativeLibraryName ? SqliteLibrary.Value : IntPtr.Zero);
    }
    private const int Row = 100;
    private const int Done = 101;

    public SqliteReader(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro";
        int result = sqlite3_open_v2(uri, out _database, 0x00000001 | 0x00000040 | 0x00008000, null);
        if (result != 0)
        {
            Dispose();
            throw new IOException($"SQLite read-only open failed ({result}).");
        }
        sqlite3_busy_timeout(_database, 150);
    }

    public HashSet<string> Columns(string table)
    {
        if (table.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Invalid table identifier.", nameof(table));
        return Query($"PRAGMA table_info({table})")
            .Select(row => row.Text("name"))
            .ToHashSet(StringComparer.Ordinal);
    }

    public List<SqliteRow> Query(string sql, params string[] parameters)
    {
        if (_database == IntPtr.Zero) throw new ObjectDisposedException(nameof(SqliteReader));
        int result = sqlite3_prepare_v2(_database, sql, -1, out var statement, IntPtr.Zero);
        if (result != 0) throw new IOException($"SQLite statement unavailable ({result}).");
        try
        {
            if (sqlite3_stmt_readonly(statement) != 1)
                throw new InvalidOperationException("Only read-only statements are allowed.");
            for (int i = 0; i < parameters.Length; i++)
            {
                result = sqlite3_bind_text(statement, i + 1, parameters[i], -1, new IntPtr(-1));
                if (result != 0) throw new IOException($"SQLite parameter failed ({result}).");
            }
            var rows = new List<SqliteRow>();
            int columnCount = sqlite3_column_count(statement);
            var names = Enumerable.Range(0, columnCount)
                .Select(i => Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i)) ?? "")
                .ToArray();
            while ((result = sqlite3_step(statement)) == Row)
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (int i = 0; i < columnCount; i++)
                    values[names[i]] = sqlite3_column_type(statement, i) == 5
                        ? null : Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(new SqliteRow(values));
            }
            if (result != Done) throw new IOException($"SQLite read interrupted ({result}).");
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }

    public void Dispose()
    {
        if (_database == IntPtr.Zero) return;
        sqlite3_close_v2(_database);
        _database = IntPtr.Zero;
    }

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out IntPtr database, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_stmt_readonly(IntPtr statement);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_name(IntPtr statement, int index);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int index);
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
}

internal sealed record SqliteRow(IReadOnlyDictionary<string, string?> Values)
{
    public string Text(string name) => Values.TryGetValue(name, out var value) ? value ?? "" : "";
    public long? Number(string name) => long.TryParse(Text(name), out long value) ? value : null;
}
