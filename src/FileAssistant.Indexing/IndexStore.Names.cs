using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FileAssistant.Core.Features.Search;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    // A derived, disposable accelerator. The canonical records (including all names and identities)
    // stay in entries. A query scans contiguous UTF-8 in bounded pages, including for one or two Chinese characters.
    private const int NamesPerPage = 512;

    private static void BuildNamePages(SqliteConnection db, SqliteTransaction tx, CancellationToken token,
        bool live = false, bool freshLive = false)
    {
        if (CheckVersion(db, tx) >= 3)
        {
            using var writer = new CompactNameWriter(db, tx, live, token);
            using var command = OrderedNameKeys(db, tx, live, freshLive);
            using var entries = command.ExecuteReader();
            var folded = new ArrayBufferWriter<byte>();
            while (entries.Read())
            {
                var name = SQLitePCL.raw.sqlite3_column_blob(entries.Handle, 1);
                if (freshLive)
                {
                    folded.ResetWrittenCount();
                    AppendUpperName(folded, name);
                    name = folded.WrittenSpan;
                }
                writer.Add(entries.GetInt32(0), name);
            }
            writer.Finish();
            return;
        }
        // An older snapshot may receive a journal batch while the schema upgrade is pending.
        using var read = OrderedNameKeys(db, tx, live, freshLive);
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO name_pages(page,keys,ids) VALUES($page,$keys,$ids)";
        var number = insert.Parameters.Add("$page", SqliteType.Integer);
        var keys = insert.Parameters.Add("$keys", SqliteType.Blob);
        var values = insert.Parameters.Add("$ids", SqliteType.Blob);
        insert.Prepare();
        var utf8 = new ArrayBufferWriter<byte>(16 * 1024);
        var ids = new byte[NamesPerPage * sizeof(int)];
        int page = 0, count = 0;
        using var rows = read.ExecuteReader();
        while (rows.Read())
        {
            var name = SQLitePCL.raw.sqlite3_column_blob(rows.Handle, 1);
            if (freshLive)
                AppendUpperName(utf8, name);
            else
                utf8.Write(name);
            utf8.GetSpan(1)[0] = 0; // NUL cannot occur in a Windows filename.
            utf8.Advance(1);
            BinaryPrimitives.WriteInt32LittleEndian(ids.AsSpan(count * sizeof(int)), rows.GetInt32(0));
            if (++count == NamesPerPage) Flush();
        }
        if (count > 0) Flush();

        void Flush()
        {
            token.ThrowIfCancellationRequested();
            number.Value = page++;
            keys.Value = utf8.WrittenSpan.ToArray();
            values.Value = count == NamesPerPage ? ids : ids.AsSpan(0, count * sizeof(int)).ToArray();
            insert.ExecuteNonQuery();
            utf8.ResetWrittenCount();
            count = 0;
        }
    }

    private static SqliteCommand OrderedNameKeys(SqliteConnection db, SqliteTransaction tx, bool live, bool freshLive)
        => Command(db, tx, live ? $"""
            SELECT e.id,CAST(e.{(freshLive ? "name" : "name_key")} AS BLOB) FROM entries e INDEXED BY live_name_order
            JOIN live_dirs d ON d.id=e.parent ORDER BY e.name COLLATE WINORD,(d.prefix||e.name) COLLATE WINORD,(d.prefix||e.name) COLLATE BINARY,e.id
            """ : "SELECT id,CAST(name_key AS BLOB) FROM entries INDEXED BY names WHERE id>0 ORDER BY name_key,id");

    private static void CompactNamePages(SqliteConnection db, SqliteTransaction tx, CancellationToken token)
    {
        Run(db, tx, """
            ALTER TABLE name_pages RENAME TO previous_name_pages;
            CREATE TABLE name_pages(page INTEGER PRIMARY KEY,keys BLOB NOT NULL,ids BLOB NOT NULL);
            """);
        using var writer = new CompactNameWriter(db, tx, IsLive(db, tx), token);
        using (var command = Command(db, tx, "SELECT keys,ids FROM previous_name_pages ORDER BY page"))
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var keys = SQLitePCL.raw.sqlite3_column_blob(rows.Handle, 0);
                var ids = SQLitePCL.raw.sqlite3_column_blob(rows.Handle, 1);
                while (!ids.IsEmpty)
                {
                    int end = keys.IndexOf((byte)0);
                    if (end < 0 || ids.Length < sizeof(int)) throw IncompleteNames();
                    writer.Add(BinaryPrimitives.ReadInt32LittleEndian(ids), keys[..end]);
                    keys = keys[(end + 1)..];
                    ids = ids[sizeof(int)..];
                }
                if (!keys.IsEmpty) throw IncompleteNames();
            }
        }
        writer.Finish();
        Run(db, tx, "DROP TABLE previous_name_pages");
    }

    private static InvalidDataException IncompleteNames() => new("名称索引不完整，请重新扫描。");

    private static void AppendUpperName(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> name)
    {
        if (Ascii.ToUpper(name, output.GetSpan(name.Length), out int written) != OperationStatus.Done)
        {
            var upper = Encoding.UTF8.GetString(name).ToUpperInvariant();
            written = Encoding.UTF8.GetBytes(upper, output.GetSpan(Encoding.UTF8.GetByteCount(upper)));
        }
        output.Advance(written);
    }

    internal static (List<int> Found, int Total) FindNames(SqliteConnection db, SqliteTransaction tx,
        string text, int offset, CancellationToken token, int take = PageSize, HashSet<int>? ignore = null,
        SearchVisibility? visibility = null, bool pinyin = false)
    {
        if (HasPackedNames(db, tx))
        {
            using var search = new CompactNameSearch(db, tx, text, offset, take, token, ignore, visibility, null);
            return search.Read(pinyin, false, null, null);
        }
        var found = new List<int>(Math.Min(take, PageSize));
        if (text.Contains('\0')) return (found, 0);
        var needle = Encoding.UTF8.GetBytes(text.ToUpperInvariant());
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT keys,ids FROM name_pages ORDER BY page";
        using var rows = cmd.ExecuteReader();
        int total = 0;
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            // SQLite owns these spans until the next step of this statement.
            var names = SQLitePCL.raw.sqlite3_column_blob(rows.Handle, 0);
            int hit = names.IndexOf(needle);
            bool hasHan = pinyin && names.IndexOfAnyInRange((byte)0xe3, (byte)0xf3) >= 0;
            if (hit < 0 && !hasHan) continue;
            var ids = SQLitePCL.raw.sqlite3_column_blob(rows.Handle, 1);
            int count = ids.Length / sizeof(int);
            if (ids.Length % sizeof(int) != 0 || names.IsEmpty || names[^1] != 0 || names.Count((byte)0) != count)
                throw new InvalidDataException("名称索引不完整，请重新扫描。");
            if (needle.Length == 0)
            {
                for (int i = 0; i < count; i++)
                    Match(BinaryPrimitives.ReadInt32LittleEndian(ids[(i * sizeof(int))..]));
                continue;
            }
            if (hasHan)
            {
                for (int i = 0; i < count; i++)
                {
                    int end = names.IndexOf((byte)0);
                    var key = names[..end];
                    int id = BinaryPrimitives.ReadInt32LittleEndian(ids[(i * sizeof(int))..]);
                    if (visibility?.Allows(id) != false && (key.IndexOf(needle) >= 0 || key.IndexOfAnyInRange((byte)0xe3, (byte)0xf3) >= 0 &&
                        Core.Features.Search.SearchPinyin.Matches(Encoding.UTF8.GetString(key), text))) Match(id);
                    names = names[(end + 1)..];
                }
                continue;
            }
            int index = 0;
            do
            {
                index += names[..hit].Count((byte)0);
                Match(BinaryPrimitives.ReadInt32LittleEndian(ids[(index * sizeof(int))..]));
                // NUL cannot be part of the term, so the next hit is in a new
                // name once we skip the rest of this one.
                var suffix = names[(hit + needle.Length)..];
                names = suffix[(suffix.IndexOf((byte)0) + 1)..];
                index++;
                hit = names.IndexOf(needle);
            } while (hit >= 0);
        }
        return (found, total);

        void Match(int id)
        {
            if (ignore?.Contains(id) == true || visibility?.Allows(id) == false) return;
            if (total >= offset && found.Count < take) found.Add(id);
            total++;
        }
    }

    internal (IReadOnlyList<SearchFile> Rows, int Total) LiveNames(SqliteConnection db,
        SqliteTransaction tx, string term, int offset, int take, CancellationToken token, SearchVisibility? visibility, bool pinyin,
        string generation, int? knownTotal, string? extension)
    {
        var dirty = new HashSet<int>();
        using (var query = Command(db, tx, "SELECT id FROM live_dirty"))
        using (var rows = query.ExecuteReader())
            while (rows.Read()) dirty.Add(rows.GetInt32(0));
        int needed = checked(offset + take);
        int baseOffset = dirty.Count == 0 ? offset : 0, baseTake = dirty.Count == 0 ? take : needed;
        List<int> ids;
        int total;
        if (HasPackedNames(db, tx))
        {
            using var search = new CompactNameSearch(db, tx, term, baseOffset, baseTake, token,
                dirty.Count == 0 ? null : dirty, visibility, RankVisibility(generation, visibility, dirty.Count != 0));
            (ids, total) = search.Read(pinyin, true, extension == null ? null : ExtensionToken(Encoding.ASCII.GetBytes("." + extension)), knownTotal);
        }
        else (ids, total) = FindNames(db, tx, term, baseOffset, token, baseTake, dirty, visibility, pinyin);
        const string fields = "d.prefix||e.name,e.kind,e.attrs,e.logical,e.modified";
        const string source = " FROM entries e JOIN live_dirs d ON d.id=e.parent ";
        var result = new List<SearchFile>(ids.Count);
        if (ids.Count != 0)
        {
            using var query = Command(db, tx, "SELECT " + fields + """
                 FROM json_each($p0) i CROSS JOIN entries e ON e.id=i.value
                 CROSS JOIN live_dirs d ON d.id=e.parent ORDER BY i.key
                """, JsonSerializer.Serialize(ids));
            ReadRows(query);
        }
        if (dirty.Count == 0) return (result, total);
        var changed = " WHERE e.id IN (SELECT id FROM live_dirty)" + (visibility == null ? "" : " AND SEARCH_VISIBLE(e.id)") + " AND ";
        if (extension != null)
            changed += "e.kind<>1 AND WINEXT(e.name_key)=$p0";
        else if (pinyin)
        {
            db.CreateFunction<string, bool>("DIRTY_PINYIN", name => SearchPinyin.Matches(name, term), isDeterministic: true);
            changed += "(instr(e.name_key,$p0)>0 OR (length(CAST(e.name AS BLOB))<>length(e.name) AND DIRTY_PINYIN(e.name)))";
        }
        else changed += "instr(e.name_key,$p0)>0";
        var key = extension ?? term.ToUpperInvariant();
        if (knownTotal.HasValue) total = knownTotal.Value;
        else
        {
            using var count = Command(db, tx, "SELECT count(*) FROM entries e" + changed, key);
            total += Convert.ToInt32(count.ExecuteScalar());
        }
        using (var query = Command(db, tx, "SELECT " + fields + source + changed +
            " ORDER BY e.name COLLATE WINORD,(d.prefix||e.name) COLLATE WINORD,(d.prefix||e.name) COLLATE BINARY LIMIT $p1", key, needed))
            ReadRows(query);
        var page = result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FullPath, StringComparer.Ordinal).Skip(offset).Take(take).ToArray();
        return (page, total);

        void ReadRows(SqliteCommand query)
        {
            using var row = query.ExecuteReader();
            while (row.Read())
            {
                token.ThrowIfCancellationRequested();
                bool folder = row.GetInt32(1) == 1;
                long time = row.GetInt64(4), size = row.GetInt64(3);
                result.Add(new(row.GetString(0), folder, folder || size < 0 ? null : size,
                    time is > 0 and <= 2650467743999999999 ? new DateTimeOffset(DateTime.FromFileTimeUtc(time)) : null)
                    { IsReparsePoint = ((FileAttributes)row.GetInt32(2) & FileAttributes.ReparsePoint) != 0 });
            }
        }
    }

    private static List<IndexRow> ReadSelected(SqliteConnection db, SqliteTransaction tx, PagePaths paths,
        List<int> ids, CancellationToken token)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT " + Columns + " FROM entries WHERE id=$id";
        var key = cmd.Parameters.Add("$id", SqliteType.Integer);
        cmd.Prepare();
        var rows = new List<IndexRow>(ids.Count);
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            key.Value = id;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException("名称和文件记录不一致，请重新扫描。");
            var entry = GetEntry(r);
            rows.Add(new(id, Path.Join(paths.Location(entry.Parent).Path, entry.Name), entry));
        }
        return rows;
    }
}
