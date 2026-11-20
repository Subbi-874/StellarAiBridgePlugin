using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.AgyBridge.Bridge.Storage;

/// <summary>
/// Thin P/Invoke SQLite wrapper backed by the embedded x64 <c>e_sqlite3.dll</c> resource
/// (extracted atomically to <c>%LOCALAPPDATA%\StellarAgyBridge\native\</c>, with System32
/// <c>winsqlite3.dll</c> fallback) so zero loose native DLLs exist under <c>stellar/plugins</c>.
/// </summary>
public sealed class NativeSqliteDb : IDisposable
{
    public const int SqliteOk = 0;
    public const int SqliteRow = 100;
    public const int SqliteDone = 101;
    public const int SqliteNull = 5;

    private const int OpenReadWrite = 0x00000002;
    private const int OpenCreate = 0x00000004;
    private const int OpenFullMutex = 0x00010000;
    private const string EmbeddedNativeResource = "Stellar.AgyBridge.Native.e_sqlite3.dll";
    private const string SystemFallbackDll = @"C:\Windows\System32\winsqlite3.dll";
    private static readonly IntPtr SqliteTransient = new(-1);

    private static readonly object LoadLock = new();
    private static IntPtr _nativeHandle;
    private static string _loadedLibraryPath = "";
    private static bool _resolverRegistered;

    private IntPtr _db;
    private bool _disposed;

    public static string LoadedLibraryPath
    {
        get
        {
            EnsureLoaded();
            return _loadedLibraryPath;
        }
    }

