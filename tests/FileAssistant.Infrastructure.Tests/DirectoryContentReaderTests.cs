using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Tests;

public sealed class DirectoryContentReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FileAssistant-directory-content-tests", Guid.NewGuid().ToString("N"));
    private readonly DirectoryContentReader reader = new();
    public DirectoryContentReaderTests() => Directory.CreateDirectory(root);
    [Fact] public async Task Navigation_lists_only_direct_folders_and_reports_read_failures()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "资料"));
        Directory.CreateDirectory(Path.Combine(child.FullName, "下一级"));
        await File.WriteAllTextAsync(Path.Combine(root, "文件.txt"), "unchanged");
        var items = await reader.ReadFoldersAsync(root, CancellationToken.None);
        Assert.Equal(child.FullName, Assert.Single(items).FullPath);
        Assert.True(items[0].IsFolder);
        File.SetAttributes(child.FullName, child.Attributes | FileAttributes.Hidden);
        Assert.True(Assert.Single(await reader.ReadFoldersAsync(root, default)).IsHiddenOrSystem);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => reader.ReadFoldersAsync(Path.Combine(root, "不存在"), CancellationToken.None));
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadFoldersAsync(root, stopped.Token));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(root, "文件.txt")));
    }
    [Fact] public async Task Reads_only_immediate_contents_including_empty_folders_and_preserves_existing_files()
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "素材"));
        Directory.CreateDirectory(Path.Combine(root, "空目录"));
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "内部.txt"), "inside");
        var file = Path.Combine(root, "说明.txt"); await File.WriteAllTextAsync(file, "original");
        var items = await reader.ReadAsync(root, CancellationToken.None);
        Assert.Equal(3, items.Count); Assert.Equal(2, items.Count(x => x.IsFolder));
        Assert.DoesNotContain(items, x => x.FullPath.EndsWith("内部.txt"));
        Assert.Equal("original", await File.ReadAllTextAsync(file));
        Assert.True(items[0].IsFolder); Assert.False(items[^1].IsFolder);
    }
    [Fact] public async Task Missing_directory_and_cancellation_are_not_reported_as_empty_successes()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => reader.ReadAsync(Path.Combine(root, "不存在"), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(root, cancelled.Token));
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
