using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    private bool syncingRenameTab;

    [ObservableProperty] public partial int RenameTab { get; set; }
    [ObservableProperty] public partial bool ChipAddParent { get; set; }
    [ObservableProperty] public partial bool ChipAddDate { get; set; }
    [ObservableProperty] public partial bool ChipAddSerial { get; set; } = true;
    [ObservableProperty] public partial int SerialStyleValue { get; set; }

    public IReadOnlyList<SelectionOption> SerialStyleOptions { get; } = [
        new(0, "末尾 _001"),
        new(1, "末尾 _01"),
        new(2, "末尾 -001"),
        new(3, "开头 001_"),
        new(4, "自定义…")
    ];

    public bool IsRenameAddTab => IsRename && RenameTab == 0;
    public bool IsRenameCleanTab => IsRename && RenameTab == 1;
    public bool IsRenameUniformTab => IsRename && RenameTab == 2;

    public bool IsReplaceSubMode => IsRenameCleanTab && RenameAction == 1;
    public bool IsDeletePosSubMode => IsRenameCleanTab && RenameAction == 3;
    public bool IsCaseSubMode => IsRenameCleanTab && RenameAction == 4;

    public bool IsUniformSubMode => IsRenameUniformTab && RenameAction == 0;
    public bool IsManualSubMode => IsRenameUniformTab && RenameAction == 7;
    public bool IsTemplateSubMode => IsRenameUniformTab && RenameAction == 5;

    public bool ShowSerialStylePicker => IsRename && ((IsRenameAddTab && ChipAddSerial) || IsUniformSubMode);
    public bool ShowCustomSerialControls => ShowSerialStylePicker && SerialStyleValue == 4;
    public bool ShowRenameTemplateNumbering => IsTemplateSubMode;

    public int UnavailableFileCount => IsRename ? Input.VisibleFiles.Count(x => x.ReferenceError is not null) : 0;
    public bool HasUnavailableFiles => UnavailableFileCount > 0;
    public string UnavailableFilesMessage => $"当前列表有 {UnavailableFileCount:N0} 个失效文件（原路径已不存在），会阻止批量改名。";

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveUnavailableFiles()
    {
        var invalid = Input.VisibleFiles.Where(x => x.ReferenceError is not null).ToArray();
        if (invalid.Length == 0) return;
        Input.RemoveFromList(invalid);
        NotifyRenameTabs();
    }

    public void SelectRenameTab(int tab)
    {
        if (!CanEdit) return;
        RenameTab = Math.Clamp(tab, 0, 2);
        switch (RenameTab)
        {
            case 0:
                ApplyAddTabComposition();
                break;
            case 1:
                if (RenameAction is not (1 or 3 or 4)) RenameAction = 1;
                break;
            case 2:
                if (RenameAction is not (0 or 7 or 5) || IsAddTabComposedPattern()) RenameAction = 0;
                break;
        }
        NotifyRenameTabs();
    }

    public void SelectCleanSubMode(int action)
    {
        if (!CanEdit) return;
        RenameTab = 1;
        if (action == 3) DeleteMethod = 1;
        RenameAction = action;
        NotifyRenameTabs();
    }

    public void SelectUniformSubMode(int action)
    {
        if (!CanEdit) return;
        RenameTab = 2;
        if (action == 5 && string.IsNullOrWhiteSpace(RenamePattern)) RenamePattern = "{name}_{n}";
        RenameAction = action;
        NotifyRenameTabs();
    }

    partial void OnChipAddParentChanged(bool value) { if (!syncingRenameTab && IsRenameAddTab) ApplyAddTabComposition(); NotifyRenameTabs(); }
    partial void OnChipAddDateChanged(bool value) { if (!syncingRenameTab && IsRenameAddTab) ApplyAddTabComposition(); NotifyRenameTabs(); }
    partial void OnChipAddSerialChanged(bool value) { if (!syncingRenameTab && IsRenameAddTab) ApplyAddTabComposition(); NotifyRenameTabs(); }

    partial void OnSerialStyleValueChanged(int value)
    {
        if (syncingRenameTab) return;
        syncingRenameTab = true;
        try
        {
            switch (value)
            {
                case 0: NumberPosition = 1; SeparatorValue = 0; Digits = "3"; break;
                case 1: NumberPosition = 1; SeparatorValue = 0; Digits = "2"; break;
                case 2: NumberPosition = 1; SeparatorValue = 1; Digits = "3"; break;
                case 3: NumberPosition = 0; SeparatorValue = 0; Digits = "3"; break;
            }
        }
        finally { syncingRenameTab = false; }
        if (IsRenameAddTab) ApplyAddTabComposition();
        NotifyRenameTabs();
    }

    private bool IsAddTabComposedPattern() =>
        RenameAction == 5 && RenameTab == 0;

    internal void ApplyAddTabComposition()
    {
        if (syncingRenameTab || !IsRename) return;
        syncingRenameTab = true;
        try
        {
            if (!ChipAddParent && !ChipAddDate && ChipAddSerial && Prefix.Length == 0 && Suffix.Length == 0)
            {
                RenameAction = 6;
            }
            else if (!ChipAddParent && !ChipAddDate && !ChipAddSerial)
            {
                RenameAction = 2;
            }
            else
            {
                var join = Separator.Length > 0 ? Separator : "_";
                var pattern = "{name}";
                if (ChipAddParent) pattern = "{parent}" + join + pattern;
                if (ChipAddDate) pattern = pattern + join + "{date}";
                if (ChipAddSerial)
                    pattern = NumberPosition == 0 ? "{n}" + Separator + pattern : pattern + Separator + "{n}";
                RenamePattern = pattern;
                RenameAction = 5;
            }
        }
        finally { syncingRenameTab = false; }
        NotifyRenameTabs();
    }

    private void SyncRenameTabFromModelChange(string? propertyName)
    {
        if (syncingRenameTab || !IsRename) return;
        if (propertyName is nameof(Prefix) or nameof(Suffix) && IsRenameAddTab)
        {
            ApplyAddTabComposition();
            return;
        }
        if (propertyName is nameof(NumberPosition) or nameof(SeparatorValue) && IsRenameAddTab && ChipAddSerial)
        {
            ApplyAddTabComposition();
            return;
        }
        if (propertyName == nameof(RenameAction))
        {
            var nextTab = RenameAction switch
            {
                6 or 2 => 0,
                1 or 3 or 4 => 1,
                0 or 7 => 2,
                5 => RenameTab == 0 ? 0 : 2,
                _ => RenameTab
            };
            if (RenameTab != nextTab) RenameTab = nextTab;
            NotifyRenameTabs();
        }
    }

    private void NotifyRenameTabs()
    {
        foreach (var name in new[]
        {
            nameof(RenameTab), nameof(IsRenameAddTab), nameof(IsRenameCleanTab), nameof(IsRenameUniformTab),
            nameof(IsReplaceSubMode), nameof(IsDeletePosSubMode), nameof(IsCaseSubMode),
            nameof(IsUniformSubMode), nameof(IsManualSubMode), nameof(IsTemplateSubMode),
            nameof(ShowSerialStylePicker), nameof(ShowCustomSerialControls), nameof(ShowRenameTemplateNumbering),
            nameof(UnavailableFileCount), nameof(HasUnavailableFiles), nameof(UnavailableFilesMessage)
        }) OnPropertyChanged(name);
        RemoveUnavailableFilesCommand.NotifyCanExecuteChanged();
    }
}
