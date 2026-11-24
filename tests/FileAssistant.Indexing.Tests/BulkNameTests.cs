using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FileAssistant.Indexing.Tests;

public sealed class BulkNameTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("FileKite-BulkNames-").FullName;
    public void Dispose() => Directory.Delete(temp, true);

    [Fact]
    public void WideWalkCanStopAtThePreviewAndRebuildWithOneInaccessibleBranch()
    {
        var root = Directory.CreateDirectory(Path.Join(temp, "wide")).FullName;
        var refused = Directory.CreateDirectory(Path.Join(root, "private"));
        File.WriteAllText(Path.Join(refused.FullName, "unreadable.txt"), "");
        for (int d = 0; d < 8; d++)
        {
            var path = Directory.CreateDirectory(Path.Join(root, $"branch-{d}", "deep")).FullName;
            for (int i = 0; i < 132; i++) File.WriteAllText(Path.Join(path, $"item-{i:D3}.txt"), "");
        }
        using var identity = WindowsIdentity.GetCurrent();
        var rule = new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var permissions = refused.GetAccessControl(AccessControlSections.Access);
        permissions.AddAccessRule(rule);
        refused.SetAccessControl(permissions);
        try
        {
            var cache = Path.Join(temp, "db");
            var catalog = new IndexCatalog(cache);
            var store = catalog.ForRoot(root);
            using var cancel = new CancellationTokenSource();
            Assert.ThrowsAny<OperationCanceledException>(() => store.PublishSearchNames(cache, cancel.Token, () =>
            {
                var preview = IndexedFileSearch.Query([store], new(), default);
                Assert.True(preview.IsPartialIndex);
                Assert.Equal(1024, preview.Total);
                cancel.Cancel();
            }));
            Assert.True(catalog.List().Single().IsBuildingNames);
            store = catalog.ForRoot(root);
            Assert.True(store.PublishSearchNames(cache, default));
            var info = catalog.List().Single();
            Assert.False(info.IsBuildingNames);
            Assert.Equal(1, info.IssueCounts[SpaceIssueKind.Access]);
            Assert.Equal(1056, info.Files);
            var results = IndexedFileSearch.Query([store], new() { Text = "ext:txt", PageSize = 1200 }, default);
            Assert.Equal(1056, results.Items.Count);
            Assert.Equal(1056, results.Items.Select(item => item.FullPath).Distinct().Count());
            Assert.All(results.Items, item => Assert.True(File.Exists(item.FullPath)));
            Assert.Equal(refused.FullName, Assert.Single(IndexedFileSearch.Query([store], new() { Text = "private" }, default).Items).FullPath);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM (SELECT parent FROM entries WHERE id>0 GROUP BY parent HAVING max(id)-min(id)+1<>count(*) OR min(id)<=parent)";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        finally
        {
            var restored = refused.GetAccessControl(AccessControlSections.Access);
            restored.RemoveAccessRuleSpecific(rule);
            refused.SetAccessControl(restored);
        }
    }

    [Fact]
    public void FirstPagesAndDirectoryRangesAgreeWithTheFilesystemAcrossVisibilityPolicies()
    {
        var root = Directory.CreateDirectory(Path.Join(temp, "root")).FullName;
        var drop = Directory.CreateDirectory(Path.Join(root, "omitted")).FullName;
        var nested = Directory.CreateDirectory(Path.Join(drop, "nested")).FullName;
        var hidden = Directory.CreateDirectory(Path.Join(root, "private")).FullName;
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        for (int i = 0; i < 1100; i++) File.WriteAllText(Path.Join(nested, $"!{i:D4}.txt"), "");
        foreach (var directory in new[] { root, drop, nested, hidden })
            foreach (var name in new[] { "合同报告.txt", "δelta-😀.TXT", "mixİ.txt", "prefix.txt", "somÉthing.txt" })
                File.WriteAllText(Path.Join(directory, name), directory);
        var system = Path.Join(root, "δelta-😀.TXT");
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        var cache = Path.Join(temp, "db");
        var store = new IndexCatalog(cache).ForRoot(root);
        Assert.True(store.PublishSearchNames(cache, default));
        var otherReader = new IndexCatalog(cache).ForRoot(root);
        var files = Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = 0
        }).Prepend(root).Select(path =>
        {
            bool folder = Directory.Exists(path);
            return new SearchFile(path, folder, folder ? null : new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
        }).ToList();

        var visible = new SearchRequest { ExcludedDirectories = ["OMITTED", nested.Replace('\\', '/')], ShowHidden = false, PinyinSearch = true };
        foreach (var request in new[] {
            new SearchRequest { Offset = 934, PageSize = 122 }, new SearchRequest { Offset = 1024, PageSize = 50 },
            visible, visible with { Offset = 2, PageSize = 2 }, visible with { Text = "htbg" },
            visible with { Text = "delta | 合同", Sort = SearchSort.Path, Descending = true },
            new SearchRequest { Text = "!01", Offset = 1, PageSize = 3 },
            new SearchRequest { ExcludedDirectories = [nested], Sort = SearchSort.Size, Descending = true },
            new SearchRequest { ExcludedDirectories = [root], PageSize = 1 },
            new SearchRequest { Text = "prefix", Offset = 1, PageSize = 0 },
            new SearchRequest { Text = "δelta", PinyinSearch = true, Sort = SearchSort.Modified },
        })
        {
            var excluded = new SearchDirectoryFilter(request.ExcludedDirectories);
            bool Allows(SearchFile file) => !excluded.Excludes(file.IsFolder ? file.FullPath : file.Directory) &&
                (request.ShowHidden || !IndexStore.Within(file.FullPath, hidden) && file.FullPath != system);
            var expected = SearchRules.Filter(files, request, "", visible: Allows);
            foreach (var reopened in new[] { store, otherReader })
            {
                var actual = IndexedFileSearch.Query([reopened], request, default);
                Assert.Equal(expected.Total, actual.Total);
                Assert.Equal(expected.Items.ToArray(), actual.Items.ToArray());
            }
        }
    }

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    public void BulkWriterPublishesExactlyTheCommittedPrefixAndKeepsInvariantKeys(int files, bool expectsPreview)
    {
        var root = Directory.CreateDirectory(Path.Join(temp, "mixed")).FullName;
        for (int i = 0; i < files; i++)
            File.WriteAllText(Path.Join(root, $"{i:D4}-{(i % 2 == 0 ? "δelTa𠀀" : "aScIi")}.txt"), "");
        var cache = Path.Join(temp, "db");
        var store = new IndexCatalog(cache).ForRoot(root);
        int previews = 0;
        Assert.True(store.PublishSearchNames(cache, default, () =>
        {
            previews++;
            var preview = IndexedFileSearch.Query([store], new(), default);
            Assert.True(preview.IsPartialIndex);
            Assert.Equal(1024, preview.Total);
        }));
        Assert.Equal(expectsPreview ? 1 : 0, previews);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT id,name,name_key,kind FROM entries ORDER BY id";
        using var rows = query.ExecuteReader();
        int count = 0;
        while (rows.Read())
        {
            Assert.Equal(count, rows.GetInt32(0));
            Assert.Equal(rows.GetString(1).ToUpperInvariant(), rows.GetString(2));
            Assert.Equal(count == 0 ? 1 : 0, rows.GetInt32(3));
            count++;
        }
        Assert.Equal(files + 1, count);
    }
}
