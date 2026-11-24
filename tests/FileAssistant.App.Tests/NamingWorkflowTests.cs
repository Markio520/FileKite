using FileAssistant.App.Features.CreateOrRename;
using FileAssistant.App.Features.Outputs;
using FileAssistant.App.Features.Tasks;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.FileInput;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Tests;

public sealed class NamingWorkflowTests
{
    private readonly WorkspaceViewModel input = new(null, WorkspaceInputPurpose.Rename);
    private readonly FakeNaming service = new();
    private NamingViewModel Model() => new(service, input, new(new Interaction()), new()) { RenameBaseName = "文件", RenameAction = 0, CreateMethod = 0 };
    private void Add() => input.AddSearchFiles([new(@"C:\source\a.txt", false, 10, null), new(@"C:\source\b.txt", false, 20, null)]);
    [Fact] public async Task Custom_edits_keep_full_path_identity_and_survive_filtering_and_selection()
    {
        var vm = Model(); Add(); service.UsePlanner = true; vm.RenameAction = 7;
        input.AddSearchFiles([new(@"C:\other\a.txt", false, 10, null)]);
        vm.SetCustomName(@"C:\source\a.txt", "第一项"); vm.SetCustomName(@"C:\other\a.txt", "另一项");
        input.SetSelection([input.VisibleFiles.Single(x => x.Name == "b.txt")]); input.SearchText = "b.txt";
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(2, service.Request!.Paths.Count); Assert.DoesNotContain(@"C:\source\b.txt", service.Request.Paths);
        Assert.Equal(new[] { "第一项.txt", "另一项.txt" }, vm.Rows.Select(x => x.NewName));
        vm.RenameAction = 6; vm.RenameAction = 7; input.SearchText = "";
        Assert.Equal("第一项", input.VisibleFiles.Single(x => x.FullPath == @"C:\source\a.txt").NamePreview.CustomName);
        Assert.Equal(2, vm.CustomNameCount);
    }
    [Fact] public async Task Custom_name_editor_invalidates_preview_and_restoring_original_removes_edit()
    {
        var vm = Model(); Add(); service.UsePlanner = true; vm.RenameAction = 7;
        var editor = input.VisibleFiles[0].NamePreview;
        Assert.True(editor.CanEditName); Assert.False(vm.CanBuild);
        editor.CustomName = "new"; await vm.BuildPlanCommand.ExecuteAsync(null); Assert.True(vm.CanRun);
        editor.CustomName = ""; Assert.Null(vm.Plan); await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.False(vm.CanRun); Assert.True(vm.Rows[0].NeedsAttention);
        editor.CustomName = "a"; Assert.Equal(0, vm.CustomNameCount); Assert.False(vm.CanBuild);
    }
    [Fact] public async Task Custom_renaming_uses_only_explicit_edits_and_success_clears_them()
    {
        var vm = Model(); Add(); service.UsePlanner = true; vm.RenameAction = 7;
        vm.FolderContents = true; vm.IncludeSubfolders = true; vm.RenameFolders = false;
        vm.SetCustomName(@"C:\source\a.txt", "custom"); await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.False(service.Request!.FolderContents); Assert.False(service.Request.IncludeSubfolders); Assert.True(service.Request.RenameFolders);
        await vm.RunCommand.ExecuteAsync(null); Assert.Equal(0, vm.CustomNameCount);
    }
    [Fact] public async Task Rule_change_invalidates_plan_and_stale_async_result_is_ignored()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null); Assert.True(vm.CanRun);
        vm.Prefix = "new_"; Assert.Null(vm.Plan); Assert.Empty(vm.PlanItems); Assert.False(vm.CanRun);
        var pending = new TaskCompletionSource<NamingPlan>(); service.Pending = pending.Task;
        var task = vm.BuildPlanCommand.ExecuteAsync(null); vm.Suffix = "changed";
        pending.SetResult(FakeNaming.Make(service.Request!)); await task;
        Assert.Null(vm.Plan); Assert.False(vm.IsBusy);
    }
    [Fact] public async Task Filtering_does_not_change_hidden_selected_execution_scope()
    {
        var vm = Model(); Add(); input.SetSelection([input.VisibleFiles[0]]);
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        input.SearchText = "b.txt"; Assert.Equal(0, input.VisibleSelectedCount);
        Assert.Same(plan, vm.Plan); Assert.Equal(@"C:\source\a.txt", service.Request!.Paths.Single());
    }
    [Fact] public async Task Execution_uses_fixed_plan_and_notifies_only_actual_changes()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = vm.Plan; IReadOnlyList<PathChange>? changes = null;
        vm.PathsChanged += (_, value) => changes = value;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Same(plan, service.Executed); Assert.NotNull(changes); Assert.Null(vm.Plan); Assert.True(vm.HasResults);
    }
    [Fact] public async Task Creation_needs_no_file_input_and_invalid_numeric_parameter_is_visible()
    {
        var vm = Model(); vm.ModeValue = 1; vm.Destination = @"C:\out"; vm.Count = "oops";
        vm.AddDirectoryBatchCommand.Execute(null); Assert.Contains("请输入整数", vm.Feedback); Assert.False(vm.CanRun);
        vm.Count = "2"; vm.AddDirectoryBatchCommand.Execute(null); await vm.BuildPlanCommand.ExecuteAsync(null); Assert.True(vm.CanRun); Assert.Empty(service.Request!.Paths);
    }
    [Fact] public void Reference_mapping_handles_parent_and_swap_and_updates_software_undo_snapshot()
    {
        Add(); input.CreateFolder("软件分组");
        PathChange[] changes = [new(@"C:\source\a.txt", @"C:\source\temp", false), new(@"C:\source\b.txt", @"C:\source\a.txt", false), new(@"C:\source\temp", @"C:\source\b.txt", false), new(@"C:\source", @"C:\renamed", true)];
        input.ApplyPathChanges(changes);
        Assert.Equal(new[] { @"C:\renamed\b.txt", @"C:\renamed\a.txt" }, input.SearchEntries.Select(x => x.FullPath));
        input.UndoCommand.Execute(null);
        Assert.Equal(new[] { @"C:\renamed\b.txt", @"C:\renamed\a.txt" }, input.SearchEntries.Select(x => x.FullPath));
    }
    [Fact] public async Task Mode_change_clears_retry_but_next_batch_settings_preserve_creation_results()
    {
        var vm = Model(); vm.ModeValue = 1; vm.Destination = @"C:\out"; vm.AddDirectoryBatchCommand.Execute(null);
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan!;
        vm.Report = new(plan, [new(plan.Items[0], NamingOutcome.Failed, "failed")], [], [], false);
        Assert.True(vm.CanRetry); vm.ModeValue = 0; Assert.False(vm.CanRetry); Assert.Null(vm.Report);
        Assert.Null(vm.Plan); vm.Plan = plan; Assert.False(vm.CanRun);
        vm.ModeValue = 1; vm.Report = new(plan, [new(plan.Items[0], NamingOutcome.Failed, "failed")], [], [], false);
        vm.Prefix = "changed"; Assert.True(vm.CanRetry); Assert.Same(plan, vm.Report!.Plan);
    }
    [Fact] public void Successful_rename_merges_retained_target_membership_in_each_folder_and_undo_snapshot()
    {
        Add(); input.CreateFolder("软件分组");
        input.ApplyPathChanges([new(@"C:\source\a.txt", @"C:\source\b.txt", false)]);
        Assert.Single(input.SearchEntries); Assert.Equal(10, input.SearchEntries[0].Size);
        input.UndoCommand.Execute(null); Assert.Single(input.SearchEntries); Assert.Equal(@"C:\source\b.txt", input.SearchEntries[0].FullPath);
    }
    [Fact] public async Task Adding_files_shows_them_before_the_user_has_chosen_a_name()
    {
        var vm = Model(); vm.RenameBaseName = ""; Add();
        Assert.Equal(2, vm.Rows.Count); Assert.Equal("a.txt", vm.Rows[0].OriginalName);
        Assert.Contains("填写统一名称", vm.NextStep); Assert.False(vm.CanBuild);
        vm.RenameBaseName = "旅行照片"; service.UsePlanner = true;
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal("旅行照片_001.txt", vm.Rows[0].NewName); Assert.Equal("旅行照片_002.txt", vm.Rows[1].NewName);
        Assert.Equal("改名 2 项", vm.RunLabel); Assert.True(vm.CanRun);
    }
    [Fact] public async Task Uniform_names_treat_braces_as_literal_and_ignore_hidden_text_rules()
    {
        var vm = Model(); Add(); service.UsePlanner = true;
        vm.RenameBaseName = "项目{date}"; vm.Prefix = "old_"; vm.Find = "a"; vm.DeleteText = "项"; vm.RemoveAt = "invalid";
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal("项目{date}_001.txt", vm.Rows[0].NewName); Assert.True(vm.CanRun);
    }
    [Fact] public async Task Switching_between_replace_and_add_applies_only_visible_rules()
    {
        var vm = Model(); Add(); service.UsePlanner = true;
        vm.RenameAction = 1; vm.Find = "a"; vm.Replacement = "最终";
        vm.Prefix = "已审核_"; vm.DeleteText = "a"; vm.Start = "invalid";
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal("最终.txt", vm.Rows[0].NewName); Assert.Equal("b.txt", vm.Rows[1].NewName);
        vm.RenameAction = 2; await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal("已审核_a.txt", vm.Rows[0].NewName); Assert.Equal("已审核_b.txt", vm.Rows[1].NewName);
    }
    [Fact] public async Task Pasted_creation_list_is_exact_and_does_not_require_hidden_count_or_number_settings()
    {
        var vm = Model(); service.UsePlanner = true;
        vm.ModeValue = 1; vm.Destination = @"C:\out"; vm.CreateMethod = 1; vm.KindValue = 1;
        vm.Count = "invalid"; vm.Start = "invalid"; vm.Prefix = "old_"; vm.Subdirectories = "不应创建";
        AddNames(vm, "设计稿\n素材");
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "设计稿.txt", "素材.txt" }, vm.Rows.Select(x => x.NewName));
        Assert.True(vm.CanRun); Assert.Equal("创建全部 2 项", vm.RunLabel); Assert.Empty(service.Request!.Subdirectories);
        vm.CreateMethod = 0; vm.Count = "2"; vm.Start = "1"; vm.CreateBaseName = "合同";
        vm.AddDirectoryBatchCommand.Execute(null); await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "设计稿.txt", "素材.txt", "合同_001.txt", "合同_002.txt" }, vm.Rows.Select(x => x.NewName));
        Assert.Empty(service.Request!.Names);
    }
    [Fact] public async Task Position_deletion_explains_that_positions_start_at_one()
    {
        var vm = Model(); Add(); vm.RenameAction = 3; vm.DeleteMethod = 1;
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Contains("至少为 1", vm.NextStep); Assert.False(vm.CanRun);
    }
    [Fact] public async Task Completed_results_stay_visible_and_undo_targets_this_operation_not_the_first_record()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = vm.Plan!; await vm.RunCommand.ExecuteAsync(null);
        vm.Report = new(plan, [new(plan.Items[0], NamingOutcome.Success, "成功")], [], [], false, "current");
        service.History = [new("different", DateTimeOffset.Now, "其他操作", 1), new("current", DateTimeOffset.Now, "本次操作", 1)];
        await vm.RefreshRecoveriesCommand.ExecuteAsync(null);
        Assert.True(vm.ShowResults); Assert.True(vm.HasRows); Assert.False(vm.Empty);
        Assert.Equal("已完成", vm.Rows[0].Status); Assert.True(vm.CanUndoLast);
        await vm.UndoLastCommand.ExecuteAsync(null); Assert.Equal("current", service.Restored);
        Assert.False(vm.CanUndoLast); Assert.False(vm.ShowResults);
    }
    [Fact] public async Task Cancelled_rename_and_failed_creation_show_actual_outcomes_in_the_name_column()
    {
        var vm = Model(); Add(); service.UsePlanner = true; await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = vm.Plan!; vm.Plan = null;
        vm.Report = new(plan, [new(plan.Items[0], NamingOutcome.Cancelled, "已取消并恢复原名称", @"C:\source\a.txt")], [], [], true);
        Assert.Equal("a.txt", vm.Rows[0].NewName); Assert.StartsWith("已取消并恢复原名称", vm.RowDetails);
        Assert.Contains("计划：", vm.RowDetails); Assert.Contains(@"实际：C:\source\a.txt", vm.RowDetails);
        vm.ModeValue = 1; vm.Destination = @"C:\out"; vm.Count = "1"; vm.AddDirectoryBatchCommand.Execute(null); await vm.BuildPlanCommand.ExecuteAsync(null);
        plan = vm.Plan!; vm.Plan = null;
        vm.Report = new(plan, [new(plan.Items[0], NamingOutcome.Failed, "创建失败", plan.Items[0].Target)], [], [], false);
        Assert.Equal("未创建", vm.Rows[0].NewName); Assert.Contains("未创建该项目", vm.RowDetails);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adding_more_files_blocks_old_plan_until_import_finishes_or_is_cancelled(bool cancel)
    {
        var reader = new PendingReader(); var files = new WorkspaceViewModel(reader, WorkspaceInputPurpose.Rename);
        var vm = new NamingViewModel(service, files, new(new Interaction()), new()) { RenameBaseName = "文件", RenameAction = 0, CreateMethod = 0 };
        files.AddSearchFiles([new(@"C:\source\a.txt", false, 1, null)]);
        await vm.BuildPlanCommand.ExecuteAsync(null); Assert.True(vm.CanRun);
        var adding = files.ImportPathsAsync([@"C:\source\b.txt"]);
        Assert.True(vm.IsImporting); Assert.Contains("正在检查", vm.NextStep);
        Assert.False(vm.CanRun); Assert.False(vm.RunCommand.CanExecute(null));
        await vm.RunCommand.ExecuteAsync(null); Assert.Null(service.Executed);
        if (cancel) files.CancelLoadingCommand.Execute(null);
        reader.Completion.SetResult(new([new(@"C:\source\b.txt", FileImportStatus.Ready, "可用", new(@"C:\source\b.txt", false, 1, null))]));
        await adding; Assert.False(vm.IsImporting);
        if (cancel) { Assert.Single(files.EffectiveEntries); Assert.True(vm.CanRun); }
        else
        {
            Assert.Null(vm.Plan); Assert.False(vm.CanRun);
            await vm.BuildPlanCommand.ExecuteAsync(null); Assert.Equal(2, service.Request!.Paths.Count); Assert.True(vm.CanRun);
        }
    }
    [Fact] public void Input_and_picker_errors_are_visible_alongside_the_next_step()
    {
        var vm = Model(); Assert.Contains("先添加", vm.NextStep);
        vm.Feedback = "读取失败，请检查访问权限";
        Assert.Contains("读取失败", vm.NextStep); Assert.Contains("先添加", vm.NextStep);
        vm.ModeValue = 1; vm.Feedback = "无法选择目录，也可直接填写路径";
        Assert.Contains("无法选择目录", vm.NextStep); Assert.Contains("保存位置", vm.NextStep);
    }
    private sealed class PendingReader : IFileInputReader
    {
        public TaskCompletionSource<FileImportReport> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FileImportReport> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => Completion.Task;
    }
    [Fact] public async Task Shared_input_import_errors_reach_the_naming_operation_panel()
    {
        var reader = new PendingReader(); var files = new WorkspaceViewModel(reader, WorkspaceInputPurpose.Rename);
        var vm = new NamingViewModel(service, files, new(new Interaction()), new()) { CreateMethod = 0 };
        var adding = files.ImportPathsAsync([@"C:\missing.txt"]);
        reader.Completion.SetResult(new([new(@"C:\missing.txt", FileImportStatus.Unavailable, "文件不存在")]));
        await adding;
        Assert.Contains("不可访问 1 项", vm.NextStep); Assert.Contains("先添加", vm.NextStep); Assert.False(vm.CanRun);
    }
    [Fact] public async Task Creation_is_independent_of_a_rename_import_finishing_in_the_background()
    {
        var reader = new PendingReader(); var files = new WorkspaceViewModel(reader, WorkspaceInputPurpose.Rename);
        var vm = new NamingViewModel(service, files, new(new Interaction()), new()) { CreateMethod = 0 };
        files.AddSearchFiles([new(@"C:\source\a.txt", false, 1, null)]);
        var adding = files.ImportPathsAsync([@"C:\source\b.txt"]);
        vm.ModeValue = 1; vm.Destination = @"C:\out"; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.True(vm.CanBuild); Assert.False(vm.IsImporting); Assert.False(vm.CanCancelImport);
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        Assert.True(vm.CanRun); Assert.Empty(service.Request!.Paths);
        reader.Completion.SetResult(new([new(@"C:\source\b.txt", FileImportStatus.Ready, "可用", new(@"C:\source\b.txt", false, 1, null))]));
        await adding;
        Assert.Same(plan, vm.Plan); Assert.True(vm.CanRun); Assert.Equal(2, files.EffectiveEntries.Count);
        vm.ModeValue = 0; Assert.Null(vm.Plan); Assert.Equal(2, vm.Rows.Count);
    }
    [Theory]
    [InlineData(0, "001_a.txt")]
    [InlineData(1, "a_001.txt")]
    public async Task Numbering_can_keep_original_names_and_use_either_position(int position, string expected)
    {
        var vm = Model(); Add(); service.UsePlanner = true; vm.RenameAction = 6; vm.NumberPosition = position;
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(expected, vm.Rows[0].NewName);
        Assert.Equal("→ " + expected, input.VisibleFiles[0].NamePreview.Text);
        Assert.Contains(expected, vm.NamingExample);
        vm.NumberPosition = 1 - position;
        Assert.Null(vm.Plan); Assert.False(vm.CanRun);
    }
    [Fact] public async Task Sidebar_preview_uses_current_rule_then_the_resolved_name_from_the_plan()
    {
        var vm = Model(); Add(); service.UsePlanner = true; vm.RenameAction = 6;
        Assert.Equal("a_001.txt", vm.RuleSample.Target);
        Assert.Equal(["_001"], vm.RuleSample.Parts.Where(p => p.IsChange).Select(p => p.Text));
        vm.NumberPosition = 0;
        Assert.Equal("001_a.txt", vm.RuleSample.Target);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(vm.Plan!.Items[0].NewName, vm.RuleSample.Target);
        Assert.Equal("执行改名", vm.PrimaryActionLabel);
    }

    [Fact] public void Sidebar_highlights_separate_edits_and_keeps_combining_characters_intact()
    {
        var sample = NamingRuleSample.Compare("材料🌟.txt", "📁材料🌟e\u0301.txt");
        Assert.Equal(["📁", "e\u0301"], sample.Parts.Where(p => p.IsChange).Select(p => p.Text));
        Assert.Equal(sample.Target, string.Concat(sample.Parts.Select(p => p.Text)));
    }

    [Fact] public void Creation_selector_exposes_all_three_methods_and_maps_back_to_the_active_editor()
    {
        var vm = Model(); vm.ModeValue = 1;
        Assert.Equal(["填写名称", "编号生成", "组合模板"], vm.CreationInputModes.Select(x => x.Label));
        vm.CreationInputMode = 2; Assert.True(vm.Custom); Assert.Equal(2, vm.CreationInputMode);
        vm.CreationInputMode = 1; Assert.True(vm.CreateNumbered);
        vm.CreationInputMode = 0; Assert.True(vm.CreationByName);
        Assert.Equal("创建全部", vm.PrimaryActionLabel);
    }

    [Fact] public async Task Prefix_numbering_preserves_literal_braces_and_actual_video_extension()
    {
        var vm = Model(); service.UsePlanner = true;
        input.AddSearchFiles([new(@"C:\source\拍摄.mp4", false, 10, null)]);
        vm.RenameBaseName = "项目{date}"; vm.NumberPosition = 0;
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal("001_项目{date}.mp4", vm.Rows[0].NewName);
        Assert.Contains("001_项目{date}.mp4", vm.NamingExample); Assert.DoesNotContain(".jpg", vm.NamingExample);
    }
    [Fact] public async Task Panel_navigation_keeps_pending_plan_and_returning_from_results_allows_editing()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.OpenMoreCommand.Execute(null); Assert.True(vm.ShowMoreSettings); Assert.False(vm.ShowMainSettings);
        await vm.OpenHistoryCommand.ExecuteAsync(null); Assert.True(vm.ShowHistory); Assert.Same(plan, vm.Plan);
        vm.BackToSettingsCommand.Execute(null); Assert.True(vm.ShowMainSettings); Assert.Same(plan, vm.Plan);
        await vm.RunCommand.ExecuteAsync(null); Assert.True(vm.ShowResultPanel); Assert.False(vm.ShowMainSettings);
        vm.ContinueEditingCommand.Execute(null); Assert.True(vm.ShowMainSettings); Assert.False(vm.ShowResultPanel);
        vm.RenameBaseName = "新名称"; Assert.Null(vm.Report); Assert.False(vm.CanRun);
    }
    [Fact] public async Task More_settings_follow_the_current_task_without_leaving_an_empty_section()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.OpenMoreCommand.Execute(null);
        Assert.Equal(2, vm.MoreSection); Assert.Equal(new[] { 2, 0 }, vm.MoreSections.Select(x => x.Value));
        vm.MoreSection = 0; vm.BackToSettingsCommand.Execute(null); Assert.Same(plan, vm.Plan);
        vm.OpenMoreCommand.Execute(null); vm.RenameAction = 1;
        Assert.Equal(0, vm.MoreSection); Assert.Single(vm.MoreSections); Assert.True(vm.ShowMoreSettings);
        Assert.False(vm.ShowMoreNumbering); Assert.Null(vm.Plan);
    }
    [Fact] public void Folder_scope_is_only_offered_for_the_effective_folder_targets()
    {
        var vm = Model(); Add(); input.AddSearchFiles([new(@"C:\source\folder", true, null, null)]);
        Assert.Contains(vm.MoreSections, x => x.Value == 5);
        vm.OpenMoreCommand.Execute(null); vm.MoreSection = 5;
        input.SetSelection([input.VisibleFiles.First(x => x.Source?.IsFolder == false)]);
        Assert.DoesNotContain(vm.MoreSections, x => x.Value == 5);
        Assert.Contains(vm.MoreSections, x => x.Value == vm.MoreSection);
        Assert.True(vm.ShowMoreSettings);
    }
    [Fact] public async Task Directory_editor_supports_different_children_and_nested_structure_in_each_root()
    {
        var vm = Model(); service.UsePlanner = true; vm.ModeValue = 1; vm.CreateMethod = 3; vm.Destination = @"C:\out";
        vm.Count = "ignored"; vm.Start = "ignored"; vm.Names = "ignored"; vm.Subdirectories = "ignored";
        vm.AddDirectoryCommand.Execute(null); var first = vm.SelectedDirectory!; first.Name = "客户甲";
        vm.AddChildDirectoryCommand.Execute(null); var child = vm.SelectedDirectory!; child.Name = "素材";
        vm.AddChildDirectoryCommand.Execute(null); vm.SelectedDirectory!.Name = "原稿";
        vm.SelectedDirectory = first; vm.AddDirectoryCommand.Execute(null); var second = vm.SelectedDirectory!; second.Name = "客户乙";
        vm.AddChildDirectoryCommand.Execute(null); vm.SelectedDirectory!.Name = "交付";
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.True(vm.CanRun); Assert.Equal(5, vm.Rows.Count);
        Assert.Contains(vm.Rows, x => x.NewName == @"客户甲\素材\原稿");
        Assert.Contains(vm.Rows, x => x.NewName == @"客户乙\交付");
        Assert.DoesNotContain(vm.Rows, x => x.NewName == @"客户乙\素材");
        Assert.Equal(3, service.Request!.Entries!.Count(x => x.RelativePath.StartsWith("客户甲")));
        vm.SelectedDirectory = child; vm.RemoveDirectoryCommand.Execute(null);
        Assert.Empty(first.Children); Assert.Null(vm.Plan); Assert.Equal(2, vm.Directories.Count);
    }
    [Fact] public async Task Selecting_a_directory_focuses_its_preview_without_invalidating_the_plan()
    {
        var vm = Model(); service.UsePlanner = true; vm.ModeValue = 1; vm.CreateMethod = 3; vm.Destination = @"C:\out";
        vm.AddDirectoryCommand.Execute(null); var parent = vm.SelectedDirectory!; parent.Name = "项目";
        vm.AddChildDirectoryCommand.Execute(null); var child = vm.SelectedDirectory!; child.Name = "素材";
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.SelectedDirectory = parent; Assert.Equal("项目", vm.SelectedRow!.NewName);
        vm.SelectedDirectory = child; Assert.Equal(@"项目\素材", vm.SelectedRow!.NewName);
        Assert.Equal(@"项目\素材", vm.SelectedDirectoryPath); Assert.Same(plan, vm.Plan); Assert.True(vm.CanRun);
        vm.OpenMoreCommand.Execute(null); vm.BackToSettingsCommand.Execute(null);
        Assert.Same(child, vm.SelectedDirectory); Assert.Same(plan, vm.Plan);
    }
    [Fact] public void Directory_editor_blocks_invalid_names_and_case_insensitive_sibling_duplicates()
    {
        var vm = Model(); vm.ModeValue = 1; vm.CreateMethod = 3; vm.Destination = @"C:\out";
        vm.AddDirectoryCommand.Execute(null); var first = vm.SelectedDirectory!; first.Name = "../outside";
        Assert.False(vm.CanBuild); Assert.Contains("不支持", vm.NextStep);
        first.Name = "Assets"; vm.AddDirectoryCommand.Execute(null); vm.SelectedDirectory!.Name = "assets";
        Assert.False(vm.CanBuild); Assert.Contains("重名", vm.NextStep);
        vm.SelectedDirectory!.Name = "交付"; Assert.True(vm.CanBuild);
    }
    [Fact] public async Task Replacement_marks_unchanged_files_and_keeps_preview_after_filtering()
    {
        var vm = Model(); Add(); service.UsePlanner = true; vm.RenameAction = 1; vm.Find = "a"; vm.Replacement = "最终";
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal("→ 最终.txt", input.VisibleFiles[0].NamePreview.Text);
        Assert.Contains("未匹配", input.VisibleFiles[1].NamePreview.Status);
        input.SearchText = "b.txt";
        Assert.Single(input.VisibleFiles); Assert.Contains("未匹配", input.VisibleFiles[0].NamePreview.Status);
    }
    private NamingViewModel BulkModel(IDirectoryContentReader? reader = null)
    {
        service.UsePlanner = true;
        return new(service, input, new(new Interaction()), new(), reader) { ModeValue = 1, Destination = @"C:\out" };
    }
    private static void AddNames(NamingViewModel vm, string names)
    {
        vm.CreationNames = names; Assert.True(vm.CanAddDirectoryBatch);
        vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Empty(vm.CreationNames);
    }
    [Fact] public async Task Adding_ten_literal_names_does_not_execute_until_the_final_create_command()
    {
        var vm = BulkModel(); vm.Count = "ignored"; vm.Start = "ignored";
        vm.CreationNames = string.Join('\n', Enumerable.Range(1, 10).Select(x => "客户" + x));
        Assert.Empty(vm.Directories); Assert.False(vm.CanRun);
        vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Equal(10, vm.Directories.Count); Assert.Null(service.Executed);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Rows.Count); Assert.Equal("客户1", vm.Rows[0].NewName);
        Assert.True(vm.CanRun); Assert.Equal("创建全部 10 项", vm.RunLabel);
    }
    [Fact] public async Task Switching_parents_keeps_unsubmitted_inputs_and_completed_draft_structure()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙"); var root = vm.CurrentCreationDirectory;
        var first = vm.Directories[0]; var second = vm.Directories[1];
        vm.SelectCreationParents([first]); vm.CreationNames = "素材\n设计稿";
        vm.SelectCreationParents([second]); AddNames(vm, "交付");
        vm.SelectCreationParents([first]); Assert.Equal("素材\n设计稿", vm.CreationNames);
        vm.AddDirectoryBatchCommand.Execute(null); await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.SelectCreationParents([root]); vm.SelectCreationParents([second]);
        Assert.Equal("交付", Assert.Single(second.Children).Name); Assert.Same(plan, vm.Plan); Assert.True(vm.CanRun);
    }
    [Fact] public async Task Common_children_merge_into_multiple_parents_and_keep_each_original_structure()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙\n丙"); var parents = vm.Directories.ToArray();
        vm.SelectCreationParents([parents[0]]); AddNames(vm, "甲独有\n素材");
        vm.SelectCreationParents([parents[1]]); AddNames(vm, "乙独有");
        vm.SelectCreationParents(parents); AddNames(vm, "素材\n交付");
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Contains(vm.Rows, x => x.NewName == @"甲\甲独有"); Assert.Contains(vm.Rows, x => x.NewName == @"乙\乙独有");
        Assert.All(parents, p => Assert.Equal(1, p.Children.Count(x => x.Name == "素材")));
        Assert.All(parents, p => Assert.Contains(p.Children, x => x.Name == "交付"));
        vm.CreationNames = ""; Assert.Equal(3, parents[0].Children.Count);
    }
    [Fact] public async Task Mixed_numbered_and_named_batches_share_one_tree_and_allow_different_descendants()
    {
        var vm = BulkModel(); vm.ToggleCreationNumberingCommand.Execute(null);
        vm.Count = "5"; vm.CreateBaseName = "新建"; vm.NumberPosition = 0; vm.SeparatorValue = 2;
        Assert.True(vm.IsTreeCreation); vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Equal(5, vm.Directories.Count); Assert.Equal("001 新建", vm.Directories[0].Name);
        var roots = vm.Directories.ToArray();
        vm.BrowseCreationDirectory(roots[0]); AddNames(vm, "素材\n成片");
        vm.BrowseCreationDirectory(roots[1]); AddNames(vm, "合同\n发票");
        vm.BrowseCreationDirectory(roots[2]); vm.ToggleCreationNumberingCommand.Execute(null);
        vm.Count = "2"; vm.CreateBaseName = "设计稿"; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Same(roots[2], vm.CurrentCreationDirectory);
        Assert.Equal(2, roots[2].Children.Count); Assert.Equal("001 设计稿", roots[2].Children[0].Name);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(11, vm.Plan!.ReadyCount); Assert.Null(service.Executed);
        Assert.Contains(vm.Rows, x => x.NewName == @"001 新建\素材");
        Assert.DoesNotContain(vm.Rows, x => x.NewName == @"002 新建\素材");
    }
    [Fact] public async Task Paths_build_missing_ancestors_and_duplicate_paths_merge_without_replacing_children()
    {
        var vm = BulkModel(); AddNames(vm, "客户甲/素材/原片\n客户甲/交付\n客户乙/合同\n客户乙/发票\n客户丙/设计稿");
        var first = vm.Directories[0]; var material = first.Children[0];
        AddNames(vm, "客户甲/素材/原片\n客户甲/素材/原片\n客户乙/归档");
        Assert.Same(material, first.Children[0]); Assert.Single(material.Children);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Plan!.ReadyCount); Assert.Equal(3, vm.Directories.Count);
    }
    [Fact] public async Task Invalid_input_is_atomic_and_unsubmitted_input_blocks_execution_of_an_existing_plan()
    {
        var vm = BulkModel(); AddNames(vm, "素材"); await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.CreationNames = "交付\n../outside"; Assert.False(vm.CanRun);
        vm.AddDirectoryBatchCommand.Execute(null); Assert.Single(vm.Directories); Assert.Contains("不支持", vm.NextStep);
        Assert.Same(plan, vm.Plan); vm.CreationNames = "交付"; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Null(vm.Plan); await vm.BuildPlanCommand.ExecuteAsync(null); Assert.True(vm.CanRun);
    }
    [Fact] public async Task Adding_another_batch_invalidates_an_inflight_preview()
    {
        var vm = BulkModel(); AddNames(vm, "甲");
        var pending = new TaskCompletionSource<NamingPlan>(); service.Pending = pending.Task;
        var building = vm.BuildPlanCommand.ExecuteAsync(null); var original = service.Request!;
        AddNames(vm, "乙");
        pending.SetResult(NamingPlanner.Create(original, _ => false, null)); await building;
        Assert.Null(vm.Plan); Assert.False(vm.CanRun); Assert.Equal(2, vm.Directories.Count);
    }
    [Fact] public async Task Switching_file_formats_and_folders_keeps_one_queue_and_the_current_location()
    {
        var vm = BulkModel(); AddNames(vm, "项目"); var folder = vm.Directories.Single();
        var folderItems = folder.FolderChildren;
        vm.BrowseCreationDirectory(folder); vm.KindValue = 1; AddNames(vm, "说明\n备注");
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.FileExtension = ".docx"; vm.KindValue = 0; vm.KindValue = 1;
        Assert.Same(plan, vm.Plan); Assert.Same(folder, vm.CurrentCreationDirectory);
        Assert.Same(folder, Assert.Single(vm.CreationParents));
        AddNames(vm, "合同"); vm.KindValue = 0; AddNames(vm, "素材");
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.Plan!.ReadyCount);
        Assert.Equal(new[] { "说明.txt", "备注.txt", "合同.docx", "素材" }, vm.CreationContents.Select(x => x.Name));
        Assert.EndsWith("blank.docx", vm.Plan.Items.Single(x => x.Target.EndsWith("合同.docx")).TemplatePath);
        Assert.Single(folder.FolderChildren);
        Assert.Same(folderItems, folder.FolderChildren);
    }
    [Fact] public void Destination_selection_and_checkbox_fallback_remain_explicit()
    {
        var vm = BulkModel(); AddNames(vm, "甲"); var root = vm.CreationParents.Single(); var child = vm.Directories.Single();
        root.IsTarget = false; vm.SelectCreationParents([]); Assert.True(root.IsTarget);
        vm.SelectCreationParents([root, child]); Assert.Same(child, vm.CreationParents.Single()); Assert.False(root.IsTarget);
        vm.SelectCreationParents([root]); Assert.True(root.IsTarget); Assert.False(child.IsTarget);
    }
    [Fact] public async Task Center_only_shows_the_clicked_directorys_direct_children()
    {
        var vm = BulkModel(); var root = vm.CurrentCreationDirectory;
        AddNames(vm, "甲/素材/原稿\n甲/设计稿\n乙/交付");
        Assert.Equal(new[] { "甲", "乙" }, vm.CreationContents.Select(x => x.Name));
        var first = vm.Directories[0]; vm.BrowseCreationDirectory(first);
        Assert.Equal(new[] { "素材", "设计稿" }, vm.CreationContents.Select(x => x.Name));
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        Assert.DoesNotContain(vm.CreationContents, x => x.Name == "原稿" || x.Name == "交付");
        vm.BrowseCreationParentCommand.Execute(null); Assert.Same(root, vm.CurrentCreationDirectory); Assert.Same(plan, vm.Plan);
    }
    [Fact] public async Task Browsing_a_folder_does_not_change_multiple_checked_creation_targets()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙"); var parents = vm.Directories.ToArray();
        vm.SelectCreationParents(parents, browse: false); Assert.True(vm.CurrentCreationDirectory.IsDestination);
        AddNames(vm, "素材/原稿\n交付"); await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.BrowseCreationDirectory(parents[1]); Assert.Equal(parents, vm.CreationParents);
        Assert.Equal(2, vm.CreationContents.Count); Assert.All(parents, x => Assert.True(x.IsTarget)); Assert.Same(plan, vm.Plan);

        var material = parents[1].FolderChildren.Single(folder => folder.Name == "素材");
        vm.BrowseCreationDirectory(material.FolderChildren.Single());
        Assert.Equal(new[] { "out", "乙", "素材", "原稿" }, vm.CreationBreadcrumbs.Select(crumb => crumb.Name));
        vm.BrowseCreationParentCommand.Execute(null);
        Assert.Same(material, vm.CurrentCreationDirectory);
        vm.BrowseCreationDirectory(vm.CreationBreadcrumbs[0].Directory);
        Assert.True(vm.CurrentCreationDirectory.IsDestination);
        Assert.Single(vm.CreationBreadcrumbs);
        Assert.Equal(parents, vm.CreationParents);
        Assert.Same(plan, vm.Plan);
    }
    [Fact] public async Task Real_folders_are_navigable_and_only_new_descendants_are_created()
    {
        var reader = new ContentsReader { Read = (path, _) => Task.FromResult<IReadOnlyList<SearchFile>>(
            path == @"C:\out" ? [new(@"C:\out\已有", true, null, null), new(@"C:\out\说明.txt", false, 12, null)] : []) };
        service.ExistingDirectories.Add(@"C:\out\已有");
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        var existing = vm.Directories.Single(); Assert.True(existing.IsExisting);
        vm.BrowseCreationDirectory(existing); Assert.Equal(@"C:\out\已有", vm.CurrentCreationPath);
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null); AddNames(vm, "素材/原片\n交付");
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.DoesNotContain(service.Request!.Entries!, x => x.RelativePath == "已有");
        Assert.Equal(NamingAction.Existing, vm.Plan!.Items[0].Action); Assert.Equal(3, vm.Plan.ReadyCount);
        Assert.Null(service.Executed);
    }
    [Fact] public async Task A_late_directory_read_cannot_replace_the_current_folder_or_add_stale_tree_nodes()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SearchFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new ContentsReader { Read = (path, _) => path == @"C:\out" ? pending.Task : Task.FromResult<IReadOnlyList<SearchFile>>([]) };
        var vm = BulkModel(reader); AddNames(vm, "甲"); var reading = vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        vm.BrowseCreationDirectory(vm.Directories.Single()); AddNames(vm, "素材"); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        pending.SetResult([new(@"C:\out\旧目录", true, null, null)]); await reading;
        Assert.Equal(@"C:\out\甲", vm.CurrentCreationPath); Assert.Equal("素材", Assert.Single(vm.CreationContents).Name);
        Assert.Single(vm.Directories); Assert.False(vm.IsReadingCreationContents);
    }
    [Fact] public async Task Conflicting_file_numbering_keeps_children_under_the_remapped_parent()
    {
        var vm = BulkModel(); AddNames(vm, "甲/素材"); var parent = vm.Directories.Single();
        vm.BrowseCreationDirectory(parent); service.Existing.Add(@"C:\out\甲"); vm.ConflictValue = 2;
        await vm.BuildPlanCommand.ExecuteAsync(null); var target = vm.Plan!.Items[0].DisplayPath;
        Assert.NotEqual(@"C:\out\甲", target); Assert.Equal(target, vm.CurrentCreationPath);
        Assert.Equal(Path.Combine(target, "素材"), Assert.Single(vm.CreationContents).Path);
    }
    [Fact] public async Task Created_folders_remain_navigable_and_accept_another_different_batch_without_recreation()
    {
        var vm = BulkModel(); AddNames(vm, "甲/素材\n乙/合同");
        await vm.BuildPlanCommand.ExecuteAsync(null); await vm.RunCommand.ExecuteAsync(null);
        Assert.All(vm.Directories, x => Assert.True(x.IsExisting)); Assert.Equal(0, vm.PendingDirectoryCount);
        vm.BrowseCreationDirectory(vm.Directories[1]); Assert.True(vm.ShowMainSettings);
        AddNames(vm, "发票"); await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Plan!.ReadyCount); Assert.Contains(vm.Plan.Items, x => x.Action == NamingAction.Existing && x.Target == @"C:\out\乙");
        Assert.DoesNotContain(vm.Plan.Items, x => x.Target == @"C:\out\甲");
    }
    [Fact] public async Task Completed_items_are_not_duplicated_by_the_real_listing()
    {
        var reader = new ContentsReader { Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([new(@"C:\out\甲", true, null, null)]) };
        var vm = BulkModel(reader); AddNames(vm, "甲"); await vm.BuildPlanCommand.ExecuteAsync(null); await vm.RunCommand.ExecuteAsync(null);
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null); Assert.Equal("已创建", Assert.Single(vm.CreationContents).Status);
    }
    [Fact] public async Task Read_errors_are_visible_and_refresh_keeps_pending_children()
    {
        var reader = new ContentsReader { Read = (_, _) => throw new UnauthorizedAccessException("拒绝访问") };
        var vm = BulkModel(reader); AddNames(vm, "新项目"); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Contains("拒绝访问", vm.CreationContentsNotice); Assert.Single(vm.CreationContents);
        reader.Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([]);
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null); Assert.Empty(vm.CreationContentsNotice);
        vm.BrowseCreationDirectory(vm.Directories.Single()); reader.Read = (_, _) => throw new DirectoryNotFoundException();
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null); Assert.Empty(vm.CreationContentsNotice);
    }
    [Fact] public async Task Retrying_one_child_preserves_completed_rows_and_parent_navigation()
    {
        var vm = BulkModel(); AddNames(vm, "甲/素材\n甲/交付"); var parent = vm.Directories.Single(); vm.BrowseCreationDirectory(parent);
        await vm.BuildPlanCommand.ExecuteAsync(null); var full = vm.Plan!; var failed = full.Items.Single(x => x.Target.EndsWith("交付"));
        vm.Report = new(full, full.Items.Select(x => new NamingItemResult(x, x.Id == failed.Id ? NamingOutcome.Failed : NamingOutcome.Success, "结果", x.Target)).ToArray(), [], [], false);
        vm.Plan = null; var retry = full with { Items = [failed] }; vm.Plan = retry;
        Assert.Equal("已创建", vm.CreationContents.Single(x => x.Name == "素材").Status);
        Assert.Equal("待创建", vm.CreationContents.Single(x => x.Name == "交付").Status);
        vm.Report = new(retry, [new(failed, NamingOutcome.Success, "成功", failed.Target)], [], [], false); vm.Plan = null;
        Assert.All(vm.CreationContents, x => Assert.Equal("已创建", x.Status)); Assert.StartsWith("已创建", parent.Preview);
    }
    [Theory]
    [InlineData("../其他")]
    [InlineData(@"C:\其他")]
    [InlineData("甲//乙")]
    [InlineData("甲/CON")]
    public void Invalid_paths_cannot_escape_or_partially_modify_the_draft(string path)
    {
        var vm = BulkModel(); vm.CreationNames = "安全\n" + path; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Empty(vm.Directories); Assert.False(vm.CanRun); Assert.NotEmpty(vm.Feedback); Assert.Null(service.Executed);
    }
    [Fact] public async Task Removing_a_pending_subtree_never_removes_existing_folders()
    {
        var reader = new ContentsReader { Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([new(@"C:\out\已有", true, null, null)]) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null); AddNames(vm, "待建/子目录");
        vm.SelectedCreationContent = vm.CreationContents.Single(x => x.Name == "已有"); Assert.False(vm.CanRemovePendingContent);
        vm.SelectedCreationContent = vm.CreationContents.Single(x => x.Name == "待建"); vm.RemovePendingContentCommand.Execute(null);
        Assert.Equal("已有", Assert.Single(vm.Directories).Name); Assert.Equal(0, vm.PendingDirectoryCount);
    }
    [Fact] public async Task Numeric_hierarchy_and_leaf_selection_add_the_same_children_to_all_four_leaves()
    {
        var vm = BulkModel(); AddNames(vm, "1/1/2\n1/1/3\n1/2/2\n1/2/3");
        var root = vm.CurrentCreationDirectory;
        Assert.Equal(7, vm.PendingDirectoryCount); Assert.Single(vm.CreationContents);
        await vm.SelectLeafFoldersCommand.ExecuteAsync(null);
        Assert.Equal(new[] { @"1\1\2", @"1\1\3", @"1\2\2", @"1\2\3" }, vm.CreationParents.Select(x => x.DisplayPath));
        vm.BrowseCreationDirectory(vm.Directories[0]);
        Assert.Equal(4, vm.CreationParents.Count); Assert.Equal(2, vm.CreationContents.Count);
        AddNames(vm, "素材\n交付"); await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(15, vm.Plan!.ReadyCount); Assert.All(vm.CreationParents, p => Assert.Equal(new[] { "素材", "交付" }, p.Children.Select(x => x.Name)));
        vm.BrowseCreationDirectory(root); Assert.Single(vm.CreationContents);
        await vm.SelectDirectChildrenCommand.ExecuteAsync(null); Assert.Single(vm.CreationParents);
    }
    [Fact] public async Task Number_only_generation_can_build_each_level_without_manual_paths()
    {
        var vm = BulkModel(); AddNames(vm, "1"); vm.BrowseCreationDirectory(vm.Directories[0]);
        vm.CreateMethod = 0; vm.CreateBaseName = ""; vm.Count = "2"; vm.Digits = "1";
        vm.AddDirectoryBatchCommand.Execute(null);
        await vm.SelectDirectChildrenCommand.ExecuteAsync(null);
        vm.CreateMethod = 0; vm.Start = "2"; vm.AddDirectoryBatchCommand.Execute(null);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "1", @"1\1", @"1\2", @"1\1\2", @"1\1\3", @"1\2\2", @"1\2\3" }, vm.Plan!.Items.Select(x => x.RelativeTarget));
    }
    [Fact] public async Task Leaf_selection_reads_real_descendants_and_skips_directory_links()
    {
        var calls = new List<string>();
        var reader = new ContentsReader { Read = (path, _) =>
        {
            calls.Add(path);
            return Task.FromResult<IReadOnlyList<SearchFile>>(path switch
            {
                @"C:\out" => [new(@"C:\out\甲", true, null, null), new(@"C:\out\乙", true, null, null), new(@"C:\out\链接", true, null, null) { IsReparsePoint = true }],
                @"C:\out\甲" => [new(@"C:\out\甲\末级", true, null, null)],
                _ => []
            });
        } };
        var vm = BulkModel(reader); await vm.SelectLeafFoldersCommand.ExecuteAsync(null);
        Assert.Equal(new[] { @"甲\末级", "乙" }, vm.CreationParents.Select(x => x.DisplayPath));
        Assert.DoesNotContain(@"C:\out\链接", calls); Assert.False(vm.IsSelectingTargets);
        AddNames(vm, "共用"); Assert.Equal(2, vm.PendingDirectoryCount);
    }
    [Fact] public async Task A_failed_or_cancelled_target_scan_keeps_the_previous_selection()
    {
        var reader = new ContentsReader { Read = (_, _) => throw new UnauthorizedAccessException("denied") };
        var vm = BulkModel(reader); var original = vm.CreationParents.ToArray();
        await vm.SelectLeafFoldersCommand.ExecuteAsync(null);
        Assert.Equal(original, vm.CreationParents); Assert.Contains("未更改勾选", vm.Feedback);
        reader.Read = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return []; };
        var selecting = vm.SelectLeafFoldersCommand.ExecuteAsync(null);
        vm.CancelTargetSelectionCommand.Execute(null); await selecting;
        Assert.Equal(original, vm.CreationParents); Assert.False(vm.IsSelectingTargets); Assert.True(vm.CanEdit);
    }
    [Fact] public async Task Custom_extensions_require_templates_or_explicit_placeholders_and_snapshot_each_source()
    {
        var vm = BulkModel(); vm.KindValue = 1; vm.FileExtension = "xyz";
        vm.CreationNames = "自定义"; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Empty(vm.Directories); Assert.Contains("模板", vm.Feedback);
        vm.FileContentValue = 2; vm.AddDirectoryBatchCommand.Execute(null);
        vm.FileContentValue = 1; vm.Template = @"C:\templates\甲.xyz"; AddNames(vm, "甲");
        vm.Template = @"C:\templates\乙.xyz"; AddNames(vm, "乙");
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Null(vm.Plan!.Items[0].TemplatePath);
        Assert.Equal(@"C:\templates\甲.xyz", vm.Plan.Items[1].TemplatePath);
        Assert.Equal(@"C:\templates\乙.xyz", vm.Plan.Items[2].TemplatePath);
        Assert.Equal("自定义.xyz", vm.CreationContents[0].Name);
    }
    [Fact] public void Invalid_file_batch_cannot_partially_add_ancestors_or_replace_folders()
    {
        var vm = BulkModel(); AddNames(vm, "保留.txt"); vm.KindValue = 1;
        vm.CreationNames = "新目录/新文件.txt\n保留.txt"; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Equal("保留.txt", Assert.Single(vm.Directories).Name);
        vm.CreationNames = "新目录/可用.txt\n不能伪造.xyz"; vm.AddDirectoryBatchCommand.Execute(null);
        Assert.Single(vm.Directories); Assert.Contains("没有内置空白模板", vm.Feedback);
    }
    [Fact] public async Task Input_and_current_highlight_survive_type_switches_and_only_the_browsed_row_is_current()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙"); var root = vm.CurrentCreationDirectory;
        vm.BrowseCreationDirectory(vm.Directories[0]); vm.CreationNames = "尚未添加";
        vm.KindValue = 1; vm.FileExtension = ".doc"; vm.KindValue = 0;
        Assert.Equal("尚未添加", vm.CreationNames); Assert.True(vm.Directories[0].IsCurrent); Assert.False(root.IsCurrent);
        vm.CreationNames = ""; await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.BrowseCreationDirectory(vm.Directories[1]);
        Assert.False(vm.Directories[0].IsCurrent); Assert.True(vm.Directories[1].IsCurrent); Assert.Same(plan, vm.Plan);
    }
    [Fact] public async Task Refresh_after_external_deletion_removes_created_subtrees_without_resurrecting_report_rows()
    {
        var reader = new ContentsReader(); var vm = BulkModel(reader);
        AddNames(vm, "项目/素材/原片\n项目/交付"); await vm.BuildPlanCommand.ExecuteAsync(null); await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("已创建", Assert.Single(vm.CreationContents).Status); Assert.NotNull(vm.Report);
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Empty(vm.Directories); Assert.Empty(vm.CreationContents); Assert.Equal(0, vm.PendingCreationCount);
        Assert.False(vm.CanRun); Assert.Same(vm.CurrentCreationDirectory, Assert.Single(vm.CreationParents));
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Empty(vm.CreationContents); Assert.NotNull(vm.Report);
    }
    [Fact] public async Task Refresh_rechecks_loaded_descendants_without_scanning_unvisited_directories()
    {
        var removed = false; var calls = new List<string>();
        var reader = new ContentsReader { Read = (path, _) =>
        {
            calls.Add(path);
            return Task.FromResult<IReadOnlyList<SearchFile>>(path switch
            {
                @"C:\out" => [new(@"C:\out\甲", true, null, null), new(@"C:\out\未访问", true, null, null)],
                @"C:\out\甲" when !removed => [new(@"C:\out\甲\子目录", true, null, null)],
                _ => []
            });
        } };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null); var root = vm.CurrentCreationDirectory;
        var first = vm.Directories.First(x => x.Name == "甲"); vm.BrowseCreationDirectory(first); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Single(first.Children); vm.BrowseCreationDirectory(root); removed = true; calls.Clear();
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Empty(first.Children); Assert.Equal(new[] { @"C:\out", @"C:\out\甲" }, calls); Assert.Equal(0, vm.PendingCreationCount);
    }
    [Fact] public async Task Deleting_the_current_directory_returns_to_its_surviving_parent_and_removes_stale_targets()
    {
        var removed = false;
        var reader = new ContentsReader { Read = (path, _) => Task.FromResult<IReadOnlyList<SearchFile>>(path switch
        {
            @"C:\out" => [new(@"C:\out\甲", true, null, null)],
            @"C:\out\甲" when !removed => [new(@"C:\out\甲\子目录", true, null, null)],
            @"C:\out\甲\子目录" when removed => throw new DirectoryNotFoundException(),
            _ => []
        }) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        var parent = vm.Directories.Single(); vm.BrowseCreationDirectory(parent); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        vm.BrowseCreationDirectory(parent.Children.Single()); removed = true;
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Same(parent, vm.CurrentCreationDirectory); Assert.Same(parent, Assert.Single(vm.CreationParents));
        Assert.Empty(parent.Children); Assert.Empty(vm.CreationContents); Assert.False(vm.IsReadingCreationContents);
    }
    [Fact] public async Task Deleted_parent_retains_only_uncreated_work_and_cannot_be_silently_recreated()
    {
        var removed = false;
        var reader = new ContentsReader { Read = (path, _) => Task.FromResult<IReadOnlyList<SearchFile>>(removed ? [] : path switch
        {
            @"C:\out" => [new(@"C:\out\甲", true, null, null)],
            @"C:\out\甲" => [new(@"C:\out\甲\旧目录", true, null, null)],
            _ => []
        }) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null); var root = vm.CurrentCreationDirectory;
        var parent = vm.Directories.Single(); vm.BrowseCreationDirectory(parent); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        AddNames(vm, "尚未创建"); await vm.BuildPlanCommand.ExecuteAsync(null);
        vm.BrowseCreationDirectory(root); removed = true; await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.True(parent.IsMissing); Assert.Equal("尚未创建", Assert.Single(parent.Children).Name);
        Assert.Equal(1, vm.PendingCreationCount); Assert.False(vm.CanBuild); Assert.False(vm.CanRun); Assert.Contains("已删除", vm.CreationContents[0].Status);
        vm.Destination = @"C:\new"; Assert.False(parent.IsMissing); Assert.Equal(2, vm.PendingCreationCount); Assert.True(vm.CanBuild);
    }
    [Fact] public async Task An_old_async_plan_cannot_restore_targets_removed_during_refresh()
    {
        var removed = false;
        var reader = new ContentsReader { Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>(removed ? [] : [new(@"C:\out\甲", true, null, null)]) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        AddNames(vm, "新目录"); var pending = new TaskCompletionSource<NamingPlan>(); service.Pending = pending.Task;
        var building = vm.BuildPlanCommand.ExecuteAsync(null); var request = service.Request!;
        removed = true; await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        pending.SetResult(NamingPlanner.Create(request, _ => false, null)); await building;
        Assert.Null(vm.Plan); Assert.Equal("新目录", Assert.Single(vm.Directories).Name); Assert.False(vm.CanRun);
    }
    [Fact] public async Task Refresh_error_preserves_existing_entries_and_never_turns_them_into_new_work()
    {
        var reader = new ContentsReader { Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([new(@"C:\out\甲", true, null, null)]) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null); var node = vm.Directories.Single();
        reader.Read = (_, _) => throw new UnauthorizedAccessException("denied"); await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.Same(node, vm.Directories.Single()); Assert.True(node.IsExisting); Assert.False(node.IsMissing);
        Assert.Equal(0, vm.PendingCreationCount); Assert.Contains("denied", vm.CreationContentsNotice);
    }
    [Fact] public async Task Context_rename_and_remove_use_the_clicked_draft_without_changing_another_selected_folder()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙/子目录"); var first = vm.Directories[0]; var second = vm.Directories[1];
        vm.SelectCreationParents([first]); await vm.BuildPlanCommand.ExecuteAsync(null);
        vm.RenameCreationDraft(second, "新名称"); Assert.Null(vm.Plan);
        Assert.Equal(@"C:\out\新名称", vm.CreationContext(second)!.Path); Assert.Same(first, vm.CreationParents.Single());
        Assert.Throws<ArgumentException>(() => vm.RenameCreationDraft(second, "甲"));
        vm.RemoveCreationDraft(second); Assert.Same(first, vm.Directories.Single()); Assert.Same(first, vm.CurrentCreationDirectory);
        first.IsExisting = true; vm.RemoveCreationDraft(first); vm.RenameCreationDraft(first, "不应改名"); Assert.Equal("甲", first.Name);
    }
    [Fact] public void Context_new_item_targets_the_clicked_folder_and_only_prepares_the_editor()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙"); var first = vm.Directories[0]; var second = vm.Directories[1];
        vm.SelectCreationParents([first, second]); var focused = false; vm.CreationEditorRequested += (_, _) => focused = true;
        vm.PrepareCreationAt(second, true);
        Assert.True(focused); Assert.True(vm.IsFileCreation); Assert.Same(second, vm.CurrentCreationDirectory);
        Assert.Same(second, vm.CreationParents.Single()); Assert.Empty(second.Children); Assert.Equal(2, vm.PendingCreationCount);
    }
    [Fact] public void Common_context_actions_use_the_clicked_real_item_and_never_export_pending_paths()
    {
        var interaction = new CapturingInteraction(); var vm = new NamingViewModel(service, input, new(new Interaction()), new(), fileInteraction: interaction) { ModeValue = 1, Destination = @"C:\out" };
        var real = new CreationContentRow("真实.txt", @"C:\out\真实.txt", false, "已有", "", true);
        var pending = new CreationContentRow("草稿.txt", @"C:\out\草稿.txt", false, "待创建", "");
        vm.OpenCreationItem(real); Assert.Equal(real.Path, interaction.Opened);
        vm.OpenCreationItem(real, true); Assert.Equal(real.Path, interaction.Revealed);
        vm.CopyCreationText(pending); Assert.Equal(pending.Path, interaction.Copied);
        IReadOnlyList<SearchFile>? sent = null; vm.CreationCopyRequested += (_, files) => sent = files;
        vm.TransferCreationItem(pending, false); Assert.Null(sent);
        vm.TransferCreationItem(real, false); Assert.Equal(real.Path, Assert.Single(sent!).FullPath);
        vm.OpenCreationItem(pending); Assert.Equal(real.Path, interaction.Opened);
    }
    [Fact] public async Task Deleting_and_restoring_the_save_location_keeps_pending_work_but_blocks_creation_while_missing()
    {
        var removed = false;
        var reader = new ContentsReader { Read = (_, _) => removed ? throw new DirectoryNotFoundException() : Task.FromResult<IReadOnlyList<SearchFile>>([]) };
        var vm = BulkModel(reader); AddNames(vm, "未创建"); await vm.BuildPlanCommand.ExecuteAsync(null); Assert.True(vm.CanRun);
        removed = true; await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.True(vm.CurrentCreationDirectory.IsMissing); Assert.False(vm.CanRun); Assert.False(vm.CanBuild);
        Assert.Equal("未创建", Assert.Single(vm.Directories).Name);
        removed = false; await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        Assert.False(vm.CurrentCreationDirectory.IsMissing); Assert.True(vm.CanBuild);
    }
    [Fact] public async Task Target_checkboxes_are_opt_in_and_browsing_in_manual_selection_keeps_the_checked_target()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙"); var root = vm.CurrentCreationDirectory;
        var first = vm.Directories[0]; var second = vm.Directories[1];
        Assert.False(root.ShowTargetCheckBox); Assert.All(vm.Directories, x => Assert.False(x.ShowTargetCheckBox));
        vm.BrowseCreationDirectory(first); await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        vm.ChooseCreationTargetsCommand.Execute(null); Assert.True(first.ShowTargetCheckBox);
        vm.BrowseCreationDirectory(second);
        Assert.Same(second, vm.CurrentCreationDirectory); Assert.Same(first, Assert.Single(vm.CreationParents));
        vm.UseCurrentCreationFolderCommand.Execute(null);
        Assert.False(vm.IsChoosingCreationTargets); Assert.False(root.ShowTargetCheckBox);
        Assert.All(vm.Directories, x => Assert.False(x.ShowTargetCheckBox)); Assert.Same(second, Assert.Single(vm.CreationParents)); Assert.Same(plan, vm.Plan);
    }
    [Fact] public async Task Target_shortcuts_show_checkboxes_and_changing_location_restores_normal_navigation()
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙");
        await vm.SelectDirectChildrenCommand.ExecuteAsync(null);
        Assert.True(vm.IsChoosingCreationTargets); Assert.All(vm.Directories, x => Assert.True(x.ShowTargetCheckBox));
        AddNames(vm, "素材"); Assert.All(vm.Directories, x => Assert.True(x.Children.Single().ShowTargetCheckBox));
        vm.Destination = @"C:\another";
        Assert.False(vm.IsChoosingCreationTargets); Assert.All(vm.Directories, x => Assert.False(x.ShowTargetCheckBox));
        Assert.Equal(4, vm.PendingDirectoryCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Target_scan_removes_deleted_cached_folders_and_preserves_uncreated_children(bool leaves)
    {
        var removed = false;
        var reader = new ContentsReader { Read = (path, _) => Task.FromResult<IReadOnlyList<SearchFile>>(path == @"C:\out"
            ? removed ? [new(@"C:\out\保留", true, null, null)]
                : [new(@"C:\out\已删除", true, null, null), new(@"C:\out\保留", true, null, null)]
            : []) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null); var root = vm.CurrentCreationDirectory;
        var deleted = vm.Directories.First(x => x.Name == "已删除"); vm.BrowseCreationDirectory(deleted);
        AddNames(vm, "尚未创建"); vm.BrowseCreationDirectory(root); await vm.BuildPlanCommand.ExecuteAsync(null);
        removed = true;
        await (leaves ? vm.SelectLeafFoldersCommand : vm.SelectDirectChildrenCommand).ExecuteAsync(null);
        Assert.Equal("保留", Assert.Single(vm.CreationParents).Name);
        Assert.True(deleted.IsMissing); Assert.False(deleted.IsTarget); Assert.Equal("尚未创建", Assert.Single(deleted.Children).Name);
        Assert.Null(vm.Plan); Assert.False(vm.CanRun); Assert.Equal(1, vm.PendingCreationCount);
        Assert.All(vm.CreationContents.Where(x => x.Name == "已删除"), x => Assert.False(x.IsExisting));
    }
    [Fact] public async Task Target_scan_of_a_deleted_current_folder_returns_to_its_surviving_parent()
    {
        var removed = false;
        var reader = new ContentsReader { Read = (path, _) => Task.FromResult<IReadOnlyList<SearchFile>>(path switch
        {
            @"C:\out" when !removed => [new(@"C:\out\已删除", true, null, null)],
            @"C:\out\已删除" when removed => throw new DirectoryNotFoundException(),
            _ => []
        }) };
        var vm = BulkModel(reader); await vm.ReloadCreationContentsCommand.ExecuteAsync(null); var root = vm.CurrentCreationDirectory;
        vm.BrowseCreationDirectory(vm.Directories.Single()); removed = true;
        await vm.SelectLeafFoldersCommand.ExecuteAsync(null);
        Assert.Same(root, vm.CurrentCreationDirectory); Assert.Same(root, Assert.Single(vm.CreationParents)); Assert.Empty(vm.Directories);
        Assert.False(vm.IsSelectingTargets); Assert.False(vm.CanRun);
    }
    [Fact] public async Task Target_scan_supersedes_an_older_contents_read()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<SearchFile>>();
        var reader = new ContentsReader { Read = (_, _) => pending.Task };
        var vm = BulkModel(reader); var reading = vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        reader.Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([new(@"C:\out\当前目录", true, null, null)]);
        await vm.SelectDirectChildrenCommand.ExecuteAsync(null);
        pending.SetResult([new(@"C:\out\过期目录", true, null, null)]); await reading;
        Assert.Equal("当前目录", Assert.Single(vm.Directories).Name);
        Assert.Equal("当前目录", Assert.Single(vm.CreationContents).Name);
        Assert.Same(vm.Directories.Single(), Assert.Single(vm.CreationParents)); Assert.False(vm.IsReadingCreationContents);
    }
    [Fact] public async Task A_folder_appearing_during_preview_invalidates_the_old_creation_plan()
    {
        var reader = new ContentsReader { Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([new(@"C:\out\项目", true, null, null)]) };
        var vm = BulkModel(reader); AddNames(vm, "项目/资料");
        var pending = new TaskCompletionSource<NamingPlan>(); service.Pending = pending.Task;
        var building = vm.BuildPlanCommand.ExecuteAsync(null); var request = service.Request!;
        await vm.ReloadCreationContentsCommand.ExecuteAsync(null);
        pending.SetResult(NamingPlanner.Create(request, _ => false, null)); await building;
        Assert.True(vm.Directories.Single().IsExisting); Assert.Equal(1, vm.PendingCreationCount);
        Assert.Null(vm.Plan); Assert.False(vm.CanRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clearing_a_creation_input_block_by_navigation_or_mode_change_resumes_preview(bool numbered)
    {
        var vm = BulkModel(); AddNames(vm, "甲\n乙"); vm.BrowseCreationDirectory(vm.Directories[0]);
        if (numbered) vm.CreateMethod = 0; else vm.CreationNames = "稍后继续填写";
        Assert.False(vm.CanBuild); var requested = false;
        vm.PlanInvalidated += (_, _) => requested = true;
        if (numbered) vm.CreateMethod = 3; else vm.BrowseCreationDirectory(vm.Directories[1]);
        Assert.True(vm.CanBuild); Assert.True(requested); Assert.Equal(2, vm.PendingCreationCount);
        if (!numbered) { vm.BrowseCreationDirectory(vm.Directories[0]); Assert.Equal("稍后继续填写", vm.CreationNames); }
    }
    private sealed class CapturingInteraction : IFileInteraction
    {
        public string? Opened, Revealed, Copied;
        public void Open(SearchFile file) => Opened = file.FullPath;
        public void Reveal(SearchFile file) => Revealed = file.FullPath;
        public void CopyText(string text) => Copied = text;
    }
    private sealed class ContentsReader : IDirectoryContentReader
    {
        public Func<string, CancellationToken, Task<IReadOnlyList<SearchFile>>> Read = (_, _) => Task.FromResult<IReadOnlyList<SearchFile>>([]);
        public Task<IReadOnlyList<SearchFile>> ReadAsync(string path, CancellationToken token) => Read(path, token);
    }
    private sealed class FakeNaming : INamingService
    {
        public NamingRequest? Request; public NamingPlan? Executed; public Task<NamingPlan>? Pending;
        public bool UsePlanner; public string? Restored; public IReadOnlyList<NamingRecovery> History = [];
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public static NamingPlan Make(NamingRequest request) => new(request, [new(Guid.NewGuid(), null, @"C:\out\new.txt", false)]);
        public Task<NamingPlan> PlanAsync(NamingRequest request, CancellationToken token)
        {
            Request = request;
            return Pending ?? Task.FromResult(!UsePlanner ? Make(request) : request.Mode == NamingMode.Create ? NamingPlanner.Create(request, p => Existing.Contains(p) || ExistingDirectories.Contains(p), null, ExistingDirectories.Contains)
                : NamingPlanner.Rename(request, request.Paths.Select(x => new NamingSource(x, false, null)).ToArray(), _ => false));
        }
        public Task<NamingReport> ExecuteAsync(NamingPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
        {
            Executed = plan;
            if (plan.Request.Mode == NamingMode.Create) foreach (var item in plan.Items.Where(x => x.IsDirectory && x.CanRun)) ExistingDirectories.Add(item.Target);
            return Task.FromResult(new NamingReport(plan, plan.Items.Select(x => new NamingItemResult(x, x.Action == NamingAction.Existing ? NamingOutcome.Skipped : NamingOutcome.Success, "成功", x.Source is null ? x.DisplayPath : null)).ToArray(), [new(@"C:\source\a.txt", @"C:\source\new.txt", false)], [], false));
        }
        public Task<NamingPlan> RetryAsync(NamingReport report, CancellationToken token) => Task.FromResult(report.Plan);
        public Task<IReadOnlyList<NamingRecovery>> RecoveriesAsync(CancellationToken token) => Task.FromResult(History);
        public Task<NamingRestoreResult> RestoreAsync(string id, CancellationToken token) { Restored = id; return Task.FromResult(new NamingRestoreResult([], [], "已恢复")); }
    }
    private sealed class Interaction : IFileInteraction
    { public void Open(SearchFile file) { } public void Reveal(SearchFile file) { } public void CopyText(string text) { } }
}
