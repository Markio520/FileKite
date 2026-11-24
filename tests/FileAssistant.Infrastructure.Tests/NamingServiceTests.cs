using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Tests;

public sealed class NamingServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FileAssistant-Naming-" + Guid.NewGuid().ToString("N"));
    private NamingService Service => new(Path.Combine(root, "journal"));

    [Fact] public async Task Custom_names_execute_swap_and_restore_without_touching_unedited_file()
    {
        var a = FileAt("A.txt", "A"); var b = FileAt("B.txt", "B"); var keep = FileAt("keep.txt", "keep");
        var plan = await Service.PlanAsync(new(NamingMode.Rename, [a, b, keep], new(),
            CustomNames: new Dictionary<string, string> { [a] = "B", [b] = "A" }), default);
        Assert.Equal(2, plan.ReadyCount); Assert.Equal(NamingAction.Skipped, plan.Items.Single(x => x.Source!.Path == keep).Action);
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 2);
        Assert.Equal("B", File.ReadAllText(a)); Assert.Equal("A", File.ReadAllText(b)); Assert.Equal("keep", File.ReadAllText(keep));
        var restored = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal("本次操作已撤销。", restored.Message); Assert.Equal("A", File.ReadAllText(a)); Assert.Equal("B", File.ReadAllText(b));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task Invalid_step_lists_do_not_block_other_recovery_records(string steps)
    {
        var source = FileAt("recover.txt");
        var report = await Service.ExecuteAsync(await Rename([source]), null, default);
        var id = Guid.NewGuid().ToString("N");
        FileAt("journal/" + id + ".json", "{\"Version\":1,\"Id\":\"" + id + "\",\"Remaining\":1,\"Steps\":" + steps + "}");
        var records = await Service.RecoveriesAsync(default);
        Assert.Contains(records, record => record.Id == report.RecoveryId);
        Assert.Contains(records, record => record.Id == id && record.Remaining == 0 && record.Error != null);
        Assert.Equal("本次操作已撤销。", (await Service.RestoreAsync(report.RecoveryId!, default)).Message);
    }
    private string Output => Path.Combine(root, "out");
    public NamingServiceTests() { Directory.CreateDirectory(Output); }
    private string FileAt(string name, string text = "sample")
    { var path = Path.GetFullPath(Path.Combine(root, name)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
    private Task<NamingPlan> Rename(string[] paths, NamingRules? rules = null, bool contents = false, bool recursive = false)
        => Service.PlanAsync(new(NamingMode.Rename, paths, rules ?? new(Prefix: "新_"), FolderContents: contents, IncludeSubfolders: recursive), default);
    private static void Success(NamingReport report, int count)
    { Assert.Equal(count, report.Items.Count(x => x.Outcome == NamingOutcome.Success)); Assert.DoesNotContain(report.Items, x => x.Retryable); }

    [Fact] public async Task Rename_and_cross_restart_undo_preserve_contents()
    {
        var source = FileAt("照片.jpg", "not really an image"); var plan = await Rename([source]);
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 1);
        Assert.False(File.Exists(source)); Assert.Equal("not really an image", File.ReadAllText(plan.Items[0].Target));
        var restore = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal("本次操作已撤销。", restore.Message); Assert.True(File.Exists(source));
        Assert.Empty(await Service.RecoveriesAsync(default));
    }
    [Fact] public async Task Swaps_use_temporary_names_and_restore_in_reverse_order()
    {
        var a = FileAt("A.txt", "A"); var b = FileAt("B.txt", "B");
        File.SetCreationTimeUtc(a, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(b, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var plan = await Rename([a, b]);
        plan = plan with { Items = [plan.Items[0] with { Target = b }, plan.Items[1] with { Target = a }] };
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 2);
        Assert.Equal("B", File.ReadAllText(a)); Assert.Equal("A", File.ReadAllText(b));
        Assert.Equal(b, NamingRulesEngine.MapPath(a, report.Changes));
        var restored = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal("本次操作已撤销。", restored.Message);
        Assert.Equal("A", File.ReadAllText(a)); Assert.Equal("B", File.ReadAllText(b));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_rejects_replacement_with_identical_contents_and_timestamps(bool create)
    {
        var source = FileAt("original.txt", "");
        var plan = create
            ? await Service.PlanAsync(new(NamingMode.Create, [], new("{name}"), Destination: Output,
                Names: "created", Kind: CreationKind.Text), default)
            : await Rename([source]);
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 1);
        var target = plan.Items[0].Target;
        var created = File.GetCreationTimeUtc(target); var modified = File.GetLastWriteTimeUtc(target);
        var retained = Path.Combine(root, "retained.txt");
        File.Move(target, retained);
        File.WriteAllText(target, "");
        File.SetCreationTimeUtc(target, created); File.SetLastWriteTimeUtc(target, modified);

        var restored = await Service.RestoreAsync(report.RecoveryId!, default);

        Assert.Contains("恢复未完成", restored.Message);
        Assert.True(File.Exists(target)); Assert.True(File.Exists(retained));
        Assert.Single(await Service.RecoveriesAsync(default));
    }
    [Fact] public async Task Case_only_rename_is_not_skipped()
    {
        var source = FileAt("abc.TXT"); var plan = await Rename([source], new(Case: NameCase.Upper));
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 1);
        Assert.Contains("ABC.TXT", Directory.GetFiles(root).Select(Path.GetFileName));
        await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Contains("abc.TXT", Directory.GetFiles(root).Select(Path.GetFileName));
    }
    [Fact] public async Task Parent_and_child_rename_produce_previewed_final_paths()
    {
        var source = FileAt("父/子/文件.txt"); var parent = Path.Combine(root, "父");
        var plan = await Rename([parent, source], contents: true, recursive: true);
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 3);
        var expected = Path.Combine(root, "新_父", "新_子", "新_文件.txt"); Assert.True(File.Exists(expected));
        Assert.Equal(expected, plan.Items.Single(x => x.Source!.Path == source).DisplayPath);
        Assert.Equal(expected, NamingRulesEngine.MapPath(source, report.Changes));
        await Service.RestoreAsync(report.RecoveryId!, default); Assert.True(File.Exists(source));
    }
    [Fact] public async Task New_conflict_rolls_back_group_and_preserves_external_file()
    {
        var source = FileAt("A.txt", "original"); var plan = await Rename([source]); File.WriteAllText(plan.Items[0].Target, "external");
        var report = await Service.ExecuteAsync(plan, null, default);
        Assert.Equal(NamingOutcome.Failed, report.Items[0].Outcome); Assert.Equal("original", File.ReadAllText(source));
        Assert.Equal("external", File.ReadAllText(plan.Items[0].Target)); Assert.Empty(Directory.GetFiles(root, ".fa-*"));
    }
    [Fact] public async Task Locked_group_does_not_leave_temporary_names_and_other_group_completes()
    {
        var a = FileAt("first/a.txt"); var b = FileAt("second/b.txt"); var plan = await Rename([a, b]);
        using (var locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = await Service.ExecuteAsync(plan, null, default);
            Assert.Equal(1, report.Items.Count(x => x.Outcome == NamingOutcome.Success));
            Assert.Equal(1, report.Items.Count(x => x.Retryable)); Assert.True(File.Exists(a));
            Assert.Empty(Directory.GetFiles(root, ".fa-*", SearchOption.AllDirectories));
        }
    }
    [Fact] public async Task Creation_builds_nested_directories_and_undo_keeps_foreign_contents()
    {
        var request = new NamingRequest(NamingMode.Create, [], new("{name}"), Destination: Output, Names: "客户A\n客户B", Subdirectories: "素材/原稿\n交付");
        var report = await Service.ExecuteAsync(await Service.PlanAsync(request, default), null, default); Success(report, 8);
        var foreign = Path.Combine(Output, "客户A", "external.txt"); File.WriteAllText(foreign, "external");
        var result = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Contains("恢复未完成", result.Message); Assert.Equal("external", File.ReadAllText(foreign));
        Assert.False(Directory.Exists(Path.Combine(Output, "客户B"))); Assert.True(Directory.Exists(Path.Combine(Output, "客户A")));
    }
    [Fact] public async Task Text_creation_and_undo_remove_only_unchanged_new_files()
    {
        var request = new NamingRequest(NamingMode.Create, [], new("文件{n}"), Destination: Output, Count: 3, Kind: CreationKind.Text);
        var report = await Service.ExecuteAsync(await Service.PlanAsync(request, default), null, default); Success(report, 3);
        Assert.Equal(3, report.Outputs.Count); File.WriteAllText(Path.Combine(Output, "文件002.txt"), "changed");
        var result = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Contains("恢复未完成", result.Message); Assert.Equal("changed", File.ReadAllText(Path.Combine(Output, "文件002.txt")));
        Assert.False(File.Exists(Path.Combine(Output, "文件003.txt"))); Assert.Empty(Directory.GetFiles(Output, ".fa-*"));
    }
    [Fact] public async Task Valid_template_is_copied_byte_for_byte_and_changed_template_is_rejected()
    {
        var template = FileAt("template.bin", "valid template content");
        var request = new NamingRequest(NamingMode.Create, [], new("文档{n}"), Destination: Output, Count: 2, Kind: CreationKind.Template, Template: template);
        var plan = await Service.PlanAsync(request, default); var report = await Service.ExecuteAsync(plan, null, default); Success(report, 2);
        Assert.All(report.Outputs, x => Assert.Equal(File.ReadAllBytes(template), File.ReadAllBytes(x.FullPath)));
        await Service.RestoreAsync(report.RecoveryId!, default); Assert.Empty(Directory.GetFiles(Output));
        plan = await Service.PlanAsync(request, default); File.WriteAllText(template, "modified");
        report = await Service.ExecuteAsync(plan, null, default); Assert.All(report.Items, x => Assert.Equal(NamingOutcome.Failed, x.Outcome));
        Assert.Empty(Directory.GetFiles(Output));
    }
    [Fact] public async Task Restore_refuses_to_overwrite_new_object_at_original_path()
    {
        var source = FileAt("source.txt"); var report = await Service.ExecuteAsync(await Rename([source]), null, default); File.WriteAllText(source, "new");
        var result = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Contains("恢复未完成", result.Message); Assert.Equal("new", File.ReadAllText(source));
    }
    [Fact] public async Task Cancellation_and_retry_do_not_repeat_successful_creations()
    {
        var request = new NamingRequest(NamingMode.Create, [], new("file{n}"), Destination: Output, Count: 4, Kind: CreationKind.Text);
        using var cancellation = new CancellationTokenSource();
        var report = await Service.ExecuteAsync(await Service.PlanAsync(request, default), new InlineProgress(p => { if (p.Completed == 1) cancellation.Cancel(); }), cancellation.Token);
        Assert.True(report.Cancelled); Assert.Single(report.Outputs);
        var plan = await Service.RetryAsync(report, default); Assert.Equal(3, plan.ReadyCount);
        var second = await Service.ExecuteAsync(plan, null, default); Success(second, 3); Assert.Equal(4, Directory.GetFiles(Output).Length);
    }
    [Fact]
    public async Task Retry_rejects_a_replaced_successfully_created_parent_directory()
    {
        var request = new NamingRequest(NamingMode.Create, [], new(), Destination: Output, DirectoryPaths: [@"parent\child"]);
        using var cancellation = new CancellationTokenSource();
        var report = await Service.ExecuteAsync(await Service.PlanAsync(request, default),
            new InlineProgress(p => { if (p.Completed == 1) cancellation.Cancel(); }), cancellation.Token);
        Assert.Single(report.Items, x => x.Outcome == NamingOutcome.Success);
        var parent = Path.Combine(Output, "parent");
        Directory.Move(parent, Path.Combine(root, "retained-parent"));
        Directory.CreateDirectory(parent);

        await Assert.ThrowsAsync<IOException>(() => Service.RetryAsync(report, default));

        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    private sealed class InlineProgress(Action<FileAssistant.Core.Engines.ProcessingProgress> action) : IProgress<FileAssistant.Core.Engines.ProcessingProgress>
    { public void Report(FileAssistant.Core.Engines.ProcessingProgress value) => action(value); }
    [Fact] public async Task Undo_created_file_keeps_its_public_reference_name()
    {
        var request = new NamingRequest(NamingMode.Create, [], new("Foo"), Destination: Output, Kind: CreationKind.Text);
        var report = await Service.ExecuteAsync(await Service.PlanAsync(request, default), null, default);
        var output = report.Outputs.Single().FullPath; var restored = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal(output, NamingRulesEngine.MapPath(output, restored.Changes)); Assert.Contains(output, restored.Removed);
        Assert.DoesNotContain(restored.Removed, x => Path.GetFileName(x).StartsWith(".fa-"));
    }
    [Fact] public async Task Cancelling_during_rename_staging_rolls_back_the_entire_directory_group()
    {
        var paths = Enumerable.Range(0, 40).Select(i => FileAt($"batch/{i:D3}.txt", i.ToString())).ToArray();
        using var cancel = new CancellationTokenSource();
        var report = await Service.ExecuteAsync(await Rename(paths), new InlineProgress(_ => cancel.Cancel()), cancel.Token);
        Assert.True(report.Cancelled); Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "batch"), ".fa-*"));
        Assert.All(paths, path => Assert.Equal(path, NamingRulesEngine.MapPath(path, report.Changes)));
    }
    [Fact] public async Task Recovery_resumes_after_move_completed_before_cursor_was_saved()
    {
        var source = FileAt("recover.txt"); var report = await Service.ExecuteAsync(await Rename([source]), null, default);
        var path = Path.Combine(root, "journal", report.RecoveryId + ".json");
        var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var last = journal["Steps"]!.AsArray().Last()!;
        journal["UndoIndex"] = journal["Steps"]!.AsArray().Count - 1;
        File.WriteAllText(path, journal.ToJsonString());
        File.Move(last["To"]!.GetValue<string>(), last["From"]!.GetValue<string>());
        var restore = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal("本次操作已撤销。", restore.Message); Assert.True(File.Exists(source));
        Assert.Equal(source, NamingRulesEngine.MapPath(report.Items[0].ActualPath!, restore.Changes));
    }
    [Fact] public async Task Two_hundred_files_have_stable_numbers_and_no_staging_leftovers()
    {
        var paths = Enumerable.Range(0, 200).Select(i => FileAt($"many/{i:D3}.txt", i.ToString())).ToArray();
        var plan = await Rename(paths, new("file{n}", Start: 10, Step: 2, Digits: 4));
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 200);
        Assert.Equal("0", File.ReadAllText(Path.Combine(root, "many", "file0010.txt")));
        Assert.Equal("199", File.ReadAllText(Path.Combine(root, "many", "file0408.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "many"), ".fa-*"));
        await Service.RestoreAsync(report.RecoveryId!, default); Assert.All(paths, path => Assert.True(File.Exists(path)));
    }
    [Fact] public async Task Edited_directory_tree_matches_execution_and_can_be_undone_after_restart()
    {
        var plan = await Service.PlanAsync(new(NamingMode.Create, [], new(), Destination: Output,
            DirectoryPaths: ["客户甲", @"客户甲\素材", @"客户甲\素材\原稿", "客户乙", @"客户乙\交付"]), default);
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 5);
        Assert.True(Directory.Exists(Path.Combine(Output, "客户甲", "素材", "原稿")));
        Assert.True(Directory.Exists(Path.Combine(Output, "客户乙", "交付")));
        Assert.False(Directory.Exists(Path.Combine(Output, "客户乙", "素材")));
        await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Empty(Directory.GetFileSystemEntries(Output));
    }
    [Fact] public async Task Different_subtrees_reuse_existing_parents_and_undo_only_new_directories()
    {
        var keep = FileAt("out/客户甲/素材/已有.txt", "keep");
        var plan = await Service.PlanAsync(new(NamingMode.Create, [], new(), Destination: Output,
            DirectoryPaths: [@"客户甲\素材\原片", @"客户甲\交付", @"客户乙\合同", @"客户乙\发票"], ReuseExistingDirectories: true), default);
        Assert.Equal(5, plan.ReadyCount);
        Assert.All(plan.Items.Where(x => x.Action == NamingAction.Existing), x => Assert.NotNull(x.ExistingStamp));
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 5);
        Assert.Equal("keep", File.ReadAllText(keep)); Assert.True(Directory.Exists(Path.Combine(Output, "客户乙", "发票")));
        var restored = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal("本次操作已撤销。", restored.Message);
        Assert.Equal("keep", File.ReadAllText(keep)); Assert.False(Directory.Exists(Path.Combine(Output, "客户乙")));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(keep)!));
    }
    [Fact] public async Task Replacing_a_reused_parent_after_preview_stops_new_children()
    {
        var parent = Directory.CreateDirectory(Path.Combine(Output, "客户甲")).FullName;
        var plan = await Service.PlanAsync(new(NamingMode.Create, [], new(), Destination: Output,
            DirectoryPaths: [@"客户甲\交付"], ReuseExistingDirectories: true), default);
        Directory.Move(parent, parent + "原件"); Directory.CreateDirectory(parent);
        var report = await Service.ExecuteAsync(plan, null, default);
        Assert.Contains(report.Items, x => x.Retryable && x.Message.Contains("父目录"));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }
    [Fact] public async Task Cancel_and_retry_under_existing_parents_keep_identity_checks_and_successful_children()
    {
        var keep = FileAt("out/客户甲/已有.txt", "keep");
        var plan = await Service.PlanAsync(new(NamingMode.Create, [], new(), Destination: Output,
            DirectoryPaths: [@"客户甲\素材", @"客户甲\交付", @"客户甲\归档"], ReuseExistingDirectories: true), default);
        using var cancel = new CancellationTokenSource();
        var first = await Service.ExecuteAsync(plan, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
        Assert.Single(first.Outputs); Assert.True(first.Cancelled);
        var retry = await Service.RetryAsync(first, default);
        Assert.Equal(2, retry.ReadyCount); Assert.Single(retry.Items, x => x.Action == NamingAction.Existing);
        var second = await Service.ExecuteAsync(retry, null, default); Success(second, 2);
        Assert.Equal(3, Directory.GetDirectories(Path.GetDirectoryName(keep)!).Length); Assert.Equal("keep", File.ReadAllText(keep));
    }
    [Fact] public async Task Mixed_nested_creation_copies_each_real_word_template_and_undo_preserves_later_file_edits()
    {
        var doc = TemplateAsset(".doc"); var docx = TemplateAsset(".docx");
        var request = new NamingRequest(NamingMode.Create, [], new(), Destination: Output, ReuseExistingDirectories: true,
            Entries: [new("1/1/2"), new("1/1/3"), new("1/2/2"), new("1/2/3"),
                new("1/1/2/说明.txt", false), new("1/1/3/合同.doc", false, doc), new("1/2/3/合同.docx", false, docx)]);
        var plan = await Service.PlanAsync(request, default); Assert.Equal(10, plan.ReadyCount);
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 10);
        Assert.Equal(File.ReadAllBytes(doc), File.ReadAllBytes(Path.Combine(Output, "1/1/3/合同.doc")));
        Assert.Equal(File.ReadAllBytes(docx), File.ReadAllBytes(Path.Combine(Output, "1/2/3/合同.docx")));
        var text = Path.Combine(Output, "1/1/2/说明.txt"); Assert.Equal(0, new FileInfo(text).Length);
        File.WriteAllText(text, "后来编辑的内容");
        var restore = await Service.RestoreAsync(report.RecoveryId!, default);
        Assert.Equal("后来编辑的内容", File.ReadAllText(text)); Assert.Contains("恢复未完成", restore.Message);
    }
    [Fact] public async Task Mixed_templates_are_checked_per_file_and_retry_retains_their_original_sources()
    {
        var first = FileAt("templates/first.xyz", "first"); var second = FileAt("templates/second.xyz", "second");
        var plan = await Service.PlanAsync(new(NamingMode.Create, [], new(), Destination: Output,
            Entries: [new("甲/a.xyz", false, first), new("甲/b.xyz", false, second)]), default);
        using var cancel = new CancellationTokenSource();
        var report = await Service.ExecuteAsync(plan, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
        Assert.Single(report.Outputs); Assert.True(report.Cancelled);
        var retry = await Service.RetryAsync(report, default);
        Assert.Equal(new[] { first, second }, retry.Items.Where(x => x.CanRun).Select(x => x.CreationTemplate!.Path));
        File.WriteAllText(second, "changed after preview");
        var result = await Service.ExecuteAsync(retry, null, default);
        Assert.Equal("first", File.ReadAllText(Path.Combine(Output, "甲/a.xyz")));
        Assert.False(File.Exists(Path.Combine(Output, "甲/b.xyz")));
        Assert.Contains(result.Items, x => x.Retryable && x.Message.Contains("模板在预览后"));
        Assert.Empty(Directory.GetFiles(Output, ".fa-*", SearchOption.AllDirectories));
    }
    [Fact] public async Task Missing_template_blocks_the_mixed_plan_before_creating_anything()
    {
        var plan = await Service.PlanAsync(new(NamingMode.Create, [], new(), Destination: Output,
            Entries: [new("目录/说明.txt", false), new("目录/合同.doc", false, Path.Combine(root, "missing.doc"))]), default);
        Assert.Equal(1, plan.InvalidCount);
        await Assert.ThrowsAsync<IOException>(() => Service.ExecuteAsync(plan, null, default));
        Assert.Empty(Directory.GetFileSystemEntries(Output));
    }
    [Fact] public void Bundled_word_templates_contain_valid_blank_document_structures()
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(TemplateAsset(".docx"));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml")); Assert.NotNull(zip.GetEntry("_rels/.rels"));
        var xml = System.Xml.Linq.XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
        System.Xml.Linq.XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Assert.Single(xml.Descendants(word + "body")); Assert.Empty(xml.Descendants(word + "t"));
        var bytes = File.ReadAllBytes(TemplateAsset(".doc"));
        Assert.Equal(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, bytes[..8]);
        Assert.Equal("5F421A3970C70F12296073478380C0849A54C03FB2F4842392C7D3C5039BF1EB", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        // Inspect the pinned CFB fixture's actual Word stream, not just its filename/signature.
        var sector = 1 << BitConverter.ToUInt16(bytes, 30);
        var fatStart = (BitConverter.ToInt32(bytes, 76) + 1) * sector;
        byte[] Stream(int id)
        {
            using var buffer = new MemoryStream(); var count = 0;
            while (id >= 0)
            { Assert.True(++count <= bytes.Length / sector); buffer.Write(bytes, (id + 1) * sector, sector); id = BitConverter.ToInt32(bytes, fatStart + id * 4); }
            return buffer.ToArray();
        }
        var directory = Stream(BitConverter.ToInt32(bytes, 48)); byte[]? document = null;
        for (var offset = 0; offset < directory.Length; offset += 128)
        {
            var length = BitConverter.ToUInt16(directory, offset + 64);
            if (length > 0 && System.Text.Encoding.Unicode.GetString(directory, offset, length - 2) == "WordDocument")
                document = Stream(BitConverter.ToInt32(directory, offset + 116));
        }
        Assert.NotNull(document); Assert.Equal(0xA5EC, BitConverter.ToUInt16(document, 0));
        Assert.Equal(1, BitConverter.ToInt32(document, 76)); // Only the mandatory paragraph marker.
        Assert.Equal(13, document[BitConverter.ToInt32(document, 24)]);
    }
    private static string TemplateAsset(string extension)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "FileAssistant.slnx"))) folder = folder.Parent;
        return Path.Combine(folder!.FullName, "src/FileAssistant.App/Assets/CreationTemplates/blank" + extension);
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("FileAssistant-Naming-", StringComparison.Ordinal))
            throw new InvalidOperationException("测试清理路径越界。");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
