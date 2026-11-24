using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FileAssistant.Core.Features.Search;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    internal static bool HasPackedNames(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = Command(db, tx, "SELECT 1 FROM sqlite_schema WHERE name='name_page_grams'");
        return cmd.ExecuteScalar() != null;
    }

    private sealed class NameRankVisibility(string generation, SearchVisibility? policy)
    {
        // Up to 8M name positions per volume, one exclusion bit per position. No names,
        // file metadata or row IDs are retained. Larger volumes still read the remaining pages.
        private readonly ConcurrentDictionary<int, ulong[]> excluded = new();
        public volatile bool Filled;
        public bool Matches(string current, SearchVisibility? visibility) => current == generation && ReferenceEquals(policy, visibility);
        public bool TryRead(int page, out ulong[] bits) => excluded.TryGetValue(page, out bits!);
        public void Store(int page, ulong[] bits) { if (page < 16384) excluded.TryAdd(page, bits); }
    }

    private NameRankVisibility? nameRankVisibility;
    private NameRankVisibility? RankVisibility(string generation, SearchVisibility? policy, bool dirty)
    {
        if (policy == null && !dirty) return null;
        var cache = Volatile.Read(ref nameRankVisibility);
        if (cache == null || !cache.Matches(generation, policy))
            Volatile.Write(ref nameRankVisibility, cache = new(generation, policy));
        return cache;
    }

    private readonly ref struct PageNumbers
    {
        public readonly ReadOnlySpan<ushort> Ends;
        public readonly ReadOnlySpan<int> Ids;
        public PageNumbers(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 4) throw IncompleteNames();
            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes), groups = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
            if (count is 0 or > NamesPerPage || groups < 1 || groups > count || bytes.Length != 4 + 2 * groups + 4 * count)
                throw IncompleteNames();
            Ends = MemoryMarshal.Cast<byte, ushort>(bytes.Slice(4, 2 * groups));
            Ids = MemoryMarshal.Cast<byte, int>(bytes[(4 + 2 * groups)..]);
            if (Ends[^1] != count) throw IncompleteNames();
        }
    }

    private sealed class CompactNameSearch(SqliteConnection db, SqliteTransaction tx, string text, int offset, int take,
        CancellationToken token, HashSet<int>? dirty, SearchVisibility? visibility, NameRankVisibility? ranks) : IDisposable
    {
        private readonly byte[] needle = Encoding.UTF8.GetBytes(text.ToUpperInvariant());
        private readonly List<int> found = new(Math.Min(take, PageSize));
        private readonly ArrayBufferWriter<byte> pinyinName = new();
        private readonly SqliteCommand readIds = Command(db, tx, "SELECT ids FROM name_pages WHERE page=$p0", 0);
        private int total;

        public (List<int> Found, int Total) Read(bool pinyin, bool live, long? extension, int? knownTotal)
        {
            if (text.Contains('\0')) return (found, 0);
            long? indexedTerm = extension;
            if (live && !indexedTerm.HasValue)
            {
                if (needle.Length == 1 && needle[0] < 128) indexedTerm = needle[0];
                else if (needle.Length == 2 && AlphaNumeric(needle[0]) >= 0 && AlphaNumeric(needle[1]) >= 0)
                    indexedTerm = 128 + AlphaNumeric(needle[0]) * 36 + AlphaNumeric(needle[1]);
            }
            if (indexedTerm.HasValue)
                ReadToken(indexedTerm.Value, pinyin && !extension.HasValue);
            else if (needle.Length == 0 && knownTotal is { } complete)
            {
                using var cmd = Command(db, tx, "SELECT ids FROM name_pages ORDER BY page");
                using var rows = cmd.ExecuteReader();
                while (found.Count < take && rows.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var numbers = new PageNumbers(raw.sqlite3_column_blob(rows.Handle, 0));
                    foreach (int id in numbers.Ids)
                    {
                        if (dirty?.Contains(id) == true || visibility?.Allows(id) == false) continue;
                        if (total++ >= offset) found.Add(id);
                        if (found.Count == take) break;
                    }
                }
                total = complete;
            }
            else
            {
                var pages = CandidatePages(needle, 0);
                if (pinyin && pages != null)
                {
                    if (CanReadPinyinKeys())
                    {
                        Union(pages, PagesForGram(UnindexedHan));
                        var spelling = Encoding.ASCII.GetBytes(text.ToUpperInvariant().Replace('Ü', 'V'));
                        Union(pages, CandidatePages(spelling, PinyinGram)!);
                    }
                    else Union(pages, PagesForGram(HanPages));
                }
                if (pages?.Count != 0)
                    Scan(pages, pinyin, false, null);
            }
            return (found, total);
        }

        private bool CanReadPinyinKeys()
        {
            uint current = SearchPinyin.SearchKeyVersion;
            if (current == 0) return false;
            using var cmd = Command(db, tx, "SELECT mask FROM name_page_grams WHERE gram=$p0 AND section=-1", PinyinVersion);
            return cmd.ExecuteScalar() is long stored && stored == current;
        }

        private static void Union(SortedDictionary<int, ulong> result, SortedDictionary<int, ulong> other)
        {
            foreach (var (section, mask) in other) result[section] = result.GetValueOrDefault(section) | mask;
        }

        private SortedDictionary<int, ulong>? CandidatePages(byte[] bytes, int alphabet)
        {
            if (bytes.Length < 2) return alphabet == 0 ? null : PagesForGram(alphabet + bytes[0]);
            if (bytes.Length == 2) return PagesForGram(alphabet + Gram2(bytes[0], bytes[1]));
            // Intersect a bounded sample across a long term; every occurrence must
            // contain all its sampled trigrams. The final block comparison is exact.
            var grams = new HashSet<int>();
            int last = bytes.Length - 3;
            for (int i = 0; i <= Math.Min(last, 5); i++)
            {
                int start = last <= 5 ? i : i * last / 5;
                grams.Add(alphabet + Gram3(bytes[start], bytes[start + 1], bytes[start + 2]));
            }
            SortedDictionary<int, ulong>? pages = null;
            foreach (int gram in grams)
            {
                pages = PagesForGram(gram, pages);
                if (pages.Count == 0) break;
            }
            return pages;
        }

        private SortedDictionary<int, ulong> PagesForGram(int gram, SortedDictionary<int, ulong>? intersect = null)
        {
            using var cmd = Command(db, tx, "SELECT section,mask FROM name_page_grams WHERE gram=$p0 ORDER BY section", gram);
            using var rows = cmd.ExecuteReader();
            var pages = new SortedDictionary<int, ulong>();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                int section = rows.GetInt32(0);
                ulong mask = unchecked((ulong)rows.GetInt64(1));
                if (intersect != null) mask &= intersect.GetValueOrDefault(section);
                if (mask != 0) pages.Add(section, mask);
            }
            return pages;
        }

        private void ReadToken(long term, bool pinyin)
        {
            if (term < 128 && ranks is { Filled: false })
            {
                // A single ASCII character typically hits almost every page. Read their
                // positions in one ordered pass instead of one point query per page.
                using var all = Command(db, tx, "SELECT page,ids FROM name_pages WHERE page<16384 ORDER BY page");
                using var positions = all.ExecuteReader();
                while (positions.Read())
                {
                    token.ThrowIfCancellationRequested();
                    int page = positions.GetInt32(0);
                    if (!ranks.TryRead(page, out _))
                        CacheExcluded(page, new PageNumbers(raw.sqlite3_column_blob(positions.Handle, 1)).Ids);
                }
                ranks.Filled = true;
            }
            // Romanization can add names whose raw bytes do not contain the letter.
            // Only Han-bearing pages need spelling checks; union their row masks in name order.
            var extra = new SortedDictionary<int, ulong[]>();
            if (pinyin)
            {
                if (CanReadPinyinKeys())
                {
                    Scan(PagesForGram(UnindexedHan), true, true, extra);
                    using var phonetic = Command(db, tx, "SELECT page,bits FROM name_tokens WHERE term=$p0", PinyinToken + term);
                    using var sections = phonetic.ExecuteReader();
                    Span<ulong> added = stackalloc ulong[NamesPerPage / 64];
                    while (sections.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        int first = sections.GetInt32(0);
                        var block = raw.sqlite3_column_blob(sections.Handle, 1);
                        while (!block.IsEmpty)
                        {
                            int page = first + block[0];
                            block = block[ReadTokenMask(block, MemoryMarshal.AsBytes(added))..];
                            if (extra.TryGetValue(page, out var existing))
                                for (int i = 0; i < added.Length; i++) existing[i] |= added[i];
                            else extra.Add(page, added.ToArray());
                        }
                    }
                }
                else Scan(PagesForGram(HanPages), true, true, extra);
            }
            using var more = extra.GetEnumerator();
            bool hasMore = more.MoveNext();
            using var cmd = Command(db, tx, "SELECT page,bits FROM name_tokens WHERE term=$p0 ORDER BY page", term);
            using var rows = cmd.ExecuteReader();
            Span<ulong> bits = stackalloc ulong[NamesPerPage / 64];
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                int first = rows.GetInt32(0);
                var block = raw.sqlite3_column_blob(rows.Handle, 1);
                while (!block.IsEmpty)
                {
                    int page = first + block[0];
                    int consumed = ReadTokenMask(block, MemoryMarshal.AsBytes(bits));
                    block = block[consumed..];
                    while (hasMore && more.Current.Key < page)
                    {
                        Collect(more.Current.Key, more.Current.Value, default);
                        hasMore = more.MoveNext();
                    }
                    if (hasMore && more.Current.Key == page)
                    {
                        for (int i = 0; i < bits.Length; i++) bits[i] |= more.Current.Value[i];
                        hasMore = more.MoveNext();
                    }
                    Collect(page, bits, default);
                }
            }
            while (hasMore)
            {
                Collect(more.Current.Key, more.Current.Value, default);
                hasMore = more.MoveNext();
            }
        }

        private static int ReadTokenMask(ReadOnlySpan<byte> input, Span<byte> bytes)
        {
            if (input.Length < 2 || input[0] >= PagesPerTokenBlock) throw IncompleteNames();
            int header = input[1];
            if (header == 128)
            {
                if (input.Length < 2 + bytes.Length) throw IncompleteNames();
                input.Slice(2, bytes.Length).CopyTo(bytes);
                return 2 + bytes.Length;
            }
            int count = header & 63;
            bytes.Fill((header & 64) == 0 ? (byte)0 : (byte)255);
            if (count <= 8)
            {
                if (input.Length < 2 + 2 * count) throw IncompleteNames();
                for (int i = 0; i < count; i++)
                {
                    int index = input[2 + 2 * i];
                    if (index >= bytes.Length) throw IncompleteNames();
                    bytes[index] = input[3 + 2 * i];
                }
                return 2 + 2 * count;
            }
            if (input.Length < 10 + count) throw IncompleteNames();
            ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(input[2..]);
            if (BitOperations.PopCount(mask) != count) throw IncompleteNames();
            for (int i = 0; mask != 0; i++, mask &= mask - 1)
                bytes[BitOperations.TrailingZeroCount(mask)] = input[10 + i];
            return 10 + count;
        }

        private void Scan(SortedDictionary<int, ulong>? pages, bool pinyin, bool pinyinOnly, SortedDictionary<int, ulong[]>? extra)
        {
            if (pages?.Count == 0) return;
            if (pages == null)
            {
                using var all = Command(db, tx, "SELECT page,keys,ids FROM name_pages ORDER BY page");
                using var rows = all.ExecuteReader();
                ScanRows(rows, pinyin, pinyinOnly, extra);
                return;
            }
            // The range seek bounds SQLite's work to one existing set of 64 pages.
            // Its bit test runs before SQLite materializes either payload column.
            using var cmd = Command(db, tx, """
                SELECT page,keys,ids FROM name_pages WHERE page BETWEEN $p0 AND $p0+63
                  AND ($p1 & (1 << (page & 63)))<>0 ORDER BY page
                """, 0, 0L);
            cmd.Prepare();
            foreach (var (section, mask) in pages)
            {
                cmd.Parameters[0].Value = section * 64;
                cmd.Parameters[1].Value = unchecked((long)mask);
                using var rows = cmd.ExecuteReader();
                ScanRows(rows, pinyin, pinyinOnly, extra);
            }
        }

        private void ScanRows(SqliteDataReader rows, bool pinyin, bool pinyinOnly, SortedDictionary<int, ulong[]>? extra)
        {
            Span<ulong> bits = stackalloc ulong[NamesPerPage / 64];
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                int page = rows.GetInt32(0);
                var keys = raw.sqlite3_column_blob(rows.Handle, 1);
                var numbers = new PageNumbers(raw.sqlite3_column_blob(rows.Handle, 2));
                bits.Clear();
                MatchBlocks(keys, numbers.Ends, bits, pinyin, pinyinOnly);
                if (bits.IndexOfAnyExcept(0UL) < 0) continue;
                if (extra != null) extra.Add(page, bits.ToArray());
                else Collect(page, bits, numbers.Ids);
            }
        }

        private void MatchBlocks(ReadOnlySpan<byte> keys, ReadOnlySpan<ushort> ends, Span<ulong> bits, bool pinyin, bool pinyinOnly)
        {
            if (keys.IsEmpty || keys[0] != 0xff) throw IncompleteNames();
            keys = keys[1..];
            int group = 0;
            Span<int> overlaps = needle.Length <= 64 ? stackalloc int[needle.Length] : new int[needle.Length];
            while (!keys.IsEmpty)
            {
                if (keys.Length < CompactBlockHeader) throw IncompleteNames();
                int groups = BinaryPrimitives.ReadUInt16LittleEndian(keys);
                int shared = BinaryPrimitives.ReadInt32LittleEndian(keys[2..]), size = BinaryPrimitives.ReadInt32LittleEndian(keys[6..]);
                keys = keys[CompactBlockHeader..];
                if (groups is 0 or > GroupsPerBlock || group + groups > ends.Length || shared < 0 || size < groups || shared > keys.Length - size)
                    throw IncompleteNames();
                var prefix = keys[..shared];
                var tails = keys.Slice(shared, size);
                keys = keys[(shared + size)..];
                int endGroup = group + groups;
                if (!pinyinOnly && prefix.IndexOf(needle) >= 0)
                {
                    SetRange(bits, group == 0 ? 0 : ends[group - 1], ends[endGroup - 1]);
                    group = endGroup;
                    continue;
                }
                bool hanPrefix = pinyin && HasHan(prefix);
                bool romanize = hanPrefix || pinyin && HasHan(tails);
                int splits = 0;
                if (!pinyinOnly)
                {
                    for (int n = 1; n <= Math.Min(prefix.Length, needle.Length - 1); n++)
                        if (prefix.EndsWith(needle.AsSpan(0, n))) overlaps[splits++] = n;
                }
                if (!romanize && (pinyinOnly || splits == 0 && tails.IndexOf(needle) < 0))
                {
                    group = endGroup;
                    continue;
                }
                for (; group < endGroup; group++)
                {
                    int end = tails.IndexOf((byte)0);
                    if (end < 0) throw IncompleteNames();
                    var tail = tails[..end];
                    tails = tails[(end + 1)..];
                    bool hit = !pinyinOnly && tail.IndexOf(needle) >= 0;
                    for (int s = 0; !hit && s < splits; s++)
                        hit = tail.StartsWith(needle.AsSpan(overlaps[s]));
                    if (!hit && romanize && (hanPrefix || HasHan(tail)))
                    {
                        pinyinName.ResetWrittenCount();
                        pinyinName.Write(prefix);
                        pinyinName.Write(tail);
                        hit = SearchPinyin.Matches(Encoding.UTF8.GetString(pinyinName.WrittenSpan), text);
                    }
                    if (hit) SetRange(bits, group == 0 ? 0 : ends[group - 1], ends[group]);
                }
            }
            if (group != ends.Length) throw IncompleteNames();
        }

        private static bool HasHan(ReadOnlySpan<byte> key) => key.IndexOfAnyInRange((byte)0xe3, (byte)0xf3) >= 0;

        private void Collect(int page, ReadOnlySpan<ulong> hits, ReadOnlySpan<int> ids)
        {
            ulong[]? hidden = null;
            bool needVisibility = ranks != null && !ranks.TryRead(page, out hidden!);
            if (ids.IsEmpty && (needVisibility || found.Count < take))
            {
                readIds.Parameters[0].Value = page;
                using var row = readIds.ExecuteReader();
                if (!row.Read()) throw IncompleteNames();
                Collect(page, hits, new PageNumbers(raw.sqlite3_column_blob(row.Handle, 0)).Ids);
                return;
            }
            if (needVisibility)
                hidden = CacheExcluded(page, ids);
            for (int word = 0; word < hits.Length; word++)
            {
                ulong mask = hits[word];
                if (hidden != null && word < hidden.Length) mask &= ~hidden[word];
                int count = BitOperations.PopCount(mask);
                if (count != 0 && found.Count < take && (long)total + count > offset)
                {
                    for (int ordinal = total; mask != 0 && found.Count < take; ordinal++)
                    {
                        if (ordinal >= offset) found.Add(ids[word * 64 + BitOperations.TrailingZeroCount(mask)]);
                        mask &= mask - 1;
                    }
                }
                total += count;
            }
        }

        private ulong[] CacheExcluded(int page, ReadOnlySpan<int> ids)
        {
            var excluded = new ulong[(ids.Length + 63) >> 6];
            for (int i = 0; i < ids.Length; i++)
                if (visibility?.Allows(ids[i]) == false || dirty?.Contains(ids[i]) == true)
                    excluded[i >> 6] |= 1UL << (i & 63);
            if (excluded.AsSpan().IndexOfAnyExcept(0UL) < 0) excluded = [];
            ranks!.Store(page, excluded);
            return excluded;
        }

        public void Dispose() => readIds.Dispose();
    }

    private static void SetRange(Span<ulong> bits, int first, int end)
    {
        if (end <= first || end > NamesPerPage) throw IncompleteNames();
        int last = (end - 1) >> 6;
        for (int word = first >> 6; word <= last; word++)
        {
            ulong mask = word == first >> 6 ? ulong.MaxValue << (first & 63) : ulong.MaxValue;
            if (word == last) mask &= ulong.MaxValue >> (63 - ((end - 1) & 63));
            bits[word] |= mask;
        }
    }
}
