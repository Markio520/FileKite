using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Core.Tests;

public sealed class SpaceAnalysisRulesTests
{
    [Fact]
    public void UnreadableDirectoryAndEmptyDirectoryHaveDifferentAllocationSemantics()
    {
        var folder = new SpaceEntry("folder", 0, new(default, 0, 0, FileAttributes.Directory, 1), 0, 0, 0);
        Assert.False(folder.UnknownAllocation);
        Assert.True((folder with { AllocatedBytes = -1 }).UnknownAllocation);
        Assert.True((folder with { Flags = SpaceEntryFlags.Partial | SpaceEntryFlags.AccessDenied }).UnknownAllocation);
        Assert.False((folder with { Flags = SpaceEntryFlags.Partial, AllocatedBytes = 4096 }).UnknownAllocation);
        var disk = new SpaceVolume(@"C:\", 100, 30);
        Assert.Equal(70, disk.UsedBytes);
        Assert.Equal(10, disk.DifferenceFrom(60));
        Assert.Equal(-5, disk.DifferenceFrom(75));
    }

    [Fact]
    public void TreemapKeepsAreasProportionalWhenSmallItemsAreGrouped()
    {
        var tiles = SpaceTreemap.Layout([(0, 60), (1, 20), (2, 1)], 22, 100, 400, 100);
        Assert.Equal(3, tiles.Length);
        Assert.Equal(40000, tiles.Sum(tile => tile.Width * tile.Height), precision: 5);
        Assert.Equal(24000, tiles.Single(tile => tile.Id == 0).Width * tiles.Single(tile => tile.Id == 0).Height, precision: 5);
        var other = tiles.Single(tile => tile.Id == -1);
        Assert.Equal(20, other.Grouped);
        Assert.Equal(20, other.Bytes);
    }

    [Fact]
    public void RecyclingDirectoryInvalidatesItsReferencesWithoutMatchingNamePrefixes()
    {
        var root = Path.GetFullPath("space-references");
        var removed = Path.Combine(root, "build");
        var affected = Path.Combine(removed, "file.txt");
        var retained = Path.Combine(root, "build-keep", "file.txt");
        var collection = new FileCollectionSnapshot(1, [],
            [new(Guid.NewGuid(), Guid.Empty, new SearchFile(affected, false, 2, null)),
             new(Guid.NewGuid(), Guid.Empty, new SearchFile(retained, false, 2, null))]);
        var updated = FileCollection.Remap(collection, [], [removed], "已移入回收站");
        Assert.Equal("已移入回收站", updated.Entries.Single(entry => entry.File.FullPath == affected).Error);
        Assert.Null(updated.Entries.Single(entry => entry.File.FullPath == retained).Error);
    }
}
