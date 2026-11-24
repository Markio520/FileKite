using System.Xml.Linq;

namespace FileAssistant.App.Tests;

public sealed class WorkspacePageResourceTests
{
    [Fact]
    public void Sidebar_collapse_button_can_resolve_its_style_on_startup()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "StartupXaml");
        var page = XElement.Load(Path.Combine(directory, "WorkspacePage.xaml"));
        var app = XElement.Load(Path.Combine(directory, "App.xaml"));
        XNamespace ui = page.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = page.Descendants(ui + "Button").Single(b => (string?)b.Attribute(x + "Name") == "RailCollapseButton");
        var reference = (string?)button.Attribute("Style");
        const string prefix = "{StaticResource ";
        Assert.NotNull(reference);
        Assert.StartsWith(prefix, reference);
        Assert.EndsWith("}", reference);
        var key = reference[prefix.Length..^1];

        var localResources = button.AncestorsAndSelf()
            .SelectMany(owner => owner.Elements(ui + (owner.Name.LocalName + ".Resources")));
        var sharedResources = app.Descendants(ui + "ResourceDictionary").Attributes("Source")
            .Select(source => XElement.Load(Path.Combine(directory, source.Value)));
        var visibleKeys = localResources.Concat(sharedResources).Elements(ui + "Style")
            .Select(style => (string?)style.Attribute(x + "Key"));
        Assert.Contains(key, visibleKeys);
    }

    [Fact]
    public void Left_navigation_rail_has_draggable_resize_handle_and_pattern_04_invisible_rest_splitter()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "StartupXaml");
        var page = XElement.Load(Path.Combine(directory, "WorkspacePage.xaml"));
        var palette = XElement.Load(Path.Combine(directory, "Resources", "ThemePalette.xaml"));
        var theme = XElement.Load(Path.Combine(directory, "Resources", "WorkspaceTheme.xaml"));
        var resize = XElement.Load(Path.Combine(directory, "Resources", "ResizeControls.xaml"));

        XNamespace ui = page.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "using:FileAssistant.App.Controls";

        var titleBar = page.Descendants(ui + "TitleBar").Single(t => (string?)t.Attribute(x + "Name") == "WorkspaceTitleBar");
        Assert.Equal("3", (string?)titleBar.Attribute("Grid.ColumnSpan"));

        var railHandle = page.Descendants(controls + "ResizeHandle")
            .Single(h => (string?)h.Attribute(x + "Name") == "LeftRailResizeHandle");
        Assert.Equal("LeftRail_ResizeRequested", (string?)railHandle.Attribute("ResizeRequested"));
        Assert.Equal("LeftRail_ResetRequested", (string?)railHandle.Attribute("ResetRequested"));

        var themeDicts = theme.Element(ui + "ResourceDictionary.ThemeDictionaries")!.Elements(ui + "ResourceDictionary").ToArray();
        Assert.Equal(3, themeDicts.Length);
        foreach (var dict in themeDicts)
            Assert.Contains(dict.Elements(), el => (string?)el.Attribute(x + "Key") == "WorkspaceSplitterRestBrush");

        var paletteDicts = palette.Element(ui + "ResourceDictionary.ThemeDictionaries")!.Elements(ui + "ResourceDictionary")
            .ToDictionary(d => (string)d.Attribute(x + "Key")!);
        Assert.Equal("#00000000", paletteDicts["Light"].Elements(ui + "Color").Single(c => (string?)c.Attribute(x + "Key") == "AppSplitterRestColor").Value);
        Assert.Equal("#00000000", paletteDicts["Dark"].Elements(ui + "Color").Single(c => (string?)c.Attribute(x + "Key") == "AppSplitterRestColor").Value);

        var dividers = resize.Descendants(ui + "Border").Where(b => (string?)b.Attribute(x + "Name") == "Divider").ToArray();
        Assert.Equal(2, dividers.Length);
        Assert.All(dividers, d => Assert.Equal("{ThemeResource WorkspaceSplitterRestBrush}", (string?)d.Attribute("Background")));
    }

    [Fact]
    public void Operation_panels_expose_inline_settings_without_subpage_jump_and_center_single_line_inputs()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "StartupXaml");
        var naming = XElement.Load(Path.Combine(directory, "NamingOptionsView.xaml"));
        var convert = XElement.Load(Path.Combine(directory, "ConversionOptionsView.xaml"));
        var controls = XElement.Load(Path.Combine(directory, "Resources", "WorkspaceControls.xaml"));

        XNamespace ui = naming.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // No subpage jump buttons for MoreSettings
        Assert.DoesNotContain(naming.Descendants(ui + "Button"), b => ((string?)b.Attribute("Command"))?.Contains("OpenMoreCommand") == true);
        Assert.DoesNotContain(convert.Descendants(ui + "Button"), b => ((string?)b.Attribute("Command"))?.Contains("OpenMoreSettingsCommand") == true);

        // In-place collapsible drawers exist (including inline history in NamingOptionsView)
        Assert.Contains(naming.Descendants(ui + "Button"), b => (string?)b.Attribute(x + "Name") == "NumberingTuningButton");
        Assert.Contains(naming.Descendants(ui + "Button"), b => (string?)b.Attribute(x + "Name") == "ComboCleaningButton");
        Assert.Contains(naming.Descendants(ui + "Button"), b => (string?)b.Attribute(x + "Name") == "InlineHistoryToggle");
        Assert.Contains(convert.Descendants(ui + "Button"), b => (string?)b.Attribute(x + "Name") == "AdvancedToggle");

        // Toolbar buttons and dropdown menus match WorkspaceToolbarSelectorStyle (WorkspaceSubtleBrush + WorkspaceBorderBrush)
        var selectionControls = XElement.Load(Path.Combine(directory, "Resources", "SelectionControls.xaml"));
        var toolbarBtnStyle = controls.Elements(ui + "Style").Single(s => (string?)s.Attribute(x + "Key") == "WorkspaceToolbarButtonStyle");
        Assert.Contains(toolbarBtnStyle.Elements(ui + "Setter"), s => (string?)s.Attribute("Property") == "Background" && (string?)s.Attribute("Value") == "{ThemeResource WorkspaceSubtleBrush}");
        Assert.Contains(toolbarBtnStyle.Elements(ui + "Setter"), s => (string?)s.Attribute("Property") == "BorderBrush" && (string?)s.Attribute("Value") == "{ThemeResource WorkspaceBorderBrush}");
        var toolbarMenuStyle = selectionControls.Elements(ui + "Style").Single(s => (string?)s.Attribute(x + "Key") == "WorkspaceToolbarMenuStyle");
        Assert.Contains(toolbarMenuStyle.Elements(ui + "Setter"), s => (string?)s.Attribute("Property") == "Background" && (string?)s.Attribute("Value") == "{ThemeResource WorkspaceSubtleBrush}");
        Assert.Contains(toolbarMenuStyle.Elements(ui + "Setter"), s => (string?)s.Attribute("Property") == "BorderBrush" && (string?)s.Attribute("Value") == "{ThemeResource WorkspaceBorderBrush}");

        // Create save destination & conflict dropdown sit at top of Create settings, with no two-step "加入待创建" button
        var allElements = naming.Descendants().ToList();
        var createDestIdx = allElements.FindIndex(el => (string?)el.Attribute(x + "Name") == "CreateDestinationBox");
        var createConflictIdx = allElements.FindIndex(el => (string?)el.Attribute("AutomationProperties.Name") == "创建同名冲突处理");
        Assert.True(createDestIdx > 0 && createConflictIdx > createDestIdx);
        Assert.DoesNotContain(naming.Descendants(ui + "Button"), b => (string?)b.Attribute("Content") == "加入待创建");

        // All NumberBoxes in ConversionOptionsView hide the floating compact spin popup
        var numberBoxes = convert.Descendants(ui + "NumberBox").ToArray();
        Assert.NotEmpty(numberBoxes);
        Assert.All(numberBoxes, nb => Assert.Equal("Hidden", (string?)nb.Attribute("SpinButtonPlacementMode")));

        // Single-line OpSoftTextBoxStyle centers vertically while OpMultilineSoftTextBoxStyle top-aligns
        var softBox = controls.Elements(ui + "Style").Single(s => (string?)s.Attribute(x + "Key") == "OpSoftTextBoxStyle");
        Assert.Contains(softBox.Elements(ui + "Setter"), s => (string?)s.Attribute("Property") == "VerticalAlignment" && (string?)s.Attribute("Value") == "Center");
        Assert.Contains(softBox.Elements(ui + "Setter"), s => (string?)s.Attribute("Property") == "VerticalContentAlignment" && (string?)s.Attribute("Value") == "Center");
        Assert.Contains(controls.Elements(ui + "Style"), s => (string?)s.Attribute(x + "Key") == "OpMultilineSoftTextBoxStyle");

        // FileInputView renders green inline diff pill using WorkspaceDiffAddedBackgroundBrush & WorkspaceDiffAddedTextBrush
        var fileInput = XElement.Load(Path.Combine(directory, "FileInputView.xaml"));
        Assert.Contains(fileInput.Descendants(ui + "Border"), b => (string?)b.Attribute("Background") == "{ThemeResource WorkspaceDiffAddedBackgroundBrush}");
        Assert.Contains(fileInput.Descendants(ui + "TextBlock"), t => (string?)t.Attribute("Foreground") == "{ThemeResource WorkspaceDiffAddedTextBrush}");

        // Direct WYSIWYG Outline Editor canvas exists, noisy headers/selection banner removed (图四), bottom status bar aligned with FileInputView (图六)
        var outlineView = XElement.Load(Path.Combine(directory, "NamingCreationPreviewView.xaml"));
        Assert.Contains(outlineView.Descendants(ui + "StackPanel"), s => (string?)s.Attribute(x + "Name") == "OutlineRowsHost");
        Assert.DoesNotContain(outlineView.Descendants(ui + "Border"), b => (string?)b.Attribute(x + "Name") == "MissingDestinationBanner");
        Assert.DoesNotContain(outlineView.Descendants(ui + "Border"), b => (string?)b.Attribute(x + "Name") == "OutlineRootDropHeader");
        Assert.DoesNotContain(outlineView.Descendants(ui + "Border"), b => (string?)b.Attribute(x + "Name") == "SelectionActionBar");
        Assert.Contains(outlineView.Descendants(ui + "Border"), b => (string?)b.Attribute(x + "Name") == "MarqueeRect");
        Assert.Contains(outlineView.Descendants(ui + "TextBlock"), t => (string?)t.Attribute(x + "Name") == "OutlineListSummary");
        Assert.Contains(outlineView.Descendants(ui + "TextBlock"), t => (string?)t.Attribute(x + "Name") == "OutlineSelectionSummary");
        Assert.Contains(outlineView.Descendants(ui + "Button"), b => (string?)b.Attribute(x + "Name") == "RemoveSelectedOutlineButton" && (string?)b.Attribute("Content") == "移出列表");
        Assert.DoesNotContain(outlineView.Descendants(ui + "TextBlock"), t => (string?)t.Attribute("Text") is "⇥ 缩进子目录" or "⇤ 升一级");
        var pathBar = outlineView.Descendants(ui + "Grid").Single(g => (string?)g.Attribute("Style") == "{StaticResource WorkspacePathBarStyle}");
        Assert.Contains(pathBar.Descendants(ui + "DropDownButton"), d => (string?)d.Attribute(x + "Name") == "TemplateDropDownButton");
        var templateFlyout = outlineView.Descendants(ui + "Flyout").Single(f => (string?)f.Attribute(x + "Name") == "TemplateFlyout");
        Assert.Equal("BottomEdgeAlignedLeft", (string?)templateFlyout.Attribute("Placement"));

        // File extension selector uses SelectionDropDown (not raw ComboBox)
        Assert.DoesNotContain(naming.Descendants(ui + "ComboBox"), c => (string?)c.Attribute("AutomationProperties.Name") == "文件扩展名");
        Assert.Contains(naming.Descendants(), el => (string?)el.Attribute(x + "Name") == "FileExtensionDropDown");

        // Left creation directory tree supports native TreeView drag-and-drop without pointer-swallowing Button or bottom hint text
        var treeView = XElement.Load(Path.Combine(directory, "NamingDirectoryTreeView.xaml"));
        var creationTree = treeView.Descendants(ui + "TreeView").Single(t => (string?)t.Attribute(x + "Name") == "CreationTree");
        Assert.Equal("True", (string?)creationTree.Attribute("CanDragItems"));
        Assert.Equal("CreationTree_DragItemsStarting", (string?)creationTree.Attribute("DragItemsStarting"));
        Assert.Empty(creationTree.Descendants(ui + "Button"));
        Assert.DoesNotContain(treeView.Descendants(ui + "TextBlock"), t => ((string?)t.Attribute("Text"))?.Contains("可拖拽待创建文件夹") == true);
    }

    [Fact]
    public void File_name_preview_renders_full_green_target_name_without_partial_diff_chopping()
    {
        var preview = new FileAssistant.App.Features.Workspace.FileNamePreview();
        var raised = new HashSet<string>();
        preview.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        preview.ApplyDiffPreview("01.jpg", "→ 01 (14).png");
        Assert.Contains(nameof(FileAssistant.App.Features.Workspace.FileNamePreview.ShowPlainSuccess), raised);
        Assert.Contains(nameof(FileAssistant.App.Features.Workspace.FileNamePreview.ShowPlainError), raised);
        Assert.False(preview.HasStructuredDiff);
        Assert.True(preview.ShowPlainPreview);
        Assert.True(preview.ShowPlainSuccess);
        Assert.False(preview.ShowPlainError);
        Assert.Equal("→ 01 (14).png", preview.Text);

        preview.ApplyDiffPreview("花絮合集.mp4", "→ 花絮合集_003.mp4");
        Assert.False(preview.HasStructuredDiff);
        Assert.True(preview.ShowPlainSuccess);
        Assert.Equal("→ 花絮合集_003.mp4", preview.Text);
    }

    [Fact]
    public void Naming_additive_chips_and_direct_outline_sync_work_without_staging_step()
    {
        var vm = new FileAssistant.App.Features.CreateOrRename.NamingViewModel(
            new NoopNamingService(),
            new FileAssistant.App.Features.Workspace.WorkspaceViewModel(null, FileAssistant.App.Features.Workspace.WorkspaceInputPurpose.Rename),
            new FileAssistant.App.Features.Outputs.OutputsViewModel(new NoopInteraction()),
            new FileAssistant.App.Features.Tasks.ProcessingJobsViewModel());

        // Additive chips combine without overwriting each other
        vm.ChipAddParent = true;
        vm.ChipAddDate = true;
        vm.ChipAddSerial = true;
        Assert.Equal(5, vm.RenameAction);
        Assert.Equal("{parent}_{name}_{date}_{n}", vm.RenamePattern);

        // 4-in-1 Serial style preset updates position, separator, digits
        vm.SerialStyleValue = 3; // 开头 001_
        Assert.Equal(0, vm.NumberPosition);
        Assert.Equal(0, vm.SeparatorValue);
        Assert.Equal("3", vm.Digits);
        Assert.Equal("{n}_{parent}_{name}_{date}", vm.RenamePattern);

        // Direct outline parsing supports indentation, slash paths, and auto file detection
        var parsed = FileAssistant.App.Features.CreateOrRename.NamingViewModel.ParseOutlineText("客户甲\n  素材/原片\n  说明.txt\n客户乙");
        Assert.Equal(5, parsed.Count);
        Assert.Equal((0, "客户甲", (bool?)false), (parsed[0].Indent, parsed[0].Name, parsed[0].ForceIsFile));
        Assert.Equal((1, "素材", (bool?)false), (parsed[1].Indent, parsed[1].Name, parsed[1].ForceIsFile));
        Assert.Equal((2, "原片", (bool?)false), (parsed[2].Indent, parsed[2].Name, parsed[2].ForceIsFile));
        Assert.Equal((1, "说明.txt", (bool?)true), (parsed[3].Indent, parsed[3].Name, parsed[3].ForceIsFile));
        Assert.Equal((0, "客户乙", (bool?)false), (parsed[4].Indent, parsed[4].Name, parsed[4].ForceIsFile));

        // Direct outline sync creates nested draft hierarchy with zero staging friction
        vm.ModeValue = 1;
        vm.Destination = @"C:\out";
        var error = vm.SyncOutlineDrafts(parsed);
        Assert.Equal("", error);
        Assert.False(vm.HasUnaddedDirectoryInput);
        Assert.Equal(5, vm.PendingCreationCount);
        Assert.Equal(4, vm.PendingDirectoryCount);
        Assert.True(vm.CanBuild);

        // In-place outline sync preserves DirectoryDraft instances (no collapse/recreate in left tree)
        var folderA = vm.Directories.Single(d => d.Name == "客户甲");
        var folderAId = folderA.Id;
        var renamedLines = FileAssistant.App.Features.CreateOrRename.NamingViewModel.ParseOutlineText("客户甲_已改名\n  素材/原片\n  说明.txt\n客户乙");
        Assert.Equal("", vm.SyncOutlineDrafts(renamedLines));
        Assert.Same(folderA, vm.Directories.Single(d => d.Name == "客户甲_已改名"));
        Assert.Equal(folderAId, folderA.Id);
        // Restore name for subsequent assertions
        Assert.Equal("", vm.SyncOutlineDrafts(parsed));

        // Left tree drag-and-drop safely moves pending drafts and blocks invalid cycles
        var folderB = vm.Directories.Single(d => d.Name == "客户乙");
        var subAssets = folderA.Children.Single(d => d.Name == "素材");
        Assert.False(vm.CanMoveCreationDraft(folderA, subAssets)); // cannot move parent into its own child
        Assert.True(vm.CanMoveCreationDraft(folderB, folderA));
        Assert.True(vm.MoveCreationDraft(folderB, folderA));
        Assert.Contains(folderB, folderA.Children);
        Assert.DoesNotContain(folderB, vm.Directories);
        // Move nested folder back to root ("保存位置")
        Assert.True(vm.CanMoveCreationDraft(folderB, vm.CreationTreeRoots[0]));
        Assert.True(vm.MoveCreationDraft(folderB, vm.CreationTreeRoots[0]));
        Assert.Contains(folderB, vm.Directories);

        // Dragging outline items onto a sibling folder in the left tree preserves target folder and attaches items
        Assert.False(vm.CanDropOutlineLinesIntoDirectory([new(0, "客户甲", false)], [new(0, "客户乙", false)], folderA));
        Assert.True(vm.CanDropOutlineLinesIntoDirectory([new(0, "新增说明.txt", true)], [new(0, "客户甲", false), new(0, "客户乙", false)], folderB));
        Assert.True(vm.MoveOutlineLinesToDirectory([new(0, "新增说明.txt", true)], [new(0, "客户甲", false), new(0, "客户乙", false)], folderB));
        Assert.Contains(folderB.Children, c => c.Name == "新增说明.txt" && !c.IsDirectory);

        // Center outline multi-select drag-into-folder and promote-to-root
        var sampleLines = new FileAssistant.App.Features.CreateOrRename.OutlineDraftLine[]
        {
            new(0, "01_素材", false),
            new(0, "02_工程文件", false),
            new(0, "新建_001.txt", true),
            new(0, "新建_002.txt", true),
            new(0, "新建_003.txt", true)
        };
        var movedIntoFolder = FileAssistant.App.Features.CreateOrRename.NamingViewModel.MoveOutlineSelection(
            sampleLines, [2, 3], targetIndex: 0, dropIntoFolder: true);
        Assert.NotNull(movedIntoFolder);
        Assert.Equal([(0, "01_素材"), (1, "新建_001.txt"), (1, "新建_002.txt"), (0, "02_工程文件"), (0, "新建_003.txt")],
            movedIntoFolder.Select(l => (l.Indent, l.Name)).ToArray());

        // Cannot drop a folder into its own descendant
        Assert.Null(FileAssistant.App.Features.CreateOrRename.NamingViewModel.MoveOutlineSelection(
            movedIntoFolder, [0], targetIndex: 1, dropIntoFolder: true));

        // Promote nested files back to top level
        var promoted = FileAssistant.App.Features.CreateOrRename.NamingViewModel.MoveOutlineSelectionToRoot(
            movedIntoFolder, [1, 2], appendToEnd: true);
        Assert.NotNull(promoted);
        Assert.Equal([(0, "01_素材"), (0, "02_工程文件"), (0, "新建_003.txt"), (0, "新建_001.txt"), (0, "新建_002.txt")],
            promoted.Select(l => (l.Indent, l.Name)).ToArray());

        // Template add / delete / persist workflow
        var tempStore = Path.Combine(Path.GetTempPath(), $"fa-tpl-{Guid.NewGuid():N}.json");
        try
        {
            vm.ResetDefaultOutlineTemplates(tempStore);
            var initialCount = vm.OutlineTemplates.Count;
            Assert.Equal("", vm.SaveCurrentOutlineAsTemplate("我的测试模板", tempStore));
            Assert.Equal(initialCount + 1, vm.OutlineTemplates.Count);
            var saved = vm.OutlineTemplates.Single(t => t.Name == "我的测试模板");
            Assert.True(vm.DeleteOutlineTemplate(saved.Id, tempStore));
            Assert.Equal(initialCount, vm.OutlineTemplates.Count);
        }
        finally { if (File.Exists(tempStore)) File.Delete(tempStore); }
    }

    [Fact]
    public async Task Rename_tab_additive_rules_do_not_leak_stale_replace_text_and_mark_errors_clearly()
    {
        var plannerService = new PlannerNamingService();
        var input = new FileAssistant.App.Features.Workspace.WorkspaceViewModel(null, FileAssistant.App.Features.Workspace.WorkspaceInputPurpose.Rename);
        input.AddSearchFiles([new FileAssistant.Core.Features.Search.SearchFile(@"C:\videos\花絮.mp4", false, 1024, null)]);
        var vm = new FileAssistant.App.Features.CreateOrRename.NamingViewModel(
            plannerService,
            input,
            new FileAssistant.App.Features.Outputs.OutputsViewModel(new NoopInteraction()),
            new FileAssistant.App.Features.Tasks.ProcessingJobsViewModel());

        // Set Find/Replacement in Tab 1, then switch back to Tab 0 with +父目录 + +序号
        vm.SelectRenameTab(1);
        vm.Find = "花絮";
        vm.Replacement = "错换";
        vm.SelectRenameTab(0);
        vm.ChipAddParent = true;
        vm.ChipAddSerial = true;

        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Plan);
        Assert.Single(vm.Plan.Items);
        Assert.Equal("videos_花絮_001.mp4", vm.Plan.Items[0].NewName);
        Assert.True(vm.CanRun);

        // Error state sets IsError rather than green success preview
        var preview = new FileAssistant.App.Features.Workspace.FileNamePreview();
        preview.ApplyDiffPreview("4.mp4", "无法改名（原文件已失效）", "找不到文件", "", isError: true);
        Assert.True(preview.IsError);
        Assert.True(preview.ShowPlainError);
        Assert.False(preview.ShowPlainSuccess);
        Assert.False(preview.HasStructuredDiff);
    }

    [Fact]
    public void Pane_toggle_icons_and_staging_import_and_result_handoff_resources_are_wired()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FileAssistant.App")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var appRoot = Path.Combine(dir!.FullName, "src", "FileAssistant.App");
        var controlsXaml = File.ReadAllText(Path.Combine(appRoot, "Resources/WorkspaceControls.xaml"));
        var pickerXaml = File.ReadAllText(Path.Combine(appRoot, "Features/FileInput/CollectionSourcePicker.xaml"));
        var fileInputXaml = File.ReadAllText(Path.Combine(appRoot, "Features/FileInput/FileInputView.xaml"));
        var cleanupXaml = File.ReadAllText(Path.Combine(appRoot, "Features/Cleanup/CleanupView.xaml"));
        var convertXaml = File.ReadAllText(Path.Combine(appRoot, "Features/Convert/ConversionOptionsView.xaml"));
        var copyXaml = File.ReadAllText(Path.Combine(appRoot, "Features/Organize/CopyOptionsView.xaml"));

        // Crisp icon button template & layout-rounded pane icons
        var iconStyleIdx = controlsXaml.IndexOf("x:Key=\"WorkspaceIconButtonStyle\"", StringComparison.Ordinal);
        Assert.True(iconStyleIdx >= 0);
        var iconStyleBlock = controlsXaml.Substring(iconStyleIdx, Math.Min(2200, controlsXaml.Length - iconStyleIdx));
        Assert.Contains("ControlTemplate TargetType=\"Button\"", iconStyleBlock);
        Assert.Contains("UseLayoutRounding=\"True\"", iconStyleBlock);
        Assert.Contains("x:Key=\"WorkspaceLeftPaneIconTemplate\"", controlsXaml);
        Assert.Contains("x:Key=\"WorkspaceRightPaneIconTemplate\"", controlsXaml);
        Assert.Contains("x:Key=\"WorkspaceSplitButtonShellStyle\"", controlsXaml);
        Assert.Contains("x:Key=\"WorkspaceSubtleCardStyle\"", controlsXaml);
        Assert.DoesNotContain("DefaultContentDialogStyle", controlsXaml);

        var stagingButtonCs = File.ReadAllText(Path.Combine(appRoot, "Controls/StagingImportButton.cs"));
        Assert.DoesNotContain("一键导入至", stagingButtonCs);
        Assert.DoesNotContain("零弹窗", stagingButtonCs);

        // Two-column Scheme B frameless staging picker & split toolbar buttons
        Assert.Contains("x:Name=\"TargetBadgePill\"", pickerXaml);
        Assert.Contains("暂存区分组", pickerXaml);
        Assert.Contains("大小 / 项数", pickerXaml);
        Assert.Contains("x:Name=\"FolderTree\"", pickerXaml);
        Assert.Contains("x:Name=\"SelectAllCheck\"", pickerXaml);
        Assert.Contains("x:Name=\"PathBreadcrumbs\"", pickerXaml);
        Assert.Contains("x:Name=\"DestinationButton\"", pickerXaml);
        Assert.Contains("x:Name=\"PreserveCheck\"", pickerXaml);
        Assert.Contains("x:Name=\"ConfirmButton\"", pickerXaml);
        Assert.Contains("controls:StagingImportButton", fileInputXaml);
        Assert.Contains("controls:StagingImportButton", cleanupXaml);

        // SpaceAnalysis folder drawer transition & global dialog backdrop-dismiss rule
        var spaceTreeCs = File.ReadAllText(Path.Combine(appRoot, "Features/SpaceAnalysis/SpaceAnalysisView.Tree.cs"));
        var workspacePageCs = File.ReadAllText(Path.Combine(appRoot, "Features/Workspace/WorkspacePage.xaml.cs"));
        var dialogsCs = File.ReadAllText(Path.Combine(appRoot, "Features/FileInput/FileCollectionDialogs.cs"));
        Assert.Contains("AnimateSidePaneToggle", spaceTreeCs);
        Assert.Contains("DialogDismiss.RegisterWindowRoot", workspacePageCs);
        Assert.Contains("DialogDismiss.Attach", dialogsCs);

        // Picker folder row splits type & item count cleanly
        var folderRow = new FileAssistant.App.Features.Workspace.WorkspaceFile(
            "格式图片", "软件文件夹 · 76 项", 0, "文件暂存区", "—", "\uE8B7", 8, FolderId: Guid.NewGuid());
        Assert.Equal("软件文件夹", folderRow.PickerTypeLabel);
        Assert.Equal("76 项", folderRow.PickerSizeOrCountLabel);

        // 6-item UI overhaul checks: Naming result handoff, frameless destination modal, sortable headers, Search inline filters, distinct task icon
        var namingXaml = File.ReadAllText(Path.Combine(appRoot, "Features/CreateOrRename/NamingOptionsView.xaml"));
        var destPickerXaml = File.ReadAllText(Path.Combine(appRoot, "Features/FileInput/CollectionFolderPicker.xaml"));
        var searchXaml = File.ReadAllText(Path.Combine(appRoot, "Features/Search/SearchView.xaml"));
        var workspacePageXaml = File.ReadAllText(Path.Combine(appRoot, "Features/Workspace/WorkspacePage.xaml"));

        Assert.Contains("用新文件去转换", namingXaml);
        Assert.Contains("用新文件去整理", namingXaml);
        Assert.Contains("新文件加入暂存区", namingXaml);
        Assert.Contains("x:Name=\"ModalHeader\"", destPickerXaml);
        Assert.Contains("x:Name=\"ModalConfirmButton\"", destPickerXaml);
        Assert.Contains("SortColumn_Click", fileInputXaml);
        Assert.DoesNotContain("Content=\"移出列表\"", fileInputXaml);
        Assert.Contains("ViewModel.SizeUnitHint, Mode=OneWay", searchXaml);
        Assert.Contains("LeftTasksIdleIcon\" Glyph=\"&#xE9D5;\"", workspacePageXaml);

        // Column sorting keeps software folders grouped at top and cycles asc/desc/default
        var sortVm = new FileAssistant.App.Features.Workspace.WorkspaceViewModel();
        sortVm.CreateFolder("Z分组");
        sortVm.AddSearchFiles([
            new FileAssistant.Core.Features.Search.SearchFile(@"C:\demo\b.png", false, 100, null),
            new FileAssistant.Core.Features.Search.SearchFile(@"C:\demo\a.png", false, 500, null)
        ]);
        sortVm.ToggleSort(FileAssistant.App.Features.Workspace.WorkspaceSortField.Name);
        Assert.True(sortVm.VisibleFiles[0].IsCollectionFolder);
        Assert.Equal("a.png", sortVm.VisibleFiles[1].Name);
        Assert.Equal("b.png", sortVm.VisibleFiles[2].Name);
        sortVm.ToggleSort(FileAssistant.App.Features.Workspace.WorkspaceSortField.Size);
        Assert.True(sortVm.VisibleFiles[0].IsCollectionFolder);
        Assert.Equal("a.png", sortVm.VisibleFiles[1].Name); // 500 > 100 (descending first for Size)
        sortVm.ToggleSort(FileAssistant.App.Features.Workspace.WorkspaceSortField.Size);
        Assert.Equal("b.png", sortVm.VisibleFiles[1].Name); // 100 < 500 (ascending second)
        sortVm.ToggleSort(FileAssistant.App.Features.Workspace.WorkspaceSortField.Size);
        Assert.Equal(FileAssistant.App.Features.Workspace.WorkspaceSortField.Default, sortVm.SortField);

        // Result card 1-click handoff in Convert & Copy
        Assert.Contains("用新文件去改名", convertXaml);
        Assert.Contains("用新文件去整理", convertXaml);
        Assert.Contains("新文件加入暂存区", convertXaml);
        Assert.Contains("用新文件去改名", copyXaml);
        Assert.Contains("用新文件去转换", copyXaml);
        Assert.Contains("新文件加入暂存区", copyXaml);
    }

    private sealed class PlannerNamingService : FileAssistant.Core.Features.CreateOrRename.INamingService
    {
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingPlan> PlanAsync(FileAssistant.Core.Features.CreateOrRename.NamingRequest request, CancellationToken token)
            => Task.FromResult(FileAssistant.Core.Features.CreateOrRename.NamingPlanner.Rename(
                request,
                request.Paths.Select(p => new FileAssistant.Core.Features.CreateOrRename.NamingSource(p, false, null)).ToArray(),
                _ => false));
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingReport> ExecuteAsync(FileAssistant.Core.Features.CreateOrRename.NamingPlan plan, IProgress<FileAssistant.Core.Engines.ProcessingProgress>? progress, CancellationToken token)
            => Task.FromResult(new FileAssistant.Core.Features.CreateOrRename.NamingReport(plan, [], [], [], false));
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingPlan> RetryAsync(FileAssistant.Core.Features.CreateOrRename.NamingReport report, CancellationToken token)
            => Task.FromResult(report.Plan);
        public Task<IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.NamingRecovery>> RecoveriesAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.NamingRecovery>>([]);
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingRestoreResult> RestoreAsync(string id, CancellationToken token)
            => Task.FromResult(new FileAssistant.Core.Features.CreateOrRename.NamingRestoreResult([], [], ""));
    }

    private sealed class NoopInteraction : FileAssistant.Core.Abstractions.IFileInteraction
    {
        public void Open(FileAssistant.Core.Features.Search.SearchFile file) { }
        public void Reveal(FileAssistant.Core.Features.Search.SearchFile file) { }
        public void CopyText(string text) { }
    }

    private sealed class NoopNamingService : FileAssistant.Core.Features.CreateOrRename.INamingService
    {
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingPlan> PlanAsync(FileAssistant.Core.Features.CreateOrRename.NamingRequest request, CancellationToken token)
            => Task.FromResult(new FileAssistant.Core.Features.CreateOrRename.NamingPlan(request, []));
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingReport> ExecuteAsync(FileAssistant.Core.Features.CreateOrRename.NamingPlan plan, IProgress<FileAssistant.Core.Engines.ProcessingProgress>? progress, CancellationToken token)
            => Task.FromResult(new FileAssistant.Core.Features.CreateOrRename.NamingReport(plan, [], [], [], false));
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingPlan> RetryAsync(FileAssistant.Core.Features.CreateOrRename.NamingReport report, CancellationToken token)
            => Task.FromResult(report.Plan);
        public Task<IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.NamingRecovery>> RecoveriesAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.NamingRecovery>>([]);
        public Task<FileAssistant.Core.Features.CreateOrRename.NamingRestoreResult> RestoreAsync(string id, CancellationToken token)
            => Task.FromResult(new FileAssistant.Core.Features.CreateOrRename.NamingRestoreResult([], [], ""));
    }
}
