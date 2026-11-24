using System.Runtime.InteropServices;
using System.Security.AccessControl;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Indexing.Windows;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Indexing.Tests;

public sealed class BulkJournalTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("FileAssistant-bulk-journal-").FullName;
    private string Cache => Path.Join(temp, "db");
    private string Root => Path.Join(temp, "root");
    private IndexStore Store => new IndexCatalog(Cache).ForRoot(Root);

    [Fact]
    public void ARedactedDataWriteProbesOnlyTheExactIdentityInACaseSensitiveDirectory()
    {
        var source = new Files(Root);
        var upper = source.Add("Twin.txt", 7, 11, 14);
        source.Add("twin.txt", 7, 12, 20);
        var initial = source.Seed(Store);
        source.Resize("Twin.txt", 60);

        var final = Store.ApplyChanges(initial, [source.Change(upper, "", 1)], [], new(9, 2), Cache, source, default);

        Assert.Equal(1, source.Probes);
        Assert.Equal(0, source.Listings);
        using var result = Store.OpenSpace(Root, default);
        var children = result.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
        Assert.Equal(60, children["Twin.txt"].LogicalBytes);
        Assert.Equal(20, children["twin.txt"].LogicalBytes);
        Assert.Equal(80, final.CountedBytes);
        Assert.Equal(new JournalCursor(9, 2), Store.GetLiveState()!.Cursor);
    }

    [Fact]
    public void AccessDeniedDuringAPointLookupRetainsNamesAndMarksTheirParent()
    {
        var source = new Files(Root);
        var item = source.Add("denied.txt", 3, 4, 13);
        var initial = source.Seed(Store);
        source.DenyParent = true;

        Store.ApplyChanges(initial, [source.Change(item, "", 1)], [], new(9, 2), Cache, source, default);

        Assert.Equal(1, source.Probes);
        Assert.Equal(1, source.Listings);
        using var result = Store.OpenSpace(Root, default);
        Assert.Equal("denied.txt", Assert.Single(result.Entries, e => !e.IsDirectory).Name);
        Assert.True(result.Entries[0].Flags.HasFlag(SpaceEntryFlags.AccessDenied));
        Assert.Equal(13, result.Entries[0].CountedBytes);
        Assert.Equal(new JournalCursor(9, 2), Store.GetLiveState()!.Cursor);
    }

    [Fact]
    public async Task ACaseOnlyRenameFallsBackToTheActualDirectorySpelling()
    {
        Directory.CreateDirectory(Root);
        var original = Path.Join(Root, "Receipt.Log");
        var renamed = Path.Join(Root, "RECEIPT.log");
        File.WriteAllText(original, "receipt");
        var journal = new Replay();
        await using var engine = Engine(journal);
        await engine.EnsureReadyAsync(null, default);
        var dir = IndexEntryReader.Read(Root)!.Value.Stamp.Identity;
        var id = IndexEntryReader.Read(original)!.Value.Stamp.Identity;
        File.Move(original, Path.Join(Root, "intermediate"));
        File.Move(Path.Join(Root, "intermediate"), renamed);
        // The file changed again after the reader captured its head. The stored path may
        // still open successfully on an ordinary NTFS directory despite its new spelling.
        journal.Events.Add(new(dir.Low, dir.High, id.Low, id.High, "", 1));
        await engine.SynchronizeAsync();

        using var result = Store.OpenSpace(Root, default);
        Assert.Equal("RECEIPT.log", Assert.Single(result.Entries, e => !e.IsDirectory).Name);
    }

    [Fact]
    public async Task ARedactedLinkNotificationCanDiscoverTwoNewNamesInDifferentParents()
    {
        var left = Directory.CreateDirectory(Path.Join(Root, "left")).FullName;
        var right = Directory.CreateDirectory(Path.Join(Root, "right")).FullName;
        var journal = new Replay();
        await using var engine = Engine(journal);
        await engine.EnsureReadyAsync(null, default);
        var original = Path.Join(left, "one.bin");
        var alias = Path.Join(right, "two.bin");
        File.WriteAllBytes(original, new byte[16000]);
        Assert.True(CreateHardLinkW(alias, original, 0));
        var dir = IndexEntryReader.Read(left)!.Value.Stamp.Identity;
        var id = IndexEntryReader.Read(original)!.Value.Stamp.Identity;
        journal.Events.Add(new(dir.Low, dir.High, id.Low, id.High, "", 0x10100));
        await engine.SynchronizeAsync();

        using var result = Store.OpenSpace(Root, default);
        var files = result.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name).ToArray();
        Assert.Equal(new[] { "one.bin", "two.bin" }, files.Select(e => e.Name));
        Assert.All(files, e => Assert.Equal(id, e.Stamp.Identity));
        Assert.Equal(32000, result.Entries[0].LogicalBytes);
        Assert.Equal(IndexEntryReader.Read(original)!.Value.AllocatedBytes, result.Entries[0].CountedBytes);
    }

    [Fact]
    public async Task CancelledMidReplayKeepsItsCommittedDataAndCursorThenRetries()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Join(Root, "pending.bin");
        File.WriteAllBytes(path, [1]);
        var journal = new Replay();
        await using var engine = Engine(journal);
        await engine.EnsureReadyAsync(null, default);
        var initial = Store.GetLiveState()!;
        var dir = IndexEntryReader.Read(Root)!.Value.Stamp.Identity;
        var id = IndexEntryReader.Read(path)!.Value.Stamp.Identity;
        File.WriteAllBytes(path, new byte[975]);
        journal.Events.AddRange(Enumerable.Repeat(new JournalChange(dir.Low, dir.High, id.Low, id.High, "", 1), 4096));
        using var stop = new CancellationTokenSource();
        journal.OnRead = n => { if (n == 40) stop.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SynchronizeAsync(stop.Token));
        Assert.Equal(initial.Cursor, Store.GetLiveState()!.Cursor);
        Assert.Equal(initial.Info.Generation, Store.GetLiveState()!.Info.Generation);
        using (var previous = Store.OpenSpace(Root, default))
            Assert.Equal(1, previous.Entries[previous.Find(path)!.Value].LogicalBytes);

        journal.OnRead = null;
        await engine.SynchronizeAsync();
        using var result = Store.OpenSpace(Root, default);
        Assert.Equal(975, result.Entries[result.Find(path)!.Value].LogicalBytes);
        Assert.Equal(new JournalCursor(9, 4097), Store.GetLiveState()!.Cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayingOldMftChildrenPrunesAJunctionAndStillUpdatesItsVolume(bool previouslyMarkedSkipped)
    {
        var boundary = Path.Join(Root, "boundary");
        var deep = Directory.CreateDirectory(Path.Join(boundary, "deep")).FullName;
        var obsolete = Path.Join(deep, "obsolete.txt");
        var ordinary = Path.Join(Root, "ordinary.txt");
        File.WriteAllText(obsolete, "old");
        File.WriteAllText(ordinary, "old");
        var rootId = IndexEntryReader.Read(Root)!.Value.Stamp.Identity;
        var deepId = IndexEntryReader.Read(deep)!.Value.Stamp.Identity;
        var fileId = IndexEntryReader.Read(obsolete)!.Value.Stamp.Identity;
        var ordinaryId = IndexEntryReader.Read(ordinary)!.Value.Stamp.Identity;
        await new SpaceScanner().ScanToAsync(Root, null, default, (meta, count, rows) =>
            Store.ReplaceLive(meta with { ScanMethod = "NTFS MFT" }, count, rows.Select(entry =>
                previouslyMarkedSkipped && entry.Name == "boundary" ?
                    entry with { Flags = SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped } : entry), default, new(9, 1)));

        Directory.Move(boundary, Path.Join(temp, "original-tree"));
        var outside = Directory.CreateDirectory(Path.Join(temp, "outside")).FullName;
        var outsideDeep = Directory.CreateDirectory(Path.Join(outside, "deep")).FullName;
        File.WriteAllText(Path.Join(outsideDeep, "outside-only.bin"), "must stay outside");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", boundary, outside }) start.ArgumentList.Add(arg);
        using var makeLink = System.Diagnostics.Process.Start(start)!;
        await makeLink.WaitForExitAsync();
        Assert.Equal(0, makeLink.ExitCode);
        try
        {
            File.WriteAllBytes(ordinary, new byte[91]);
            var journal = new Replay();
            journal.Events.Add(new(deepId.Low, deepId.High, fileId.Low, fileId.High, "", 1));
            journal.Events.Add(new(rootId.Low, rootId.High, ordinaryId.Low, ordinaryId.High, "", 1));
            await using var engine = Engine(journal);
            await engine.EnsureReadyAsync(null, default);

            var saved = Store.GetLiveState()!;
            Assert.Equal(new JournalCursor(9, 3), saved.Cursor);
            Assert.Equal("NTFS MFT", saved.Info.Method);
            using var result = Store.OpenSpace(Root, default);
            Assert.Equal(1, result.Files);
            Assert.Equal(2, result.Folders);
            var link = result.Entries[result.Find(boundary)!.Value];
            Assert.True(link.IsSkipped);
            Assert.True(link.Stamp.IsReparsePoint);
            Assert.Empty(result.Children(result.Find(boundary)!.Value));
            Assert.Equal(91, result.Entries[result.Find(ordinary)!.Value].LogicalBytes);
            Assert.Equal(91, result.Entries[0].LogicalBytes);
            Assert.Null(result.Find(obsolete));
            Assert.Null(result.Find(Path.Join(boundary, "deep", "outside-only.bin")));
        }
        finally
        {
            Directory.Delete(boundary);
        }
    }

    [Fact]
    public async Task ARealDirectoryDenyingListAccessKeepsItsPreviouslyIndexedFile()
    {
        var dir = Directory.CreateDirectory(Path.Join(Root, "protected"));
        var child = Path.Join(dir.FullName, "known.txt");
        File.WriteAllText(child, "old");
        var journal = new Replay();
        await using var engine = Engine(journal);
        await engine.EnsureReadyAsync(null, default);
        var parent = IndexEntryReader.Read(dir.FullName)!.Value.Stamp.Identity;
        var id = IndexEntryReader.Read(child)!.Value.Stamp.Identity;
        File.WriteAllBytes(child, new byte[800]);
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        var deny = dir.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(current.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        deny.AddAccessRule(rule);
        dir.SetAccessControl(deny);
        try
        {
            journal.Events.Add(new(parent.Low, parent.High, id.Low, id.High, "", 1));
            await engine.SynchronizeAsync();
            using var result = Store.OpenSpace(Root, default);
            Assert.True(result.Entries[result.Find(dir.FullName)!.Value].Flags.HasFlag(SpaceEntryFlags.AccessDenied));
            Assert.Equal(3, result.Entries[result.Find(child)!.Value].LogicalBytes);
        }
        finally
        {
            var restore = dir.GetAccessControl(AccessControlSections.Access);
            restore.RemoveAccessRuleSpecific(rule);
            dir.SetAccessControl(restore);
        }
    }

    [Fact]
    public void DenseHardLinkRebalancingRecomputesUntrustedDirectoryTotalsAndFlags()
    {
        SpaceEntry Folder(string name, int parent, ulong id) =>
            new(name, parent, new(new(10, id, 0), 1, 1, FileAttributes.Directory, 1), 999, 999, 999);
        var shared = new SpaceStamp(new(10, 90, 7), 1, 1, FileAttributes.Normal, 2);
        SpaceEntry[] rows = [
            Folder("", -1, 50), Folder("A", 0, 51), Folder("Z", 0, 52),
            new("first.bin", 1, shared, 10, 16, 0, SpaceEntryFlags.Shared),
            new("alias.bin", 2, shared, 10, 16, 16, SpaceEntryFlags.Shared),
            new("restricted", 2, new(default, 1, 1, FileAttributes.Normal, 0), 7, -1, 0,
                SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped | SpaceEntryFlags.AccessDenied),
            new("ordinary", 1, new(new(10, 98, 0), 1, 1, FileAttributes.Normal, 1), 3, 3, 3)
        ];
        Store.ReplaceLive(new(Root, 4, 3, null, new Dictionary<SpaceIssueKind, int>(), [], default, false),
            rows.Length, rows, default, new(9, 1));
        using var result = Store.OpenSpace(Root, default);
        var a = result.Entries[result.Find(Path.Join(Root, "A"))!.Value];
        var z = result.Entries[result.Find(Path.Join(Root, "Z"))!.Value];
        Assert.Equal((13L, 19L, 19L, SpaceEntryFlags.Shared), (a.LogicalBytes, a.AllocatedBytes, a.CountedBytes, a.Flags));
        var flags = SpaceEntryFlags.Shared | SpaceEntryFlags.Partial | SpaceEntryFlags.ContainsUnverifiedItems;
        Assert.Equal((17L, 0L, 0L, flags), (z.LogicalBytes, z.AllocatedBytes, z.CountedBytes, z.Flags));
        var root = result.Entries[0];
        Assert.Equal((30L, 19L, 19L, flags), (root.LogicalBytes, root.AllocatedBytes, root.CountedBytes, root.Flags));
    }

    [Fact]
    public async Task ARestartCheckpointsAnUnchangedJournalWithoutReplacingItsGeneration()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Join(Root, "current.txt"), "current");
        var journal = new Replay();
        await using (var first = Engine(journal)) await first.EnsureReadyAsync(null, default);
        var initial = Store.GetLiveState()!;
        journal.Events.AddRange(Enumerable.Repeat(new JournalChange(13, ulong.MaxValue, 9, ulong.MaxValue, "", 1), 2048));
        await using var restarted = Engine(journal);
        await restarted.EnsureReadyAsync(null, default);
        var saved = Store.GetLiveState()!;
        Assert.Equal(initial.Info.Generation, saved.Info.Generation);
        Assert.Equal(new JournalCursor(9, 2049), saved.Cursor);
    }

    private SharedIndexEngine Engine(Replay journal) =>
        new(Cache, "unused-for-folder-scans", () => [Root], journal, TimeSpan.FromDays(1));

    private sealed class Replay : IVolumeJournal
    {
        public List<JournalChange> Events { get; } = [];
        public Action<int>? OnRead;
        private int reads;
        public JournalHead? Head(string root) => new(9, 1, Events.Count + 1);
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token)
        {
            OnRead?.Invoke(++reads);
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(16, through - cursor.Next);
            return new(new(9, cursor.Next + count), Events.GetRange((int)cursor.Next - 1, count));
        }
    }

    private sealed class Files(string root) : IIndexFileSystem
    {
        private readonly Dictionary<string, SpaceEntry> children = new(StringComparer.Ordinal);
        private readonly SpaceEntry folder = new("", -1, new(new(100, 1, 1), 0, 0, FileAttributes.Directory, 1), 0, 0, 0);
        public bool DenyParent;
        public int Listings, Probes;
        public SpaceEntry Add(string name, ulong low, ulong high, int size) =>
            children[name] = new(name, 0, new(new(100, low, high), 0, 0, FileAttributes.Normal, 1), size, size, size);
        public void Resize(string name, int size) => children[name] = children[name] with
            { LogicalBytes = size, AllocatedBytes = size, CountedBytes = size };
        public JournalChange Change(SpaceEntry entry, string name, uint reason) =>
            new(1, 1, entry.Stamp.Identity.Low, entry.Stamp.Identity.High, name, reason);
        public IndexInfo Seed(IndexStore store)
        {
            var total = children.Values.Sum(e => e.CountedBytes);
            return store.ReplaceLive(new(root, children.Count, 1, null, new Dictionary<SpaceIssueKind, int>(), [], default, false),
                children.Count + 1, [folder with { LogicalBytes = total, AllocatedBytes = total, CountedBytes = total }, .. children.Values],
                default, new(9, 1));
        }
        public SpaceEntry? Read(string path) => path == root ? folder :
            children.TryGetValue(Path.GetFileName(path), out var entry) ? entry : null;
        public bool TryReadNamedFile(string path, out SpaceEntry entry)
        {
            ++Probes;
            if (DenyParent) throw new UnauthorizedAccessException();
            return children.TryGetValue(Path.GetFileName(path), out entry);
        }
        public IEnumerable<ListedSpaceFile> Names(string directory)
        {
            ++Listings;
            if (DenyParent) throw new UnauthorizedAccessException();
            return children.Values.Select(e => new ListedSpaceFile(e.Name, e.Stamp.Identity.Low, e.Stamp.Identity.High,
                e.Stamp.Attributes, e.LogicalBytes, e.Stamp.Modified, e.AllocatedBytes, e.Stamp.Changed, e.Stamp.Identity.Volume));
        }
        public IEnumerable<string> HardLinks(string path) => [];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string path, string existing, nint reserved);

    public void Dispose() => Directory.Delete(temp, true);
}
