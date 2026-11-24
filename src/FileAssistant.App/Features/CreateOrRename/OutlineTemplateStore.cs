using System.Text.Json;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed record OutlineTemplate(string Id, string Name, IReadOnlyList<OutlineDraftLine> Lines)
{
    public string PreviewSummary
    {
        get
        {
            if (Lines.Count == 0) return "空模板";
            var top = Lines.Take(4).Select(x => x.Name).ToArray();
            var suffix = Lines.Count > 4 ? $" 等 {Lines.Count} 项" : "";
            return string.Join(" / ", top) + suffix;
        }
    }
}

public static class OutlineTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(FileAssistant.Core.AppProfile.Directory, "outline-templates.json");

    public static IReadOnlyList<OutlineTemplate> DefaultTemplates() =>
    [
        new("builtin-project", "项目三件套", [
            new(0, "01_素材", false),
            new(0, "02_工程文件", false),
            new(0, "03_输出成片", false)
        ]),
        new("builtin-photo", "摄影工作流", [
            new(0, "01_原片", false),
            new(0, "02_精修", false),
            new(0, "03_导出成品", false)
        ]),
        new("builtin-client", "客户交付包", [
            new(0, "新客户项目", false),
            new(1, "合同与报价", false),
            new(1, "参考资料", false),
            new(1, "交付成品", false),
            new(1, "项目说明.txt", true)
        ]),
        new("builtin-quarter", "季度归档", [
            new(0, "Q1", false),
            new(0, "Q2", false),
            new(0, "Q3", false),
            new(0, "Q4", false)
        ])
    ];

    public static List<OutlineTemplate> Load(string? customPath = null)
    {
        try { return ReadForUpdate(customPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return DefaultTemplates().ToList(); }
    }

    internal static List<OutlineTemplate> ReadForUpdate(string? customPath)
    {
        try
        {
            using var file = File.OpenRead(customPath ?? StorePath);
            var list = JsonSerializer.Deserialize<List<OutlineTemplate>>(file, JsonOptions);
            if (list is null || list.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name) || item.Lines is null || item.Lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.Name) || line.Indent < 0)))
                throw new InvalidDataException("模板文件内容不完整。");
            return list;
        }
        catch (FileNotFoundException) { return DefaultTemplates().ToList(); }
        catch (DirectoryNotFoundException) { return DefaultTemplates().ToList(); }
    }

    public static void Save(IReadOnlyList<OutlineTemplate> templates, string? customPath = null)
    {
        var path = Path.GetFullPath(customPath ?? StorePath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".outline-templates-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, templates, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
