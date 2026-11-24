using CommunityToolkit.Mvvm.ComponentModel;
using FileAssistant.App.Controls;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    [ObservableProperty] public partial string FileExtension { get; set; } = ".txt";
    [ObservableProperty] public partial int FileContentValue { get; set; }
    [ObservableProperty] public partial int FileExtensionPresetValue { get; set; }
    private bool syncingExtensionPreset;
    public bool IsFileCreation => IsCreate && KindValue != 0;
    public bool IsCustomFileExtension => IsFileCreation && FileExtensionPresetValue == FileExtensionPresets.Length;
    public IReadOnlyList<string> FileExtensions { get; } = [".txt", ".md", ".docx", ".doc"];
    private static readonly string[] FileExtensionPresets = [".txt", ".md", ".docx", ".doc", ".xlsx", ".pptx", ".pdf", ".json", ".csv"];
    public IReadOnlyList<SelectionOption> FileExtensionOptions { get; } = [
        new(0, ".txt"), new(1, ".md"), new(2, ".docx"), new(3, ".doc"),
        new(4, ".xlsx"), new(5, ".pptx"), new(6, ".pdf"), new(7, ".json"), new(8, ".csv"),
        new(9, "自定义…")
    ];
    public IReadOnlyList<SelectionOption> FileContents { get; } = [new(0, "空白文档"), new(1, "复制我的模板"), new(2, "空文件（仅占位）")];
    public IReadOnlyList<SelectionOption> CreationInputModes { get; } = [new(0, "填写名称"), new(1, "编号生成"), new(2, "组合模板")];
    public int CreationInputMode
    {
        get => CreateNumbered ? 1 : Custom ? 2 : 0;
        set => CreateMethod = value == 1 ? 0 : value == 2 ? 2 : 3;
    }
    public string FileContentHint => FileContentValue == 1 ? "按模板原内容复制，扩展名须与模板一致。"
        : FileContentValue == 2 ? "仅创建零字节占位文件；不会生成可打开的 Word 等文档。"
        : FileExtension.Trim().ToLowerInvariant() is ".doc" or ".docx" ? "使用内置空白 Word 模板。"
        : FileExtension.Trim().ToLowerInvariant() is ".txt" or ".md" ? "创建空白文本文件。"
        : "此格式需要选择自己的模板；只要占位时可选择“空文件”。";
    private string NormalizedExtension()
    {
        var extension = FileExtension.Trim();
        if (extension.Length == 0) throw new ArgumentException("请填写文件扩展名，例如 .docx。");
        if (!extension.StartsWith('.')) extension = "." + extension;
        NamingRulesEngine.ValidateName("文件" + extension);
        if (extension.Length < 2 || extension[1..].Contains('.')) throw new ArgumentException("请填写一个扩展名，例如 .docx。");
        return extension.ToLowerInvariant();
    }
    private string CreationTemplateFor(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (FileContentValue == 2) return "";
        if (FileContentValue == 1)
        {
            if (string.IsNullOrWhiteSpace(Template)) throw new ArgumentException("请先选择模板文件。");
            if (!string.Equals(Path.GetExtension(Template), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("文件扩展名与模板不一致：" + name);
            return Template.Trim();
        }
        if (extension is ".txt" or ".md") return "";
        if (extension is ".doc" or ".docx") return Path.Combine(AppContext.BaseDirectory, "Assets", "CreationTemplates", "blank" + extension);
        throw new ArgumentException($"{extension} 没有内置空白模板。请选择自己的模板，或选择“空文件（仅占位）”。");
    }
    partial void OnFileExtensionPresetValueChanged(int value)
    {
        if (!syncingExtensionPreset && value >= 0 && value < FileExtensionPresets.Length)
        {
            syncingExtensionPreset = true;
            try { FileExtension = FileExtensionPresets[value]; }
            finally { syncingExtensionPreset = false; }
        }
        OnPropertyChanged(nameof(IsCustomFileExtension));
    }
    partial void OnFileExtensionChanged(string value)
    {
        if (!syncingExtensionPreset)
        {
            syncingExtensionPreset = true;
            try
            {
                var idx = Array.FindIndex(FileExtensionPresets, x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
                FileExtensionPresetValue = idx >= 0 ? idx : FileExtensionPresets.Length;
            }
            finally { syncingExtensionPreset = false; }
        }
        creationInputError = "";
        NotifyCreationEditor();
    }
    partial void OnFileContentValueChanged(int value) { creationInputError = ""; NotifyCreationEditor(); }
    partial void OnTemplateChanged(string value)
    {
        if (Path.GetExtension(value) is { Length: > 0 } extension) FileExtension = extension;
        creationInputError = "";
    }
    private void NotifyCreationFiles()
    {
        foreach (var name in new[] { nameof(IsFileCreation), nameof(IsCustomFileExtension), nameof(IsTemplateCreation), nameof(FileContentHint), nameof(CreationInputMode), nameof(CreationInputModes), nameof(NamingExample), nameof(NextStep) }) OnPropertyChanged(name);
    }
}
