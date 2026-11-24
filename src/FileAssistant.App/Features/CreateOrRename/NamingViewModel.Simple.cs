using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed record NamingPreviewRow(string OriginalName, string NewName, string Status, string Details, bool NeedsAttention = false, string? SourcePath = null);

public sealed record NamingSamplePart(string Text, bool IsChange)
{
    public bool IsPlain => !IsChange;
}

public sealed record NamingRuleSample(string Original, string Target, IReadOnlyList<NamingSamplePart> Parts)
{
    public static NamingRuleSample Compare(string original, string target)
    {
        if (original == target || original.Length == 0 || Math.Max(original.Length, target.Length) > 255)
            return new(original, target, [new(target, target != original)]);

        // Highlight only added/replaced text in the target, keeping complete Unicode text elements together.
        static string[] Elements(string text)
        {
            var list = new List<string>();
            var it = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (it.MoveNext()) list.Add(it.GetTextElement());
            return list.ToArray();
        }

        var before = Elements(original);
        var after = Elements(target);
        var common = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
            for (var j = after.Length - 1; j >= 0; j--)
                common[i, j] = before[i] == after[j] ? 1 + common[i + 1, j + 1] : Math.Max(common[i + 1, j], common[i, j + 1]);

        var parts = new List<NamingSamplePart>();
        for (int i = 0, j = 0; j < after.Length;)
        {
            if (i < before.Length && before[i] != after[j] && common[i + 1, j] >= common[i, j + 1]) { i++; continue; }
            var added = i == before.Length || before[i] != after[j];
            var text = after[j++];
            if (!added) i++;
            if (parts.Count > 0 && parts[^1].IsChange == added)
                parts[^1] = new(parts[^1].Text + text, added);
            else parts.Add(new(text, added));
        }
        return new(original, target, parts);
    }
}

public partial class NamingViewModel
{
    public IReadOnlyList<SelectionOption> RenameMethods { get; } = [
        new(6, "添加编号"), new(1, "替换文字"), new(2, "添加文字"), new(3, "删除文字"),
        new(0, "重设名称"), new(7, "逐项改名"), new(4, "大小写"), new(5, "组合模板")];
    public string PrimaryActionLabel => IsRename ? "执行改名" : "创建全部";
    public NamingRuleSample RuleSample { get; private set; } = new("", "", []);

    private void RefreshRuleSample()
    {
        if (!IsRename) return;
        string original, target;
        if (Plan is { Request.Mode: NamingMode.Rename } plan && plan.Items.FirstOrDefault() is { } item)
        {
            original = item.OriginalName;
            target = item.NewName;
        }
        else
        {
            var file = Input.EffectiveEntries.FirstOrDefault();
            original = file?.Name ?? "文件";
            var extension = file?.IsFolder == true ? "" : Path.GetExtension(original);
            var stem = original[..(original.Length - extension.Length)];
            try
            {
                var result = Manual ? file is null ? stem : customNames.GetValueOrDefault(file.FullPath, stem)
                    : NamingRulesEngine.Generate(stem, Path.GetFileName(file?.Directory) ?? "父目录",
                        file?.Modified?.ToLocalTime() ?? DateTimeOffset.Now, 0, SimpleRules());
                target = result + extension;
            }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            { original = target = "待调整"; }
        }
        if (RuleSample.Original == original && RuleSample.Target == target) return;
        RuleSample = NamingRuleSample.Compare(original, target);
        OnPropertyChanged(nameof(RuleSample));
    }

