using System.ComponentModel;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Infrastructure.FileSystem;

/// <summary>Reads names and metadata without following reparse points or opening file contents.</summary>
public static class IndexEntryReader
{
    public static SpaceEntry? Read(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { } parent) SpaceScanner.CheckAncestors(parent);
            return SpaceScanner.MakeEntry(Path.GetFileName(path), -1, SpaceFileMetadata.ReadForScan(path, 0));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or Win32Exception { NativeErrorCode: 2 or 3 })
        { return null; }
    }

    public static IEnumerable<ListedSpaceFile> Names(string directory)
    {
        SpaceScanner.CheckAncestors(directory);
        using var handle = SpaceFileMetadata.Open(directory, listDirectory: true);
        var stamp = SpaceFileMetadata.ReadDirectoryStamp(handle);
        if (!stamp.IsDirectory || stamp.IsReparsePoint || SpaceFileMetadata.IsOffline(stamp.Attributes)) yield break;
        foreach (var child in new SpaceDirectoryReader().Read(handle, directory))
            yield return new(child.Name, child.IdLow, child.IdHigh, child.Attributes, child.Length, child.Modified, child.Allocation, child.Changed, stamp.Identity.Volume);
    }
}
