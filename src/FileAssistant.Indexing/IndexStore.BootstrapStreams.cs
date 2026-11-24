using System.Collections.Concurrent;
using System.IO.Enumeration;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    private static bool CanSplitWalk(string root)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            var entries = new FileSystemEnumerable<bool>(root, (ref FileSystemEntry e) =>
                e.IsDirectory && (e.Attributes & SkipDirectoryTarget) == 0, new EnumerationOptions { AttributesToSkip = 0 });
            // Limit the probe even for a flat directory containing millions of files.
            int directories = 0;
            foreach (bool isDirectory in entries.Take(64))
                if (isDirectory && ++directories == 4) return true;
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private sealed class NameStream : IDisposable
    {
        private readonly CancellationTokenSource stop;
        private readonly BlockingCollection<List<NameRecord>> batches = new(3);
        private readonly ConcurrentQueue<List<NameRecord>> availableBuffers = new();
        private readonly IEnumerator<NameRecord> rows;
        private readonly List<(int First, int Offset)>? idRuns;
        private int localId, previousParent = -1, globalParent;
        public NameRecord Current { get; private set; }
        public Task<(int Files, int Folders, Dictionary<SpaceIssueKind, int> Issues)> Completion { get; }

        public NameStream(string root, string excluded, CancellationToken token, bool preview, int partition)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            rows = ReadBatches().GetEnumerator();
            if (partition >= 0) idRuns = [(0, 0)];
            Completion = Task.Factory.StartNew(() =>
            {
                try
                {
                    const int batchSize = 512;
                    var batch = new List<NameRecord>(batchSize);
                    int observed = 0;
                    void Send(NameRecord row)
                    {
                        batch.Add(row);
                        if (row.Name is not null) observed++;
                        int target = preview && observed <= FirstPageRows + 1 ? 128 : batchSize;
                        if (batch.Count < target) return;
                        batches.Add(batch, stop.Token);
                        batch = availableBuffers.TryDequeue(out var spare) ? spare : new(batchSize);
                    }
                    using var walk = new NameWalk(root, excluded, stop.Token, preview, partition,
                        (id, time) => Send(NameRecord.DirectoryTime(id, time)));
                    while (walk.MoveNext()) Send(walk.Current);
                    walk.FinishUnvisited();
                    if (batch.Count > 0) batches.Add(batch, stop.Token);
                    return (walk.Files, walk.Folders, walk.Issues);
                }
                finally { batches.CompleteAdding(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private IEnumerable<NameRecord> ReadBatches()
        {
            foreach (var batch in batches.GetConsumingEnumerable(stop.Token))
            {
                foreach (var row in batch) yield return row;
                batch.Clear();
                availableBuffers.Enqueue(batch);
            }
        }

        public bool Advance()
        {
            if (!rows.MoveNext())
            {
                Completion.GetAwaiter().GetResult();
                return false;
            }
            var row = rows.Current;
            if (row.Name is not null) localId++;
            if (idRuns is not null)
            {
                if (previousParent != row.DirectoryId)
                {
                    previousParent = row.DirectoryId;
                    int lo = 0, hi = idRuns.Count - 1;
                    while (lo < hi)
                    {
                        int mid = lo + (hi - lo + 1) / 2;
                        if (idRuns[mid].First <= previousParent) lo = mid;
                        else hi = mid - 1;
                    }
                    globalParent = previousParent + idRuns[lo].Offset;
                }
                row = row with { DirectoryId = globalParent };
            }
            Current = row;
            return true;
        }

        public void SetDirectoryId(int id)
        {
            // Between interleavings both row counters advance together. Only directory IDs are
            // referenced later, so one change of offset represents an entire run of directories.
            if (Current.DirectoryPath is not null && idRuns != null && id - localId != idRuns[^1].Offset)
                idRuns.Add((localId, id - localId));
        }

        public void Dispose()
        {
            stop.Cancel();
            // Advance propagates a failed walk. During unwinding, stop the other producer
            // even when it is blocked on its bounded queue, without masking the first error.
            try { Completion.GetAwaiter().GetResult(); }
            catch (Exception) when (Completion.IsFaulted || Completion.IsCanceled) { }
            rows.Dispose();
            batches.Dispose();
            stop.Dispose();
        }
    }

    private static IEnumerable<NameRecord> MergeNameStreams(NameRecord root, NameStream first, NameStream? second)
    {
        yield return root;
        if (second is null)
        {
            while (first.Advance()) yield return first.Current;
            yield break;
        }
        int id = 1;
        bool left = first.Advance(), right = second.Advance();
        // Each Windows enumerator is breadth-first. Root order restores its original
        // sibling sequence, then the published parent ID orders all later directory runs.
        while (left || right)
        {
            var a = first.Current;
            var b = second.Current;
            bool chooseFirst = left && (!right || a.Name is null || b.Name is not null &&
                (a.DirectoryId < b.DirectoryId || a.DirectoryId == b.DirectoryId && a.RootOrder <= b.RootOrder));
            var source = chooseFirst ? first : second;
            if (source.Current.Name is not null) source.SetDirectoryId(id++);
            yield return source.Current;
            if (chooseFirst) left = first.Advance();
            else right = second.Advance();
        }
    }
}
