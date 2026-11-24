using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    internal const string NameIndexMethod = "目录名称索引";
    internal const string PartialNameNotice = "正在构建名称索引";
    private const int FirstPageRows = 1024;
    private const FileAttributes SkipDirectoryTarget = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00440000;

    // Only an empty or interrupted name index can be replaced by this phase.
    public bool PublishSearchNames(string excluded, CancellationToken token) => PublishSearchNames(excluded, token, null);

    public bool PublishSearchNames(string excluded, CancellationToken token, Action? firstPageAvailable)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(path, true);
        CheckVersion(db);
        using var settings = db.CreateCommand();
        settings.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-2048;";
        settings.ExecuteNonQuery();
        using var stop = InterruptOnCancel(db, token);
        var tx = db.BeginTransaction();
        try
        {
            var previous = CheckVersion(db, tx) == 0 ? null : ReadInfo(db, tx);
            if (previous is { Method: not NameIndexMethod }) return false;
            Run(db, tx, ResetSnapshot + DirectorySchema);
            // Never replace an already complete name snapshot with a smaller interim one.
            bool preview = firstPageAvailable is not null && (previous is null || previous.IsBuildingNames);
            var lookup = new BootstrapLookup(root, Guid.NewGuid().ToString("N"));
            var (files, folders, issues) = WriteNames(db, excluded, token, preview ? PublishFirstPage : null, lookup);
            lookup.Finish(files + folders);
            CommitNames(files, folders, issues.Count == 0 ? "" : $"{issues.Values.Sum():N0} 个目录未能完整读取", JsonSerializer.Serialize(issues), lookup.Generation);
            Volatile.Write(ref bootstrapLookup, lookup);
            return true;

            void PublishFirstPage(int filesSoFar, int foldersSoFar)
            {
                var generation = CommitNames(filesSoFar, foldersSoFar, PartialNameNotice, "{}");
                tx.Dispose();
                firstPageAvailable!();
                token.ThrowIfCancellationRequested();
                tx = db.BeginTransaction();
                if (ReadInfo(db, tx)?.Generation != generation)
                    throw new IOException("名称索引已由另一个写入者更新。");
            }
            string CommitNames(int fileCount, int folderCount, string notice, string issuesJson, string? generation = null)
            {
                token.ThrowIfCancellationRequested();
                generation ??= Guid.NewGuid().ToString("N");
                Run(db, tx, """
                    INSERT OR REPLACE INTO scan VALUES(1,$p0,$p1,$p2,$p3,$p4,0,$p5,$p6,'null',$p7,'[]')
                    """, root, generation, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), fileCount,
                    folderCount, NameIndexMethod, notice, issuesJson);
                tx.Commit();
                return generation;
            }
        }
        finally { tx.Dispose(); }
    }

    private readonly record struct NameRecord(int DirectoryId, string? Name, string? DirectoryPath, FileAttributes Attributes, long Length, long Modified, string? ParentPath = null, int RootOrder = 0)
    {
        public static NameRecord DirectoryTime(int id, long modified) => new(id, null, null, 0, 0, modified);
    }
    private (int Files, int Folders, Dictionary<SpaceIssueKind, int> Issues) WriteNames(SqliteConnection db, string excluded,
        CancellationToken token, Action<int, int>? publishFirstPage, BootstrapLookup lookup)
    {
        void Check(int rc)
        {
            if (rc != raw.SQLITE_OK) SqliteException.ThrowExceptionForRC(rc, db.Handle);
        }
        sqlite3_stmt Prepare(string sql)
        {
            Check(raw.sqlite3_prepare_v2(db.Handle!, sql, out var statement));
            return statement;
        }
        void Next(sqlite3_stmt statement)
        {
            var rc = raw.sqlite3_step(statement);
            if (rc != raw.SQLITE_DONE) Check(rc);
            Check(raw.sqlite3_reset(statement));
        }
        using var insert = Prepare("""
            INSERT INTO entries(id,parent,name,upper_override,volume,file_low,file_high,modified,changed,attrs,links,logical,allocated,counted,flags,kind)
            VALUES(?,?,?,?,0,0,0,?,0,?,0,?,-1,0,17,?)
            """);
        using var directory = Prepare("INSERT INTO live_dirs(id,path) VALUES(?,?)");
        using var updateTime = Prepare("UPDATE entries SET modified=? WHERE id=?");
        // OS directory enumeration and SQLite writes can overlap. Only this thread owns the transaction.
        bool previewRequested = publishFirstPage is not null;
        var attributesAtRoot = File.GetAttributes(root);
        if ((attributesAtRoot & (FileAttributes.Directory | SkipDirectoryTarget)) != FileAttributes.Directory)
            throw new IOException("请选择可直接读取的磁盘或文件夹。");
        var info = new DirectoryInfo(root);
        var rootRow = new NameRecord(-1, info.Name, root, attributesAtRoot, 0, info.LastWriteTimeUtc.ToFileTimeUtc(), root);
        bool split = CanSplitWalk(root);
        using var first = new NameStream(root, excluded, token, previewRequested, split ? 0 : -1);
        using var second = split ? new NameStream(root, excluded, token, previewRequested, 1) : null;

        int id = 0, directoryCount = 0;
        int lastParent = int.MinValue, lastAttributes = -1, lastKind = -1;
        long lastLength = -1, lastModified = long.MinValue;
        Span<byte> utf8 = stackalloc byte[1024];
        foreach (var row in MergeNameStreams(rootRow, first, second))
        {
            if (row.Name is null)
            {
                Check(raw.sqlite3_bind_int64(updateTime, 1, row.Modified));
                Check(raw.sqlite3_bind_int64(updateTime, 2, row.DirectoryId));
                Next(updateTime);
                continue;
            }
            // Only split the transaction when a subsequent record actually exists.
            if (id == FirstPageRows && publishFirstPage is not null)
            {
                publishFirstPage(id - directoryCount, directoryCount);
                publishFirstPage = null;
            }
            // reset() keeps its bindings. Directory siblings usually share the parent,
            // attributes and kind; after the explicit root row SQLite assigns sequential rowids.
            if (id == 0) Check(raw.sqlite3_bind_int(insert, 1, 0));
            else if (id == 1) Check(raw.sqlite3_bind_null(insert, 1));
            if (lastParent != row.DirectoryId)
                Check(raw.sqlite3_bind_int(insert, 2, lastParent = row.DirectoryId));
            if (row.Name.Length <= utf8.Length / 3)
                Check(raw.sqlite3_bind_text(insert, 3, utf8[..Encoding.UTF8.GetBytes(row.Name, utf8)]));
            else
                Check(raw.sqlite3_bind_text(insert, 3, row.Name));
            if (UpperOverride(row.Name) is { } upper)
                Check(raw.sqlite3_bind_text(insert, 4, upper));
            else
                Check(raw.sqlite3_bind_null(insert, 4));
            if (lastModified != row.Modified)
                Check(raw.sqlite3_bind_int64(insert, 5, lastModified = row.Modified));
            int attributes = (int)row.Attributes, kind = row.DirectoryPath is null ? 0 : 1;
            if (lastAttributes != attributes)
                Check(raw.sqlite3_bind_int(insert, 6, lastAttributes = attributes));
            if (lastLength != row.Length)
                Check(raw.sqlite3_bind_int64(insert, 7, lastLength = row.Length));
            if (lastKind != kind)
                Check(raw.sqlite3_bind_int(insert, 8, lastKind = kind));
            Next(insert);
            if (row.DirectoryPath is { } location)
            {
                directoryCount++;
                Check(raw.sqlite3_bind_int64(directory, 1, id));
                Check(raw.sqlite3_bind_text(directory, 2, location));
                Next(directory);
            }
            lookup.Observe(id, row.DirectoryId, row.Name, row.ParentPath!, row.Attributes);
            id++;
        }
        var (files, folders, issues) = first.Completion.GetAwaiter().GetResult();
        if (second is not null)
        {
            var other = second.Completion.GetAwaiter().GetResult();
            files += other.Files;
            folders += other.Folders - 1;
            foreach (var item in other.Issues) issues[item.Key] = issues.GetValueOrDefault(item.Key) + item.Value;
        }
        return (files, folders, issues);
    }

    private sealed class NameWalk : FileSystemEnumerator<NameRecord>
    {
        private readonly string excluded;
        private readonly string excludedName;
        private readonly CancellationToken token;
        private readonly Dictionary<(string Parent, string Name), (int Id, long ListedTime)> pending = new(new PendingPathComparer());
        private readonly bool ntfsTimes;
        private readonly bool previewRequested;
        private readonly Action<int, long> correctTime;
        private readonly int partition, rootLength;
        private int rootOrder;
        private string current;
        private int parent;
        public int Files { get; private set; }
        public int Folders { get; private set; } = 1;
        public Dictionary<SpaceIssueKind, int> Issues { get; } = [];

        public NameWalk(string root, string excluded, CancellationToken token, bool previewRequested, int partition, Action<int, long> correctTime)
            : base(root, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
                ReturnSpecialDirectories = OperatingSystem.IsWindows(), AttributesToSkip = 0, BufferSize = 65536 })
        {
            this.excluded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(excluded));
            excludedName = Path.GetFileName(this.excluded);
            this.token = token;
            this.previewRequested = previewRequested;
            this.correctTime = correctTime;
            this.partition = partition;
            rootLength = root.Length;
            if (OperatingSystem.IsWindows())
            {
                try { ntfsTimes = new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            current = root;
        }
        private bool Excluded(ref FileSystemEntry entry) => entry.IsDirectory &&
            entry.FileName.Equals(excludedName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.ToFullPath(), excluded, StringComparison.OrdinalIgnoreCase);

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            token.ThrowIfCancellationRequested();
            bool self = entry.FileName.SequenceEqual(".");
            if (!entry.Directory.SequenceEqual(current))
            {
                if (!pending.GetAlternateLookup<ReadOnlySpan<char>>().Remove(entry.Directory, out _, out var dir))
                    throw new IOException("目录在枚举时已更换。");
                current = entry.Directory.ToString();
                parent = dir.Id;
                if (ntfsTimes)
                {
                    // NTFS returns the directory's own timestamp in "." as part of this enumeration.
                    // Its entry in the parent may still carry an older timestamp.
                    var actual = self ? entry.LastWriteTimeUtc.ToFileTime() : ReadDirectoryTime(current);
                    if (actual > 0 && actual != dir.ListedTime) correctTime(dir.Id, actual);
                }
            }
            if (self || entry.FileName.SequenceEqual("..") || Excluded(ref entry)) return false;
            if (parent == 0 && partition >= 0) rootOrder++;
            return Owned(ref entry);
        }
        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) =>
            (entry.Attributes & SkipDirectoryTarget) == 0 && !Excluded(ref entry) && Owned(ref entry);
        private bool Owned(ref FileSystemEntry entry)
        {
            if (partition < 0 || entry.Directory.Length != rootLength) return true;
            uint hash = 2166136261;
            // Both readers enumerate the root and must independently pick the same owner,
            // even if another process is adding root entries between those enumerations.
            foreach (char c in entry.FileName) hash = unchecked((hash ^ c) * 16777619);
            return (hash & 1) == partition;
        }
        protected override bool ContinueOnError(int error)
        {
            if (error is not (2 or 3 or 5 or 32 or 303) || Issues is null) return false;
            var kind = error is 5 or 32 ? SpaceIssueKind.Access : SpaceIssueKind.Changed;
            Issues[kind] = Issues.GetValueOrDefault(kind) + 1;
            return true;
        }
        protected override void OnDirectoryFinished(ReadOnlySpan<char> path)
        {
            var lookup = pending.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.Remove(path, out _, out var dir)) CorrectUnvisited(path.ToString(), dir);
        }

        public void FinishUnvisited()
        {
            // Opening a child for enumeration can fail even when its attributes are readable.
            foreach (var (child, dir) in pending) CorrectUnvisited(Path.Join(child.Parent, child.Name), dir);
        }

        private void CorrectUnvisited(string path, (int Id, long ListedTime) dir)
        {
            token.ThrowIfCancellationRequested();
            if (!ntfsTimes) return;
            var actual = ReadDirectoryTime(path);
            if (actual > 0 && actual != dir.ListedTime) correctTime(dir.Id, actual);
        }

        private static long ReadDirectoryTime(string path)
        {
            try { return Directory.GetLastWriteTimeUtc(path).ToFileTimeUtc(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
        }

        protected override NameRecord TransformEntry(ref FileSystemEntry entry)
        {
            string? path = null;
            var name = entry.FileName.ToString();
            var modified = entry.LastWriteTimeUtc.ToFileTime();
            if (entry.IsDirectory)
            {
                path = entry.ToFullPath();
                if (ShouldRecurseIntoEntry(ref entry))
                {
                    var id = checked(Files + Folders);
                    // A preview may commit before these directories have been visited.
                    if (OperatingSystem.IsWindows() && (!ntfsTimes || previewRequested && id < FirstPageRows))
                    {
                        var actual = ReadDirectoryTime(path);
                        if (actual > 0) modified = actual;
                    }
                    pending[(current, name)] = (id, modified);
                }
                Folders++;
            }
            else Files++;
            var attributes = entry.Attributes;
            return new(parent, name, path, attributes, path is null ? entry.Length : 0, modified, current, parent == 0 ? rootOrder : 0);
        }

        // The enumerator already owns a full path for every queued directory. Retain one parent
        // string shared by siblings and the spelling already used for the entry. Split the key
        // from Windows on lookup so case-distinct paths and hash collisions still compare exactly.
        private sealed class PendingPathComparer : IEqualityComparer<(string Parent, string Name)>,
            IAlternateEqualityComparer<ReadOnlySpan<char>, (string Parent, string Name)>
        {
            public bool Equals((string Parent, string Name) x, (string Parent, string Name) y) => x == y;
            public int GetHashCode((string Parent, string Name) path) => Hash(path.Parent, path.Name);
            public bool Equals(ReadOnlySpan<char> path, (string Parent, string Name) entry) =>
                Path.GetFileName(path).SequenceEqual(entry.Name) && Path.GetDirectoryName(path).SequenceEqual(entry.Parent);
            public int GetHashCode(ReadOnlySpan<char> path) => Hash(Path.GetDirectoryName(path), Path.GetFileName(path));
            public (string Parent, string Name) Create(ReadOnlySpan<char> path) =>
                (Path.GetDirectoryName(path).ToString(), Path.GetFileName(path).ToString());
            private static int Hash(ReadOnlySpan<char> parent, ReadOnlySpan<char> name) =>
                HashCode.Combine(string.GetHashCode(parent, StringComparison.Ordinal), string.GetHashCode(name, StringComparison.Ordinal));
        }
    }
}