    [ObservableProperty] public partial int RenameAction { get; set; } = 6;
    [ObservableProperty] public partial int CreateMethod { get; set; } = 3;
    [ObservableProperty] public partial int DeleteMethod { get; set; }
    [ObservableProperty] public partial int SeparatorValue { get; set; }
    [ObservableProperty] public partial string RenameBaseName { get; set; } = "";
    [ObservableProperty] public partial string CreateBaseName { get; set; } = "新建";
    [ObservableProperty] public partial IReadOnlyList<NamingPreviewRow> Rows { get; set; } = [];
    [ObservableProperty] public partial NamingPreviewRow? SelectedRow { get; set; }
    public IReadOnlyList<SelectionOption> Separators { get; } = [new(0, "下划线  _"), new(1, "短横线  -"), new(2, "空格"), new(3, "直接连接")];
    public IReadOnlyList<SelectionOption> CreationMethods => IsDirectoryCreation
        ? [new(3, "编辑目录树"), new(0, "连续编号"), new(1, "按名称清单"), new(2, "高级格式")]
        : [new(0, "连续编号"), new(1, "按名称清单"), new(2, "高级格式")];
    public IReadOnlyList<SelectionOption> DeleteMethods { get; } = [new(0, "删除指定文字"), new(1, "按字符位置删除")];
    private string Separator => SeparatorValue switch { 1 => "-", 2 => " ", 3 => "", _ => "_" };
    public bool Uniform => IsRename && RenameAction == 0;
    public bool Replace => IsRename && RenameAction == 1;
    public bool AddText => IsRename && RenameAction == 2;
    public bool Delete => IsRename && RenameAction == 3;
    public bool ChangeCase => IsRename && RenameAction == 4;
    public bool Custom => IsRename && RenameAction == 5 || IsCreate && CreateMethod == 2;
    public bool DeleteByText => Delete && DeleteMethod == 0;
    public bool DeleteByPosition => Delete && DeleteMethod == 1;
    public bool CreateNumbered => IsCreate && CreateMethod == 0;
    public bool CreateList => IsCreate && CreateMethod == 1;
    public bool CreationByName => IsCreate && !CreateNumbered && !Custom;
    public bool ShowNames => CreateList || IsCreate && Custom;
    public bool ShowCount => IsCreate && CreateMethod == 2;
    public bool ShowNumbering => NumberOnly || Uniform || CreateNumbered || Custom;
    public bool ShowSeparator => NumberOnly || Uniform || CreateNumbered;
    public bool HasRows => Rows.Count > 0;
    public bool Empty => !HasRows;
    public bool ShowResults => Report is not null && Plan is null;
    public bool IsImporting => IsRename && Input.IsLoading;
    public bool CanCancelImport => IsRename && Input.CanCancelLoading;
    public bool CanUndoLast => !IsBusy && Report?.RecoveryId is { } id && Recoveries.Any(x => x.Id == id && x.Remaining > 0);
    public string PageHint => IsRename ? "添加文件，选择改名方式，核对结果后执行。扩展名自动保留。" : "选择保存位置，填写名称，一次创建多个文件或文件夹。";
    public string PreviewTitle => ShowResults ? "本次处理结果" : IsRename ? "改名预览" : "创建预览";
    public string OptionsTitle => IsRename ? "项目设置 · 改名" : "项目设置 · 创建";
    public bool HasNamingPreview => Plan is not null || Report is not null;
    public string OriginalHeader => IsRename ? "现在的名称" : "创建类型";
    public string NewHeader => ShowResults ? "处理后的名称" : IsRename ? "改名后" : "将要创建";
    public string RowDetails => SelectedRow?.Details ?? "点击一行查看完整路径。";
    public string EmptyTitle => IsRename ? "把需要改名的文件放到这里" : IsTreeCreation ? "填写名称，预览会出现在左侧" : "新名称将在这里预览";
    public string EmptyHint => IsRename ? "可直接拖入文件，也可以点击上方“添加文件”。" : "左侧选择父目录，右侧填写名称；确认创建后才会写入磁盘。";
    public string MissingInput
    {
        get
        {
            if (IsRename)
            {
                if (Manual) return Input.SearchEntries.Count == 0 ? "先添加需要改名的文件。" : CustomNameCount == 0 ? "在中央文件列表填写新名称，扩展名自动保留。" : "";
                if (Input.HasSamples || Input.EffectiveEntries.Count == 0) return "先添加需要改名的文件。";
                if (Uniform && string.IsNullOrWhiteSpace(RenameBaseName)) return "填写统一名称，例如“旅行照片”。";
                if (Replace && Find.Length == 0) return "填写要替换的文字。";
                if (AddText && Prefix.Length == 0 && Suffix.Length == 0) return "填写要添加到名称前面或后面的文字。";
                if (DeleteByText && DeleteText.Length == 0) return "填写要从名称中删除的文字。";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Destination)) return "先选择保存位置。";
                if (folderLocation.IsMissing) return "保存位置已不存在，请在右上角重新选择文件夹。";
                if (DraftEntries().Any(x => x.Node.IsMissing)) return "有父目录已被删除；未创建内容已保留，请更换保存位置或移出该草稿分支。";
                if (IsTreeCreation && creationInputError.Length > 0) return creationInputError;
                if (IsTreeCreation && HasUnaddedDirectoryInput) return "点击“加入待创建”保存本批，再点击“创建全部”。";
                if (IsTreeCreation && PendingCreationCount == 0) return "在中央大纲输入要创建的名称，即可直接点击“创建全部”。";
                if (IsTreeCreation && DirectoryValidationMessage.Length > 0) return DirectoryValidationMessage;
                if (IsTreeCreation) return "";
                if (IsTemplateCreation && string.IsNullOrWhiteSpace(Template)) return "选择一个要复制的模板文件。";
                if (CreateNumbered && string.IsNullOrWhiteSpace(CreateBaseName)) return "填写新文件或文件夹的名称。";
                if (CreateList && string.IsNullOrWhiteSpace(Names)) return "粘贴名称清单，每行一个名称。";
            }
            return "";
        }
    }
    public string NextStep => IsExecuting ? "正在处理，可随时取消。" : IsImporting ? Input.LoadingMessage : IsScanning ? "正在检查名称和冲突…" : ShowResults && !EditingResults ? Feedback
        : Feedback.Length > 0 ? Feedback + (MissingInput.Length > 0 ? "\n" + MissingInput : "")
        : MissingInput.Length > 0 ? MissingInput : ShowResults && EditingResults ? "修改设置后，将生成新的预览。" : CanRun ? "核对新名称与完整路径后，点击下方按钮。" : "正在生成预览…";
    public string NamingExample
    {
        get
        {
            var file = Input.EffectiveEntries.FirstOrDefault();
            var name = NumberOnly ? file is null ? "文件" : file.IsFolder ? file.Name : Path.GetFileNameWithoutExtension(file.Name)
                : IsRename ? (string.IsNullOrWhiteSpace(RenameBaseName) ? "文件" : RenameBaseName) : CreateBaseName;
            var number = int.TryParse(Start, out var n) && n >= 0 ? n : 1;
            var digits = int.TryParse(Digits, out var d) && d is >= 1 and <= 12 ? d : 3;
            var serial = number.ToString("D" + digits);
            var extension = IsRename ? file is null || file.IsFolder ? "" : Path.GetExtension(file.Name)
                : IsFileCreation ? FileExtension : "";
            return "名称示例：" + (name.Length == 0 ? serial : NumberPosition == 0 ? serial + Separator + name : name + Separator + serial) + extension;
        }
    }
    private NamingRules SimpleRules()
    {
        NamingRules rules;
        if (IsRename && IsRenameAddTab && Custom) rules = new(RenamePattern, Prefix, Suffix);
        else if (Custom) rules = new(IsRename ? RenamePattern : CreatePattern, Prefix, Suffix, Find, Replacement, DeleteText,
            Number(RemoveAt, "删除位置"), Number(RemoveCount, "删除字符数"), (NameCase)CaseValue);
        else if (NumberOnly) rules = new(NumberPosition == 0 ? "{n}" + Separator + "{name}" : "{name}" + Separator + "{n}");
        else if (Uniform || CreateNumbered)
        {
            var name = IsRename ? RenameBaseName : CreateBaseName;
            rules = name.Length == 0 ? new("{n}") : NumberPosition == 0 ? new("{n}", Suffix: Separator + name) : new("{n}", Prefix: name + Separator);
        }
        else if (Replace) rules = new(Find: Find, Replacement: Replacement);
        else if (AddText) rules = new(Prefix: Prefix, Suffix: Suffix);
        else if (DeleteByText) rules = new(Delete: DeleteText);
        else if (DeleteByPosition)
        {
            var at = Number(RemoveAt, "删除位置"); var count = Number(RemoveCount, "删除字符数");
            if (at < 1 || count < 1) throw new ArgumentException("删除位置和字符数都须至少为 1。第一个字符的位置为 1。");
            rules = new(RemoveAt: at, RemoveCount: count);
        }
        else if (ChangeCase) rules = new(Case: (NameCase)CaseValue);
        else rules = new(); // A pasted list is used literally; no hidden suffix or numbering.
        return ShowNumbering ? rules with { Start = Number(Start, "起始数字"), Step = Number(Step, "每次递增"), Digits = Number(Digits, "数字位数"), Order = (NamingOrder)OrderValue, NumberPerDirectory = IsRename && PerDirectory } : rules;
    }
    private void RefreshRows()
    {
        Rows = Plan is { } plan ? plan.Items.Select(x => new NamingPreviewRow(IsRename ? x.OriginalName : CreationLabel(x), x.NewName, (x.Action == NamingAction.Invalid && !string.IsNullOrWhiteSpace(x.Message) ? x.Message : x.Action == NamingAction.Skipped && x.Message == "名称未变化。" ? Replace ? "未匹配 / 名称未变化" : "名称未变化" : x.Status), x.Details, x.Action == NamingAction.Invalid, x.Source?.Path)).ToArray()
            : Report is { } report ? report.Items.Select(x => ResultRow(x, report.Plan.Request)).ToArray()
            : IsRename ? Input.EffectiveEntries.Select(x => new NamingPreviewRow(Path.GetFileName(x.FullPath), Manual ? customNames.TryGetValue(x.FullPath, out var edited) ? edited + (x.IsFolder ? "" : Path.GetExtension(x.Name)) : x.Name : "等待填写规则", Manual ? "填写后自动检查" : "待设置", x.FullPath, SourcePath: x.FullPath)).ToArray() : [];
        SelectedRow = Rows.FirstOrDefault(x => x.NeedsAttention) ?? Rows.FirstOrDefault();
        UpdateNamePreviews(); SelectDirectoryPreview();
        RefreshCreationContents();
        OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(Empty));
    }
    private static string CreationLabel(NamingPlanItem item) => item.IsDirectory ? "文件夹" : Path.GetExtension(item.Target).TrimStart('.').ToUpperInvariant() + " 文件";
    private static NamingPreviewRow ResultRow(NamingItemResult result, NamingRequest request)
    {
        var rename = request.Mode == NamingMode.Rename;
        var completed = result.Outcome == NamingOutcome.Success;
        var actual = result.ActualPath ?? (completed ? result.Item.DisplayPath : result.Item.Source?.Path);
        var name = rename ? actual is null ? "未确认" : Path.GetFileName(actual)
            : completed && actual is not null ? Path.GetRelativePath(request.Destination, actual) : "未创建";
        var details = result.Message + "\n计划：" + result.Item.DisplayPath
            + (rename && actual is not null || completed ? "\n实际：" + actual : "\n未创建该项目。");
        return new(rename ? result.Item.OriginalName : CreationLabel(result.Item), name,
            result.Outcome switch { NamingOutcome.Success => "已完成", NamingOutcome.Skipped => "已跳过", NamingOutcome.Cancelled => "已取消", _ => "未完成" }, details, result.Retryable, result.Item.Source?.Path);
    }
    partial void OnSelectedRowChanged(NamingPreviewRow? value) => OnPropertyChanged(nameof(RowDetails));
    partial void OnFeedbackChanged(string value) { OnPropertyChanged(nameof(NextStep)); OnPropertyChanged(nameof(HasActionMessage)); }
    private void NotifySimple()
    {
        foreach (var name in new[] { nameof(Manual), nameof(CustomNameSummary), nameof(CustomNameCount), nameof(Uniform), nameof(Replace), nameof(AddText), nameof(Delete), nameof(ChangeCase), nameof(Custom), nameof(DeleteByText), nameof(DeleteByPosition),
            nameof(CreationByName), nameof(CreateNumbered), nameof(CreateList), nameof(ShowNames), nameof(ShowCount), nameof(ShowNumbering), nameof(ShowSeparator), nameof(ShowResults), nameof(IsImporting), nameof(CanCancelImport), nameof(CanUndoLast),
            nameof(PageHint), nameof(PreviewTitle), nameof(OptionsTitle), nameof(HasNamingPreview), nameof(OriginalHeader), nameof(NewHeader), nameof(EmptyTitle), nameof(EmptyHint), nameof(NextStep), nameof(NamingExample) }) OnPropertyChanged(name);
        RefreshRuleSample();
        OnPropertyChanged(nameof(PrimaryActionLabel));
        NotifyInteraction();
        UndoLastCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanUndoLast))]
    private async Task UndoLastAsync()
    {
        SelectedRecovery = Recoveries.FirstOrDefault(x => x.Id == Report?.RecoveryId && x.Remaining > 0);
        if (SelectedRecovery is not null) await RestoreCommand.ExecuteAsync(null);
    }
}
