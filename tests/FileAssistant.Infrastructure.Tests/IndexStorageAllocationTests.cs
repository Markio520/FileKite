using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.Persistence;

namespace FileAssistant.Infrastructure.Tests;

public sealed class IndexStorageAllocationTests : IDisposable
{
    private static readonly string DatabaseName = new string('A', 64) + ".sqlite3";
    private readonly string root = Directory.CreateTempSubdirectory("FileKite-index-allocation-").FullName;
    private string Indexes => Path.Join(root, "Indexes");
    private string Database => Path.Join(Indexes, DatabaseName);

    [Fact]
    public async Task Compressed_database_keeps_identical_bytes_and_supports_wal_checkpoint_and_reopen()
    {
        if (!HasNtfsCompression()) return;
        Directory.CreateDirectory(Indexes);
        Sql(Database, "PRAGMA application_id=0x46414958; CREATE TABLE docs(id INTEGER PRIMARY KEY, data BLOB NOT NULL); INSERT INTO docs VALUES(1,zeroblob(524288));");
        var original = SpaceFileMetadata.Read(Database);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(Database));
        var directoryAttributes = File.GetAttributes(root);
        Assert.True(IndexStorageAllocation.TryEnableCompressionForNewFiles(Indexes));
        Assert.Equal(directoryAttributes, File.GetAttributes(root));
        Assert.Equal(original.Stamp.Attributes, File.GetAttributes(Database));
        Assert.True(IndexStorageAllocation.TryCompressDatabase(Database));
        Assert.True(IndexStorageAllocation.TryCompressDatabase(Database));
        Assert.True(File.GetAttributes(Database).HasFlag(FileAttributes.Compressed));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Database)));
        var compressed = SpaceFileMetadata.Read(Database);
        Assert.Equal(original.Length, compressed.Length);
        Assert.True(compressed.Allocation < original.Length);
        var usage = await ProfileStorage.MeasureAsync(root, default);
        Assert.Equal(compressed.Allocation, usage.IndexBytes);
        Assert.Equal(original.Length, usage.IndexLogicalBytes);

        string? inserted = Sql(Database, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO docs VALUES(2,zeroblob(64)); SELECT count(*) FROM docs;", () =>
        {
            Assert.True(File.GetAttributes(Database + "-wal").HasFlag(FileAttributes.Compressed));
            Assert.True(File.GetAttributes(Database + "-shm").HasFlag(FileAttributes.Compressed));
            Assert.False(IndexStorageAllocation.TryCompressDatabase(Database));
        });
        Assert.Equal("2", inserted);
        Assert.Equal("524352", Sql(Database, "SELECT sum(length(data)) FROM docs;"));
        Assert.Equal("ok", Sql(Database, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void Occupied_readonly_foreign_and_multiply_linked_databases_are_left_alone()
    {
        if (!HasNtfsCompression()) return;
        Directory.CreateDirectory(Indexes);
        Sql(Database, "CREATE TABLE sample(value INTEGER);");
        Assert.False(IndexStorageAllocation.TryCompressDatabase(Database));
        Sql(Database, "PRAGMA application_id=0x46414958;");
        var attributes = File.GetAttributes(Database);
        using (File.Open(Database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.False(IndexStorageAllocation.TryCompressDatabase(Database));
        File.SetAttributes(Database, attributes | FileAttributes.ReadOnly);
        try { Assert.False(IndexStorageAllocation.TryCompressDatabase(Database)); }
        finally { File.SetAttributes(Database, attributes); }
        Assert.True(CreateHardLinkW(Path.Join(root, "outside-indexes.db"), Database, 0));
        Assert.False(IndexStorageAllocation.TryCompressDatabase(Database));
        Assert.Equal(attributes, File.GetAttributes(Database));
        Assert.Equal("ok", Sql(Database, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task A_second_name_in_the_profile_does_not_hide_or_duplicate_index_allocation()
    {
        if (!new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Indexes);
        File.WriteAllBytes(Database, new byte[8193]);
        Assert.True(CreateHardLinkW(Path.Join(root, "alias.db"), Database, 0));
        var usage = await ProfileStorage.MeasureAsync(root, default);
        Assert.Equal(SpaceFileMetadata.Read(Database).Allocation, usage.IndexBytes);
        Assert.Equal(8193, usage.IndexLogicalBytes);
        Assert.Equal(0, usage.OtherBytes);
        Assert.Null(usage.LegacySearchBytes);

        string legacy = Directory.CreateDirectory(Path.Join(root, "Search")).FullName;
        Assert.True(CreateHardLinkW(Path.Join(legacy, "Everything.db"), Database, 0));
        var onlyShared = await ProfileStorage.MeasureAsync(root, default);
        Assert.Equal(usage.IndexBytes, onlyShared.IndexBytes);
        Assert.Equal(0L, onlyShared.LegacySearchBytes);

        string ini = Path.Join(legacy, "Everything.ini");
        File.WriteAllBytes(ini, new byte[9000]);
        Assert.True(CreateHardLinkW(Path.Join(root, "legacy-alias.ini"), ini, 0));
        var mixed = await ProfileStorage.MeasureAsync(root, default);
        Assert.Equal(usage.IndexBytes, mixed.IndexBytes);
        Assert.Equal(usage.IndexLogicalBytes, mixed.IndexLogicalBytes);
        Assert.Equal(SpaceFileMetadata.Read(ini).Allocation, mixed.LegacySearchBytes);
        Assert.Equal(0, mixed.OtherBytes);
    }

    [Fact]
    public async Task A_junction_to_the_profile_cannot_redirect_compression_or_storage_measurement()
    {
        if (!HasNtfsCompression()) return;
        var target = Directory.CreateDirectory(Path.Join(root, "actual", "Indexes")).FullName;
        var path = Path.Join(target, DatabaseName);
        Sql(path, "PRAGMA application_id=0x46414958; CREATE TABLE sample(value INTEGER);");
        var attributes = File.GetAttributes(path);
        var directoryAttributes = File.GetAttributes(target);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c mklink /j linked actual")
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var linked = Path.Join(root, "linked");
        try
        {
            Assert.False(IndexStorageAllocation.TryEnableCompressionForNewFiles(Path.Join(linked, "Indexes")));
            Assert.False(IndexStorageAllocation.TryCompressDatabase(Path.Join(linked, "Indexes", DatabaseName)));
            Assert.Equal(directoryAttributes, File.GetAttributes(target));
            Assert.Equal(attributes, File.GetAttributes(path));
            await Assert.ThrowsAnyAsync<IOException>(() => ProfileStorage.MeasureAsync(linked, default));
        }
        finally { Directory.Delete(linked); }
    }

    [Fact]
    public void Only_local_dedicated_index_directories_and_database_names_are_accepted()
    {
        Directory.CreateDirectory(Indexes);
        Assert.False(IndexStorageAllocation.TryEnableCompressionForNewFiles(root));
        Assert.False(IndexStorageAllocation.TryEnableCompressionForNewFiles(Path.GetPathRoot(root)!));
        Assert.False(IndexStorageAllocation.TryEnableCompressionForNewFiles(@"\\unused.invalid\share\Indexes"));
        Assert.False(IndexStorageAllocation.TryCompressDatabase("invalid\0path"));
        Assert.False(IndexStorageAllocation.TryEnableCompressionForNewFiles(Path.Join(root, "absent", "Indexes")));
        Assert.False(IndexStorageAllocation.TryCompressDatabase(Path.Join(Indexes, "settings.json")));
    }

    private bool HasNtfsCompression()
    {
        var drive = Path.GetPathRoot(root)!;
        return new DriveInfo(drive).DriveFormat == "NTFS" &&
            GetVolumeInformationW(drive, 0, 0, out _, out _, out var flags, 0, 0) && (flags & 0x10) != 0;
    }

    // Windows ships this SQLite runtime. Keeping the test here avoids an Indexing dependency in Infrastructure.
    private static string? Sql(string path, string statement, Action? beforeClose = null)
    {
        Assert.Equal(0, sqlite3_open16(path, out var connection));
        try
        {
            string? result = null;
            var status = sqlite3_exec(connection, statement, (_, _, values, _) =>
            {
                result = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(values));
                return 0;
            }, 0, 0);
            Assert.True(status == 0, Marshal.PtrToStringUTF8(sqlite3_errmsg(connection)));
            beforeClose?.Invoke();
            return result;
        }
        finally { Assert.Equal(0, sqlite3_close(connection)); }
    }

    public void Dispose() => Directory.Delete(root, true);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqlRow(nint context, int columns, nint values, nint names);
    [DllImport("winsqlite3.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open16(string file, out nint connection);
    [DllImport("winsqlite3.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(nint connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string statement, SqlRow callback, nint context, nint error);
    [DllImport("winsqlite3.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_errmsg(nint connection);
    [DllImport("winsqlite3.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(nint connection);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetVolumeInformationW(string root, nint name, uint nameSize, out uint serial,
        out uint maxLength, out uint flags, nint fileSystem, uint fileSystemSize);
}
