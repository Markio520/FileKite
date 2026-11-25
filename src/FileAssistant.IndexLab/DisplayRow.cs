using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Indexing;

namespace FileAssistant.IndexLab;

public sealed record DisplayRow(string Name, string Detail, string Size, string Usage, string Glyph, string Hint,
    IndexRow? File = null, SpaceKind? Category = null)
{
    public static DisplayRow From(IndexRow item)
    {
        var e = item.Entry;
        var usage = Bytes(e.AllocatedBytes);
        var hint = item.Path;
        if (e.IsPartial) usage = e.AllocatedBytes > 0 ? "≥ " + usage : "未知";
        if (e.IsShared && !e.IsDirectory) hint += $"\n硬链接 · {e.Stamp.Links} 个名称，计入目录 {Bytes(e.CountedBytes)}";
        return new(e.Name, item.Path, Bytes(e.LogicalBytes), usage, e.IsDirectory ? "\uE8B7" : "\uE8A5", hint, item);
    }

    public static DisplayRow From(SpaceTypeTotal type)
        => new(SpaceKinds.Label(type.Kind), type.Unknown > 0 ? $"{type.Unknown:N0} 项占用未知" : "双击查看此类型的大文件",
            $"{type.Count:N0} 项", Bytes(type.Bytes), "\uE8EC", SpaceKinds.Label(type.Kind), Category: type.Kind);

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "未知";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