    public static void EnsureLoaded()
    {
        if (_nativeHandle != IntPtr.Zero)
            return;

        lock (LoadLock)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeSqliteDb).Assembly, ResolveLibrary);
                _resolverRegistered = true;
            }
            if (_nativeHandle != IntPtr.Zero)
                return;

            try
            {
                string extractedPath = ExtractEmbeddedSqlite();
                _nativeHandle = NativeLibrary.Load(extractedPath);
                _loadedLibraryPath = extractedPath;
                return;
            }
            catch
            {
                if (File.Exists(SystemFallbackDll))
                {
                    _nativeHandle = NativeLibrary.Load(SystemFallbackDll);
                    _loadedLibraryPath = SystemFallbackDll;
                    return;
                }
                throw;
            }
        }
    }

    public static string LibVersion()
    {
        EnsureLoaded();
        IntPtr ptr = Native.sqlite3_libversion();
        return ptr == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(ptr) ?? "");
    }

    public NativeSqliteDb(string dbPath)
    {
        EnsureLoaded();
        string path = string.IsNullOrWhiteSpace(dbPath) ? ":memory:" : dbPath;
        if (!string.Equals(path, ":memory:", StringComparison.Ordinal))
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
        }

        byte[] pathUtf8 = ToNullTerminatedUtf8(path);
        int rc = Native.sqlite3_open_v2(pathUtf8, out _db, OpenReadWrite | OpenCreate | OpenFullMutex, IntPtr.Zero);
        if (rc != SqliteOk)
        {
            string msg = ReadErrorMessage(_db);
            if (_db != IntPtr.Zero)
            {
                Native.sqlite3_close_v2(_db);
                _db = IntPtr.Zero;
            }
            throw new InvalidOperationException($"sqlite3_open_v2 failed (rc={rc}): {msg}");
        }

        Native.sqlite3_busy_timeout(_db, 5000);
    }

    public void RunInTransaction(Action action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));
        ThrowIfDisposed();
        ExecuteScript("BEGIN IMMEDIATE;");
        try
        {
            action();
            ExecuteScript("COMMIT;");
        }
        catch
        {
            try
            {
                ExecuteScript("ROLLBACK;");
            }
            catch
            {
                // Best-effort rollback when unwinding an aborted transaction.
            }
            throw;
        }
    }

    public void ExecuteScript(string sql)
    {
        ThrowIfDisposed();
        byte[] utf8 = ToNullTerminatedUtf8(sql ?? "");
        int rc = Native.sqlite3_exec(_db, utf8, IntPtr.Zero, IntPtr.Zero, out IntPtr errPtr);
        if (rc != SqliteOk)
        {
            string err = errPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(errPtr) ?? "") : ReadErrorMessage(_db);
            if (errPtr != IntPtr.Zero)
                Native.sqlite3_free(errPtr);
            throw new InvalidOperationException($"sqlite3_exec failed (rc={rc}): {err}");
        }
    }

    public NativeSqliteStmt Prepare(string sql)
    {
        ThrowIfDisposed();
        byte[] utf8 = ToNullTerminatedUtf8(sql ?? "");
        int rc = Native.sqlite3_prepare_v2(_db, utf8, -1, out IntPtr stmt, out _);
        if (rc != SqliteOk || stmt == IntPtr.Zero)
            throw new InvalidOperationException($"sqlite3_prepare_v2 failed (rc={rc}): {ReadErrorMessage(_db)}");
        return new NativeSqliteStmt(this, stmt);
    }

    public int Changes()
    {
        ThrowIfDisposed();
        return Native.sqlite3_changes(_db);
    }

    public long LastInsertRowId()
    {
        ThrowIfDisposed();
        return Native.sqlite3_last_insert_rowid(_db);
    }

    internal string GetLastError() => ReadErrorMessage(_db);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_db != IntPtr.Zero)
        {
            Native.sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeSqliteDb() => Dispose();

    private void ThrowIfDisposed()
    {
        if (_disposed || _db == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeSqliteDb));
    }

    private static string ExtractEmbeddedSqlite()
    {
        byte[] bytes = EmbeddedAssets.ReadBytes(EmbeddedNativeResource);
        byte[] sha = SHA256.HashData(bytes);
        string hash8 = Convert.ToHexString(sha)[..8].ToLowerInvariant();

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApp))
            localApp = Path.GetTempPath();

        string nativeDir = Path.Combine(localApp, "StellarAgyBridge", "native");
        Directory.CreateDirectory(nativeDir);
        string destPath = Path.Combine(nativeDir, $"e_sqlite3_{hash8}.dll");

        if (File.Exists(destPath) && new FileInfo(destPath).Length == bytes.Length)
            return destPath;

        string tmpPath = Path.Combine(
            nativeDir,
            $"e_sqlite3_{hash8}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(tmpPath, bytes);
            try
            {
                File.Move(tmpPath, destPath, overwrite: false);
            }
            catch (IOException)
            {
                if (!File.Exists(destPath) || new FileInfo(destPath).Length != bytes.Length)
                    throw;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // Ignore temp cleanup races
            }
        }
        return destPath;
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Native.LibName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;
        EnsureLoaded();
        return _nativeHandle;
    }

    internal static byte[] ToNullTerminatedUtf8(string text)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        byte[] buf = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, buf, 0);
        return buf;
    }

    private static string ReadErrorMessage(IntPtr db)
    {
        if (db == IntPtr.Zero)
            return "unopened database handle";
        IntPtr ptr = Native.sqlite3_errmsg(db);
        return ptr == IntPtr.Zero ? "unknown sqlite error" : (Marshal.PtrToStringUTF8(ptr) ?? "");
    }

    internal static class Native
    {
        internal const string LibName = "e_sqlite3";

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_libversion();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db, byte[] sqlUtf8, IntPtr callback, IntPtr arg, out IntPtr errMsg);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_free(IntPtr ptr);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_changes(IntPtr db);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_last_insert_rowid(IntPtr db);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sqlUtf8, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr stmt);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_reset(IntPtr stmt);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_clear_bindings(IntPtr stmt);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] textUtf8, int nBytes, IntPtr destructor);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_null(IntPtr stmt, int index);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr stmt, int col);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr stmt, int col);
    }

    public sealed class NativeSqliteStmt : IDisposable
    {
        private readonly NativeSqliteDb _owner;
        private IntPtr _stmt;

        internal NativeSqliteStmt(NativeSqliteDb owner, IntPtr stmt)
        {
            _owner = owner;
            _stmt = stmt;
        }

        public void BindText(int index, string? value)
        {
            ThrowIfDisposed();
            if (value == null)
            {
                CheckRc(Native.sqlite3_bind_null(_stmt, index), nameof(Native.sqlite3_bind_null));
                return;
            }
            if (value.Length == 0)
            {
                // Pass a non-null 1-byte buffer with length 0 so SQLite binds empty string instead of SQL NULL.
                byte[] empty = new byte[1];
                CheckRc(Native.sqlite3_bind_text(_stmt, index, empty, 0, SqliteTransient), nameof(Native.sqlite3_bind_text));
                return;
            }
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            CheckRc(Native.sqlite3_bind_text(_stmt, index, utf8, utf8.Length, SqliteTransient), nameof(Native.sqlite3_bind_text));
        }

        public void BindInt64(int index, long value)
        {
            ThrowIfDisposed();
            CheckRc(Native.sqlite3_bind_int64(_stmt, index, value), nameof(Native.sqlite3_bind_int64));
        }

        public void BindNull(int index)
        {
            ThrowIfDisposed();
            CheckRc(Native.sqlite3_bind_null(_stmt, index), nameof(Native.sqlite3_bind_null));
        }

        public void Reset()
        {
            ThrowIfDisposed();
            CheckRc(Native.sqlite3_reset(_stmt), nameof(Native.sqlite3_reset));
            CheckRc(Native.sqlite3_clear_bindings(_stmt), nameof(Native.sqlite3_clear_bindings));
        }

        public bool StepRow()
        {
            ThrowIfDisposed();
            int rc = Native.sqlite3_step(_stmt);
            if (rc == SqliteRow)
                return true;
            if (rc == SqliteDone)
                return false;
            throw new InvalidOperationException($"sqlite3_step failed (rc={rc}): {_owner.GetLastError()}");
        }

        public void StepDone()
        {
            if (StepRow())
                throw new InvalidOperationException("Expected SQLite statement completion without rows.");
        }

        public bool IsNull(int col)
        {
            ThrowIfDisposed();
            return Native.sqlite3_column_type(_stmt, col) == SqliteNull;
        }

        public string GetText(int col)
        {
            ThrowIfDisposed();
            if (Native.sqlite3_column_type(_stmt, col) == SqliteNull)
                return "";
            IntPtr ptr = Native.sqlite3_column_text(_stmt, col);
            return ptr == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(ptr) ?? "");
        }

        public long GetInt64(int col)
        {
            ThrowIfDisposed();
            return Native.sqlite3_column_int64(_stmt, col);
        }

        public void Dispose()
        {
            if (_stmt != IntPtr.Zero)
            {
                Native.sqlite3_finalize(_stmt);
                _stmt = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_stmt == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeSqliteStmt));
        }

        private void CheckRc(int rc, string op)
        {
            if (rc != SqliteOk)
                throw new InvalidOperationException($"{op} failed (rc={rc}): {_owner.GetLastError()}");
        }
    }
}
