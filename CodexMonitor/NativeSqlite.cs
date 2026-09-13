using System.IO;
using System.Runtime.InteropServices;

namespace FullStackLauncher.CodexMonitor;

/// <summary>Small read-only adapter for the SQLite library supplied with Windows.</summary>
internal sealed class NativeSqlite : IDisposable
{
    private IntPtr _database;
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;

    public NativeSqlite(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("Codex local status data is unavailable. Open Codex and try again.");

        // READONLY: SQLite must never create, migrate, or write Codex's databases.
        var result = sqlite3_open_v2(path, out _database, 0x00000001, IntPtr.Zero);
        if (result != Ok)
        {
            Dispose();
            throw ReadError();
        }

        sqlite3_busy_timeout(_database, 1000);
    }

    public IReadOnlyList<string?[]> Query(string sql)
    {
        if (_database == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeSqlite));

        IntPtr statement = IntPtr.Zero;
        try
        {
            if (sqlite3_prepare_v2(_database, sql, -1, out statement, IntPtr.Zero) != Ok)
                throw ReadError();

            var rows = new List<string?[]>();
            var columnCount = sqlite3_column_count(statement);
            while (true)
            {
                var result = sqlite3_step(statement);
                if (result == Done)
                    return rows;
                if (result != Row || rows.Count >= 50000)
                    throw ReadError();

                var values = new string?[columnCount];
                for (var index = 0; index < columnCount; index++)
                {
                    var pointer = sqlite3_column_text(statement, index);
                    var length = sqlite3_column_bytes(statement, index);
                    if (length > 32768)
                        throw ReadError();
                    values[index] = pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer, length);
                }

                rows.Add(values);
            }
        }
        finally
        {
            if (statement != IntPtr.Zero)
                sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (_database == IntPtr.Zero)
            return;
        sqlite3_close_v2(_database);
        _database = IntPtr.Zero;
    }

    private static InvalidOperationException ReadError() => new(
        "Codex status could not be read safely. Its local data may be busy or its format may have changed; completion is unconfirmed.");

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
}
