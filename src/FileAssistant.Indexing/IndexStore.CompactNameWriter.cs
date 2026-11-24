using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FileAssistant.Core.Features.Search;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    private const int GroupsPerBlock = 16;
    private const int HanPages = -1;
    private const int UnindexedHan = -2;
    private const int PinyinVersion = -3;
    private const int PinyinGram = 1 << 26;
    private const long PinyinToken = 1L << 60;
    private const int CompactBlockHeader = 10;
    private const int PagesPerTokenBlock = 32;

    // One mask covers 64 consecutive pages. An integer key distinguishes exact UTF-8
    // unigrams, bigrams and trigrams. There are no probabilistic misses or hash collisions.
    private static int Gram2(byte a, byte b) => 0x10000 + (a << 8) + b;
    private static int Gram3(byte a, byte b, byte c) => 0x1000000 + (a << 16) + (b << 8) + c;

    private sealed class CompactNameWriter : IDisposable
    {
        private readonly SqliteCommand insertPage, insertGram, insertBits;
        private readonly CancellationToken token;
        private readonly SearchVisibility? folders;
        private readonly ArrayBufferWriter<byte> names = new(16 * 1024);
        private readonly ArrayBufferWriter<byte> encoded = new(16 * 1024);
        private readonly int[] starts = new int[NamesPerPage + 1];
        private readonly ushort[] ends = new ushort[NamesPerPage];
        private readonly int[] ids = new int[NamesPerPage];
        private readonly Dictionary<int, ulong> grams = [];
        private readonly Dictionary<long, ulong[]> tokenBits = [];
        private readonly Dictionary<long, ArrayBufferWriter<byte>> tokenBlock = [];
        private readonly int[] lastBigram = new int[36 * 36];
        private int indexedGroups;
        private int page, rows, groups;

        public CompactNameWriter(SqliteConnection db, SqliteTransaction tx, bool live, CancellationToken token)
        {
            this.token = token;
            Run(db, tx, """
                DROP TABLE IF EXISTS name_page_grams;
                DROP TABLE IF EXISTS name_tokens;
                CREATE TABLE name_page_grams(gram INTEGER,section INTEGER,mask INTEGER NOT NULL,PRIMARY KEY(gram,section)) WITHOUT ROWID;
                CREATE TABLE name_tokens(term INTEGER,page INTEGER,bits BLOB NOT NULL,PRIMARY KEY(term,page)) WITHOUT ROWID;
                """);
            if (live)
            {
                Run(db, tx, """
                    CREATE INDEX IF NOT EXISTS live_directory_children ON entries(parent) WHERE kind=1;
                    CREATE INDEX IF NOT EXISTS visibility_hidden ON entries(id) WHERE (attrs&6)<>0;
                    CREATE INDEX IF NOT EXISTS visibility_denied ON live_dirs(id) WHERE (own_flags&32)<>0;
                    """);
                folders = new("", false, []);
                using var query = Command(db, tx, "SELECT id FROM entries WHERE kind=1");
                using var list = query.ExecuteReader();
                while (list.Read()) folders.Exclude(list.GetInt32(0));
                folders.Freeze();
            }
            insertPage = Insert(db, tx, "INSERT INTO name_pages(page,keys,ids) VALUES($a,$b,$c)", SqliteType.Blob, SqliteType.Blob);
            insertGram = Insert(db, tx, """
                INSERT INTO name_page_grams(gram,section,mask) VALUES($a,$b,$c)
                ON CONFLICT(gram,section) DO UPDATE SET mask=mask|excluded.mask
                """, SqliteType.Integer, SqliteType.Integer);
            insertBits = Insert(db, tx, "INSERT INTO name_tokens(term,page,bits) VALUES($a,$b,$c)", SqliteType.Integer, SqliteType.Blob);
            if (live) Execute(insertGram, PinyinVersion, -1, (long)SearchPinyin.SearchKeyVersion);
        }

        private static SqliteCommand Insert(SqliteConnection db, SqliteTransaction tx, string sql, SqliteType second, SqliteType third)
        {
            var cmd = Command(db, tx, sql);
            cmd.Parameters.Add("$a", SqliteType.Integer);
            cmd.Parameters.Add("$b", second);
            cmd.Parameters.Add("$c", third);
            cmd.Prepare();
            return cmd;
        }

        public void Add(int id, ReadOnlySpan<byte> name)
        {
            if (id <= 0) throw IncompleteNames();
            if (groups == 0 || !name.SequenceEqual(names.WrittenSpan[starts[groups - 1]..]))
            {
                starts[groups++] = names.WrittenCount;
                names.Write(name);
            }
            ids[rows++] = id;
            ends[groups - 1] = (ushort)rows;
            if (rows == NamesPerPage) WritePage();
        }

        public void Finish()
        {
            if (rows != 0) WritePage();
            if (grams.Count > 0) WriteGrams((page - 1) >> 6);
            if (tokenBlock.Count > 0) WriteTokens();
        }

        private void WritePage()
        {
            token.ThrowIfCancellationRequested();
            starts[groups] = names.WrittenCount;
            encoded.ResetWrittenCount();
            encoded.GetSpan(1)[0] = 0xff; // Invalid UTF-8 distinguishes packed pages from version 2.
            encoded.Advance(1);
            for (int first = 0; first < groups; first += GroupsPerBlock)
            {
                int end = Math.Min(first + GroupsPerBlock, groups);
                var prefix = Key(first);
                for (int i = first + 1; i < end && !prefix.IsEmpty; i++)
                    prefix = prefix[..prefix.CommonPrefixLength(Key(i))];
                int tails = starts[end] - starts[first] - (end - first) * prefix.Length + end - first;
                var header = encoded.GetSpan(CompactBlockHeader);
                BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(end - first));
                BinaryPrimitives.WriteInt32LittleEndian(header[2..], prefix.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header[6..], tails);
                encoded.Advance(CompactBlockHeader);
                encoded.Write(prefix);
                for (int i = first; i < end; i++)
                {
                    var key = Key(i);
                    IndexGroup(key, i == 0 ? 0 : ends[i - 1], ends[i]);
                    if (grams.Count >= 32768) WriteGrams(page >> 6);
                    encoded.Write(key[prefix.Length..]);
                    encoded.GetSpan(1)[0] = 0;
                    encoded.Advance(1);
                }
            }
            var numbers = new byte[4 + groups * sizeof(ushort) + rows * sizeof(int)];
            BinaryPrimitives.WriteUInt16LittleEndian(numbers, (ushort)rows);
            BinaryPrimitives.WriteUInt16LittleEndian(numbers.AsSpan(2), (ushort)groups);
            MemoryMarshal.AsBytes(ends.AsSpan(0, groups)).CopyTo(numbers.AsSpan(4));
            MemoryMarshal.AsBytes(ids.AsSpan(0, rows)).CopyTo(numbers.AsSpan(4 + groups * sizeof(ushort)));
            Execute(insertPage, page, encoded.WrittenSpan.ToArray(), numbers);
            foreach (var (term, bits) in tokenBits)
            {
                var bytes = MemoryMarshal.AsBytes(bits.AsSpan());
                if (bytes.IndexOfAnyExcept((byte)0) < 0) continue;
                ref var block = ref CollectionsMarshal.GetValueRefOrAddDefault(tokenBlock, term, out _);
                block ??= new ArrayBufferWriter<byte>(64);
                AddTokenMask(block, page & (PagesPerTokenBlock - 1), bytes);
            }
            tokenBits.Clear();
            page++;
            if ((page & 63) == 0) WriteGrams((page - 1) >> 6);
            if ((page & (PagesPerTokenBlock - 1)) == 0) WriteTokens();
            names.ResetWrittenCount();
            groups = rows = 0;
        }

        private ReadOnlySpan<byte> Key(int i) => names.WrittenSpan[starts[i]..starts[i + 1]];

        private void IndexGroup(ReadOnlySpan<byte> key, int first, int end)
        {
            ulong bit = 1UL << (page & 63);
            ulong asciiLow = 0, asciiHigh = 0;
            int previous = -1;
            indexedGroups++;
            for (int i = 0; i < key.Length; i++)
            {
                byte c = key[i];
                if (c < 64) asciiLow |= 1UL << c;
                else if (c < 128) asciiHigh |= 1UL << (c - 64);
                // Unigrams only matter for a single ASCII byte. Other literal queries
                // are at least two bytes, even when they consist of one character.
                int letter = AlphaNumeric(c);
                if (folders != null && letter >= 0 && previous >= 0)
                {
                    int pair = previous * 36 + letter;
                    if (lastBigram[pair] != indexedGroups)
                    {
                        AddRange(128 + pair, first, end);
                        lastBigram[pair] = indexedGroups;
                    }
                }
                else if (i >= 1) AddGram(Gram2(key[i - 1], c), bit);
                previous = letter;
                if (i >= 2) AddGram(Gram3(key[i - 2], key[i - 1], c), bit);
            }
            if (key.IndexOfAnyInRange((byte)0xe3, (byte)0xf3) >= 0)
            {
                AddGram(HanPages, bit);
                if (folders != null) IndexPinyin(key, first, end, bit);
            }
            if (folders == null) return;
            while (asciiLow != 0)
            {
                AddRange(BitOperations.TrailingZeroCount(asciiLow), first, end);
                asciiLow &= asciiLow - 1;
            }
            while (asciiHigh != 0)
            {
                AddRange(64 + BitOperations.TrailingZeroCount(asciiHigh), first, end);
                asciiHigh &= asciiHigh - 1;
            }
            if (ExtensionToken(key) is not { } ext) return;
            var matches = Bits(ext);
            for (int i = first; i < end; i++)
                if (folders.Allows(ids[i])) matches[i >> 6] |= 1UL << (i & 63);
        }

        private void AddGram(int gram, ulong pageBit)
            => CollectionsMarshal.GetValueRefOrAddDefault(grams, gram, out _) |= pageBit;

        private ulong[] Bits(long term)
        {
            ref var bits = ref CollectionsMarshal.GetValueRefOrAddDefault(tokenBits, term, out _);
            return bits ??= new ulong[NamesPerPage / 64];
        }

        private void AddRange(long term, int start, int end)
        {
            SetRange(Bits(term), start, end);
        }

        private void IndexPinyin(ReadOnlySpan<byte> key, int first, int end, ulong pageBit)
        {
            if (SearchPinyin.SearchKeyVersion == 0 || !SearchPinyin.TryGetSearchKeys(Encoding.UTF8.GetString(key), out var forms))
            {
                AddGram(UnindexedHan, pageBit);
                return;
            }
            bool same = forms.Full == forms.Keyboard;
            IndexSpelling(forms.Full, same ? null : false, key, first, end, pageBit);
            if (!same) IndexSpelling(forms.Keyboard, true, key, first, end, pageBit);
            if (forms.Initials != forms.Full || !same) IndexSpelling(forms.Initials, null, key, first, end, pageBit);
        }

        // Matches chooses the keyboard spelling exactly when a query contains v/ü.
        // Isolated Han runs are bounded by non-alphanumerics. A one- or two-character
        // ASCII match across a Han boundary therefore uses the same joined spelling.
        private void IndexSpelling(string spelling, bool? needsV, ReadOnlySpan<byte> literal, int first, int end, ulong pageBit)
        {
            byte prev = 0, older = 0;
            Span<byte> pair = stackalloc byte[2];
            foreach (char symbol in spelling)
            {
                byte c = char.IsAsciiLetterOrDigit(symbol) ? (byte)char.ToUpperInvariant(symbol) : (byte)0;
                if (c != 0)
                {
                    AddGram(PinyinGram + c, pageBit);
                    if ((needsV == null || needsV == (c == 'V')) && literal.IndexOf(c) < 0)
                        AddRange(PinyinToken + c, first, end);
                    if (prev != 0)
                    {
                        AddGram(PinyinGram + Gram2(prev, c), pageBit);
                        pair[0] = prev;
                        pair[1] = c;
                        if ((needsV == null || needsV == (prev == 'V' || c == 'V')) && literal.IndexOf(pair) < 0)
                            AddRange(PinyinToken + 128 + AlphaNumeric(prev) * 36 + AlphaNumeric(c), first, end);
                        if (older != 0) AddGram(PinyinGram + Gram3(older, prev, c), pageBit);
                    }
                }
                older = prev;
                prev = c;
            }
        }

        private void WriteTokens()
        {
            int firstPage = (page - 1) & ~(PagesPerTokenBlock - 1);
            foreach (var (term, bytes) in tokenBlock)
                Execute(insertBits, term, firstPage, bytes.WrittenSpan.ToArray());
            tokenBlock.Clear();
        }

        private void WriteGrams(int section)
        {
            foreach (var (gram, mask) in grams) Execute(insertGram, gram, section, unchecked((long)mask));
            grams.Clear();
        }

        private static void Execute(SqliteCommand cmd, object first, object second, object third)
        {
            cmd.Parameters[0].Value = first;
            cmd.Parameters[1].Value = second;
            cmd.Parameters[2].Value = third;
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            insertPage.Dispose();
            insertGram.Dispose();
            insertBits.Dispose();
        }
    }

    private static int AlphaNumeric(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? c - 'A'
        : c is >= (byte)'0' and <= (byte)'9' ? c - '0' + 26 : -1;

    // Each posting is a 512-position bitset. Entire pages and sparse pages are
    // common; store either the raw mask or just the byte positions that differ.
    private static void AddTokenMask(ArrayBufferWriter<byte> block, int page, ReadOnlySpan<byte> bytes)
    {
        byte fill = bytes.Count((byte)255) > bytes.Count((byte)0) ? (byte)255 : (byte)0;
        int exceptions = bytes.Length - bytes.Count(fill);
        var output = block.GetSpan(67);
        output[0] = (byte)page;
        if (exceptions >= 55)
        {
            output[1] = 128;
            bytes.CopyTo(output[2..]);
            block.Advance(2 + bytes.Length);
            return;
        }
        output[1] = (byte)(exceptions | (fill == 0 ? 0 : 64));
        int index;
        if (exceptions <= 8)
        {
            index = 2;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == fill) continue;
                output[index++] = (byte)i;
                output[index++] = bytes[i];
            }
        }
        else
        {
            ulong positions = 0;
            index = 10;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == fill) continue;
                positions |= 1UL << i;
                output[index++] = bytes[i];
            }
            BinaryPrimitives.WriteUInt64LittleEndian(output[2..], positions);
        }
        block.Advance(index);
    }

    // File extensions up to eight ASCII alphanumerics fit in one SQLite integer in base 37.
    // Longer extensions and other predicates continue through the full SQL filter.
    private static long? ExtensionToken(ReadOnlySpan<byte> filename)
    {
        int dot = filename.LastIndexOf((byte)'.');
        if (dot < 0 || filename.Length - dot is < 2 or > 9) return null;
        long result = 256;
        foreach (byte c in filename[(dot + 1)..])
        {
            int digit = c is >= (byte)'A' and <= (byte)'Z' ? c - 'A' + 1
                : c is >= (byte)'0' and <= (byte)'9' ? c - '0' + 27 : 0;
            if (digit == 0) return null;
            result = result * 37 + digit;
        }
        return result;
    }
}
