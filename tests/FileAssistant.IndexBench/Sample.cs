using System.Collections;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.IndexBench;

internal sealed class Sample : IReadOnlyList<SpaceEntry>
{
    public const string Root = @"X:\IndexFixture";
    public const int Files = 1_155_000;
    public const int Folders = 151;
    public int Count => Files + Folders;
    private static readonly (long Logical, long Counted) PerDirectory = Totals();
    private static readonly long Time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    public SpaceEntry this[int id]
    {
        get
        {
            var folder = id < Folders;
            if (folder)
            {
                int branches = id == 0 ? 150 : id <= 30 ? 5 : 1;
                var parent = id == 0 ? -1 : id <= 30 ? 0 : (id - 31) % 30 + 1;
                return new(id == 0 ? Root : $"分组-{id:D3}", parent, Stamp((ulong)id + 1, true, false),
                    PerDirectory.Logical * branches, PerDirectory.Counted * branches, PerDirectory.Counted * branches);
            }
            int n = id - Folders, local = n / 150;
            bool alias = local % 113 == 1;
            bool shared = alias || local % 113 == 0;
            var (logical, allocated) = Sizes(alias ? local - 1 : local);
            string name = (n % 17 == 0 ? "项目素材-" : n % 7 == 0 ? "设计草稿-" : "asset-") + n.ToString("D7") + (alias ? ".lnkname" : n % 2 == 0 ? ".mp4" : ".pdf");
            return new(name, n % 150 + 1, Stamp((ulong)(alias ? id - 150 : id) + 1, false, shared), logical,
                allocated, alias ? 0 : allocated, shared ? SpaceEntryFlags.Shared : SpaceEntryFlags.None);
        }
    }
    private static SpaceStamp Stamp(ulong n, bool dir, bool shared) => new(new(55, n, 0), Time, Time,
        dir ? FileAttributes.Directory : FileAttributes.Archive, shared ? 2u : 1u);
    private static (long Logical, long Allocated) Sizes(int n)
    {
        var logical = 18L + (n * 7213L) % 7_100_000;
        return (logical, n % 89 == 0 ? 0 : ((logical + 4095) / 4096) * 4096);
    }
    private static (long, long) Totals()
    {
        long logical = 0, counted = 0;
        for (int i = 0; i < Files / 150; i++) { var alias = i % 113 == 1; var size = Sizes(alias ? i - 1 : i); logical += size.Logical; if (!alias) counted += size.Allocated; }
        return (logical, counted);
    }
    public IEnumerator<SpaceEntry> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
