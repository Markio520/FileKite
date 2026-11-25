using System.Security.Cryptography;
using System.Text.Json;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.Search;
using FileAssistant.Infrastructure.Persistence;

namespace FileAssistant.Infrastructure.Tests;

public sealed class FileCollectionStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FileAssistant-collection-test-" + Guid.NewGuid().ToString("N"));
    public FileCollectionStoreTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task Older_rows_with_display_fields_round_trip_using_only_their_source_metadata()
    {
        var source = Path.Combine(root, "样本.txt");
        await File.WriteAllTextAsync(source, "abcd");
        var collection = new FileCollection();
        collection.AddFiles(default, [new(source, false, 4, null)]);
        var target = Path.Combine(root, "staging.json");
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(collection.Snapshot()));
        var store = new JsonFileCollectionStore(target);
        var loaded = (await store.LoadAsync(default)).Snapshot!;
        await store.SaveAsync(loaded, default);
        using var compact = JsonDocument.Parse(await File.ReadAllTextAsync(target));
        var file = compact.RootElement.GetProperty("Entries")[0].GetProperty("File");
        Assert.False(file.TryGetProperty("Directory", out _));
        Assert.False(file.TryGetProperty("Name", out _));
        Assert.False(file.TryGetProperty("Extension", out _));
        Assert.Equal(loaded.Entries[0], (await store.LoadAsync(default)).Snapshot!.Entries[0]);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-32BE")]
    public async Task Restore_accepts_previously_supported_unicode_byte_order_marks(string encoding)
    {
        var target = Path.Combine(root, "old.json");
        var collection = new FileCollection();
        var folder = collection.CreateFolder(default, "原分组");
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(collection.Snapshot(folder)), System.Text.Encoding.GetEncoding(encoding));
        var result = await new JsonFileCollectionStore(target).LoadAsync(default);
        Assert.Equal(folder, result.Snapshot!.CurrentFolderId);
        Assert.Equal("原分组", Assert.Single(result.Snapshot.Folders).Name);
    }

    [Fact]
    public async Task Restoring_rechecks_the_current_type_of_a_replaced_file_and_folder()
    {
        var nowFolder = Directory.CreateDirectory(Path.Combine(root, "was-a-file")).FullName;
        var nowFile = Path.Combine(root, "was-a-folder");
        await File.WriteAllTextAsync(nowFile, "abc");
        var collection = new FileCollection();
        collection.AddFiles(default, [new(nowFolder, false, 9, null), new(nowFile, true, null, null)]);
        var store = new JsonFileCollectionStore(Path.Combine(root, "staging.json"));
        await store.SaveAsync(collection.Snapshot(), default);

        var loaded = await store.LoadAsync(default);
        Assert.Null(loaded.Message);
        var restored = loaded.Snapshot!.Entries;
        Assert.All(restored, entry => Assert.Null(entry.Error));
        Assert.True(restored[0].File.IsFolder);
        Assert.Null(restored[0].File.Size);
        Assert.False(restored[1].File.IsFolder);
        Assert.Equal(3L, restored[1].File.Size);
    }

    [Fact]
    public async Task Real_references_restore_metadata_and_missing_paths_remain_visible_without_touching_sources()
    {
        var source = Path.Combine(root, "原件.txt"); await File.WriteAllTextAsync(source, "original 中文");
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var collection = new FileCollection(); var folder = collection.CreateFolder(default, "软件文件夹");
        collection.AddFiles(folder, [new(source, false, 0, null), new(Path.Combine(root, "missing.png"), false, 100, null)]);
        var store = new JsonFileCollectionStore(Path.Combine(root, "saved", "staging.json"));
        await store.SaveAsync(collection.Snapshot(folder), default);
        var result = await store.LoadAsync(default); var snapshot = result.Snapshot!;
        Assert.Equal(folder, snapshot.CurrentFolderId); Assert.Equal(2, snapshot.Entries.Count);
        Assert.NotNull(Assert.Single(snapshot.Entries, entry => entry.File.Name == "missing.png").Error);
        Assert.Equal(new FileInfo(source).Length, Assert.Single(snapshot.Entries, entry => entry.File.Name == "原件.txt").File.Size);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.False(Directory.Exists(Path.Combine(root, "软件文件夹")));
    }
    [Fact]
    public async Task Damaged_data_is_backed_up_before_a_fresh_collection_can_be_saved()
    {
        var path = Path.Combine(root, "state.json"); await File.WriteAllTextAsync(path, "{invalid");
        var store = new JsonFileCollectionStore(path); var loaded = await store.LoadAsync(default);
        Assert.Null(loaded.Snapshot); Assert.True(loaded.CanSave); Assert.Contains("备份", loaded.Message);
        var backup = Assert.Single(Directory.GetFiles(root, "*.bak")); Assert.Equal("{invalid", await File.ReadAllTextAsync(backup));
        await store.SaveAsync(new FileCollection().Snapshot(), default);
        Assert.Equal("{invalid", await File.ReadAllTextAsync(backup));
        Assert.NotNull((await store.LoadAsync(default)).Snapshot);
    }
    [Fact]
    public async Task Unknown_version_is_preserved_and_cyclic_data_is_rejected()
    {
        var path = Path.Combine(root, "state.json"); var text = JsonSerializer.Serialize(new FileCollectionSnapshot(2, [], []));
        await File.WriteAllTextAsync(path, text); var store = new JsonFileCollectionStore(path);
        Assert.False((await store.LoadAsync(default)).CanSave); Assert.Equal(text, await File.ReadAllTextAsync(path));
        var id = Guid.NewGuid(); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new FileCollectionSnapshot(1, [new(id, id, "坏目录")], [])));
        var invalid = await store.LoadAsync(default); Assert.Null(invalid.Snapshot); Assert.True(invalid.CanSave); Assert.Single(Directory.GetFiles(root, "*.bak"));
    }
    [Fact]
    public async Task Cancellation_and_write_failures_clean_up_temporary_files_and_release_the_store()
    {
        var path = Path.Combine(root, "state.json"); var store = new JsonFileCollectionStore(path); var collection = new FileCollection(); collection.CreateFolder(default, "A");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(collection.Snapshot(), cancellation.Token));
        Assert.False(File.Exists(path));
        await store.SaveAsync(collection.Snapshot(), default);
        Assert.Single((await store.LoadAsync(default)).Snapshot!.Folders);
        var blocked = Path.Combine(root, "blocked.json"); Directory.CreateDirectory(blocked);
        var failing = new JsonFileCollectionStore(blocked);
        var error = await Record.ExceptionAsync(() => failing.SaveAsync(collection.Snapshot(), default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(root, "*.tmp")); Assert.True(File.Exists(path));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
