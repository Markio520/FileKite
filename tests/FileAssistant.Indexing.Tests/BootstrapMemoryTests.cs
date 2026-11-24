using FileAssistant.Core.Features.Search;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing.Tests;

public sealed class BootstrapMemoryTests
{
    [Fact]
    public void InterleavedSparseDirectoryIdsRetainTheirPathsTimesAndSortedPages()
    {
        string temp = Directory.CreateTempSubdirectory("FileKite-NameStream-").FullName;
        try
        {
            string root = Directory.CreateDirectory(Path.Join(temp, "扫描")).FullName;
            string cache = Path.Join(temp, "index");
            // Both root partitions see equal leaf spellings. Large gaps between directory
            // records cross several producer batches before those parents are looked up again.
            var expected = new HashSet<string>(StringComparer.Ordinal);
            for (int branch = 0; branch < 12; branch++)
            {
                string parent = Directory.CreateDirectory(Path.Join(root, $"branch-{branch:D2}")).FullName;
                expected.Add(parent);
                for (int sibling = 0; sibling < 7; sibling++)
                {
                    var child = Directory.CreateDirectory(Path.Join(parent, $"同名-δ-{sibling}")).FullName;
                    expected.Add(child);
                    var deep = Directory.CreateDirectory(Path.Join(child, "内层")).FullName;
                    expected.Add(deep);
                    string file = Path.Join(deep, "重复-µ.txt");
                    File.WriteAllText(file, "test");
                    expected.Add(file);
                }
                for (int file = 0; file < 89 + branch * 3; file++)
                {
                    var location = Path.Join(parent, $"asset-{file:D3}.bin");
                    File.WriteAllText(location, "");
                    expected.Add(location);
                }
            }

            var store = new IndexCatalog(cache).ForRoot(root);
            int previews = 0;
            Assert.True(store.PublishSearchNames(cache, default, () => previews++));
            Assert.Equal(1, previews);
            var seen = new List<string> { root };
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT e.id,e.parent,e.name,e.name_key,e.modified,d.path FROM entries e
                LEFT JOIN live_dirs d ON d.id=e.id WHERE e.id>0 ORDER BY e.id
                """;
            using (var rows = command.ExecuteReader())
                while (rows.Read())
                {
                    Assert.Equal(seen.Count, rows.GetInt32(0));
                    string name = rows.GetString(2);
                    string location = Path.Join(seen[rows.GetInt32(1)], name);
                    Assert.True(expected.Contains(location), location);
                    Assert.Equal(name.ToUpperInvariant(), rows.GetString(3));
                    if (!rows.IsDBNull(5))
                    {
                        Assert.Equal(location, rows.GetString(5));
                        Assert.Equal(Directory.GetLastWriteTimeUtc(location).ToFileTimeUtc(), rows.GetInt64(4));
                    }
                    seen.Add(location);
                }
            Assert.Equal(expected.Count, seen.Count - 1);
            Assert.True(expected.SetEquals(seen.Skip(1)));
            var matches = expected.Where(p => Path.GetFileName(p) == "重复-µ.txt").Order(StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal).ToArray();
            var result = IndexedFileSearch.Query([store], new SearchRequest { Text = "重复-μ", Offset = 19, PageSize = 31 }, default);
            Assert.Equal(matches.Length, result.Total);
            Assert.Equal(matches.Skip(19).Take(31), result.Items.Select(item => item.FullPath));
        }
        finally { Directory.Delete(temp, true); }
    }
}
