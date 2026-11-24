namespace FileAssistant.Core.Settings;

public static class FileDisplay
{
    private static AppSettings settings = new();
    public static void Configure(AppSettings current) => Volatile.Write(ref settings, current);
    public static string Name(string name, bool isFolder) =>
        Name(name, isFolder, Volatile.Read(ref settings).ShowExtensions);

    public static string Name(string name, bool isFolder, bool showExtensions)
    {
        if (showExtensions || isFolder || name.LastIndexOf('.') <= 0) return name;
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string Size(long bytes) => Size(bytes, Volatile.Read(ref settings).Units);
    public static string Size(long bytes, SizeUnits units)
    {
        var step = units == SizeUnits.Decimal ? 1000 : 1024;
        string[] labels = units == SizeUnits.Binary ? ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"] : ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        if (bytes < step) return $"{bytes} B";
        double size = bytes;
        var index = 0;
        while (size >= step && index < labels.Length - 1) { size /= step; index++; }
        return index == 1 ? $"{size:0.#} {labels[index]}" : $"{size:0.##} {labels[index]}";
    }
}
