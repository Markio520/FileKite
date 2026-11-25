using System.Text.Json;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.Infrastructure.Persistence;

public sealed class JsonFileCollectionStore(string path) : IFileCollectionStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    public async Task<CollectionLoadResult> LoadAsync(CancellationToken token)
    {
        FileCollection collection;
        Guid currentFolder;
        try
        {
            var snapshot = await ReadSnapshotAsync(token).ConfigureAwait(false) ?? throw new JsonException();
            if (snapshot.Version != 1) return new(null, "保存的文件夹来自其他版本，现有记录已保留，本次暂停自动保存。", false);
            collection = FileCollection.Restore(snapshot);
            currentFolder = snapshot.CurrentFolderId;
        }
        catch (FileNotFoundException) { return new(null); }
        catch (DirectoryNotFoundException) { return new(null); }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        {
            try
            {
                File.Copy(path, path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak", overwrite: false);
                return new(null, "保存的文件夹数据损坏，已保留备份，可继续建立新的文件夹。");
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            { return new(null, "文件夹数据读取失败，原记录已保留，本次暂停自动保存。", false); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(null, "无法读取保存的文件夹，原记录已保留，本次暂停自动保存。", false); }
        return await Task.Run(() =>
        {
            var updates = new Dictionary<string, (SearchFile, string?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in collection.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (updates.ContainsKey(entry.File.FullPath)) continue;
                try
                {
                    var item = new FileInfo(entry.File.FullPath);
                    item.Refresh();
                    var attributes = item.Attributes;
                    if (attributes == (FileAttributes)(-1))
                        updates.Add(entry.File.FullPath, (entry.File, "失效：文件已移动、删除或当前无法访问。"));
                    else
                    {
                        bool isFolder = (attributes & FileAttributes.Directory) != 0;
                        var current = new SearchFile(entry.File.FullPath, isFolder, isFolder ? null : item.Length, item.LastWriteTimeUtc)
                        {
                            IsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0,
                            IsHiddenOrSystem = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                        };
                        updates.Add(entry.File.FullPath, (current, null));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { updates.Add(entry.File.FullPath, (entry.File, "失效：当前无法访问此路径。")); }
            }
            collection.UpdateReferences(updates);
            var missing = updates.Values.Count(update => update.Item2 is not null);
            return new CollectionLoadResult(collection.Snapshot(currentFolder), missing > 0 ? $"已恢复文件夹；{missing:N0} 个路径失效，引用保留。" : null);
        }, token).ConfigureAwait(false);
    }
    private async Task<FileCollectionSnapshot?> ReadSnapshotAsync(CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var first = new byte[1];
        await input.ReadAsync(first, token).ConfigureAwait(false);
        input.Position = 0;
        // Older hand-restored JSON may have a UTF-16 or UTF-32 BOM. Saved collections use UTF-8.
        if (first[0] is 0xff or 0xfe or 0x00)
        {
            using var reader = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
            return JsonSerializer.Deserialize<FileCollectionSnapshot>(await reader.ReadToEndAsync(token).ConfigureAwait(false), Options);
        }
        return await JsonSerializer.DeserializeAsync<FileCollectionSnapshot>(input, Options, token).ConfigureAwait(false);
    }

    public async Task SaveAsync(FileCollectionSnapshot snapshot, CancellationToken token)
    {
        FileCollection.Restore(snapshot);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(output, snapshot, Options, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}
