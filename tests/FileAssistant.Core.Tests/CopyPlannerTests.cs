using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Core.Tests;

public sealed class CopyPlannerTests
{
    private static CopySource Source(string path, string identity) => new(path, "", "", false, new(10, 1, identity));
    [Fact]
    public void Batch_conflicts_require_a_decision_and_cannot_overwrite_another_batch_item()
    {
        var plan = CopyPlanner.Create(new([@"C:\a\one.txt", @"C:\b\one.txt"], @"D:\out", CopyLayout.Flat, CopyConflictPolicy.Confirm),
            [Source(@"C:\a\one.txt", "a"), Source(@"C:\b\one.txt", "b")], _ => null);
        var pending = Assert.Single(plan.Items, x => x.NeedsDecision);
        Assert.True(pending.BatchConflict); Assert.False(pending.CanOverwrite);
        Assert.Throws<InvalidOperationException>(() => CopyPlanner.Decide(plan, pending.Id, CopyDecision.Overwrite, _ => null));
        var decided = CopyPlanner.Decide(plan, pending.Id, CopyDecision.Number, _ => null);
        Assert.Equal(0, decided.PendingCount);
        Assert.Equal(@"D:\out\one (1).txt", decided.Items[1].Target);
    }
    [Fact]
    public void A_target_matching_another_input_is_protected_even_when_overwrite_is_selected()
    {
        var a = Source(@"C:\a\one.txt", "a"); var b = Source(@"D:\out\one.txt", "b");
        var plan = CopyPlanner.Create(new([a.Path, b.Path], @"D:\out", CopyLayout.Flat, CopyConflictPolicy.Confirm), [a, b],
            p => p == b.Path ? new(false, b.Stamp) : null);
        Assert.Equal(CopyAction.Rejected, plan.Items[0].Action); Assert.Equal(CopyAction.Skip, plan.Items[1].Action);
    }
    [Fact]
    public void Unreadable_sources_keep_the_expected_target_for_retry()
    {
        var source = Source(@"C:\a\one.txt", "a") with { Error = "missing" };
        var plan = CopyPlanner.Create(new([source.Path], @"D:\out", CopyLayout.Extension), [source], _ => null);
        Assert.Equal(@"D:\out\TXT\one.txt", Assert.Single(plan.Items).Target);
        Assert.Equal(CopyAction.Rejected, plan.Items[0].Action);
    }
    [Fact]
    public void File_blocking_a_parent_directory_is_rejected()
    {
        var source = Source(@"C:\a\one.txt", "a");
        var plan = CopyPlanner.Create(new([source.Path], @"D:\out", CopyLayout.Extension), [source],
            p => p == @"D:\out\TXT" ? new(false, new(1, 1, "blocker")) : null);
        Assert.Equal(CopyAction.Rejected, Assert.Single(plan.Items).Action);
    }
    [Theory]
    [InlineData(@"D:\out\a", @"D:\out", true)]
    [InlineData(@"D:\outside", @"D:\out", false)]
    [InlineData(@"D:\OUT\a", @"D:\out", true)]
    [InlineData(@"D:\out\a", @"D:\", true)]
    [InlineData(@"D:\", @"D:\", true)]
    [InlineData(@"E:\out", @"D:\", false)]
    [InlineData(@"\\server\share\out", @"\\server\share\", true)]
    [InlineData(@"\\server\share-other\out", @"\\server\share\", false)]
    public void Directory_containment_is_segment_based(string path, string root, bool within) => Assert.Equal(within, CopyPlanner.Within(path, root));
    [Fact]
    public void Auto_numbering_rejects_overlong_names_instead_of_searching_forever()
    {
        var source = Source(@"C:\a\" + new string('a', 250) + ".txt", "a");
        var plan = CopyPlanner.Create(new([source.Path], @"D:\out", CopyLayout.Flat), [source],
            path => path == Path.Combine(@"D:\out", Path.GetFileName(source.Path)) ? new(false, new(1, 1, "old")) : null);
        Assert.Equal(CopyAction.Rejected, Assert.Single(plan.Items).Action);
    }

    [Fact]
    public void Gather_layout_filters_formats_and_prefixes_colliding_subfolder_names()
    {
        var east = Source(@"C:\2026项目\华东区\月度汇总.xlsx", "e1");
        var south = Source(@"C:\2026项目\华南区\月度汇总.xlsx", "s1");
        var unique = Source(@"C:\2026项目\总部\年度预算.pdf", "u1");
        var photo = Source(@"C:\2026项目\现场\照片.jpg", "p1");

        var request = new CopyRequest([east.Path, south.Path, unique.Path, photo.Path], @"D:\out", CopyLayout.Flat)
        {
            GatherFilter = GatherFilter.DocumentsAndSheets,
            GatherPrefixSourceFolder = true
        };
        var plan = CopyPlanner.Create(request, [east, south, unique, photo], _ => null);

        Assert.Equal(@"D:\out\华东区_月度汇总.xlsx", plan.Items[0].Target);
        Assert.Equal("撞名已加来源目录名", plan.Items[0].Note);
        Assert.Equal(@"D:\out\华南区_月度汇总.xlsx", plan.Items[1].Target);
        Assert.Equal(@"D:\out\年度预算.pdf", plan.Items[2].Target);
        Assert.Null(plan.Items[2].Note);
        Assert.Equal(CopyAction.Skip, plan.Items[3].Action);
    }

    [Fact]
    public void Classify_layout_supports_two_layers_and_prefers_media_capture_date()
    {
        var saveTicks = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;
        var takenTicks = new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Local).ToUniversalTime().Ticks;

        var wechatPhoto = new CopySource(@"C:\wechat\mmexport.jpg", "", "", false, new(100, saveTicks, "img1"))
        {
            MediaTakenUtcTicks = takenTicks
        };
        var doc = new CopySource(@"C:\docs\方案.docx", "", "", false, new(200, saveTicks, "doc1"));

        var request = new CopyRequest([wechatPhoto.Path, doc.Path], @"D:\archive", CopyLayout.Classify)
        {
            ClassifyBy = ClassifyPrimary.FileType,
            SubDateLayer = ClassifyDateGranularity.YearMonth,
            PreferMediaDate = true
        };
        var plan = CopyPlanner.Create(request, [wechatPhoto, doc], _ => null);

        Assert.Equal(@"D:\archive\图片\2026-08\mmexport.jpg", plan.Items[0].Target);
        Assert.Equal("按拍摄期 2026-08", plan.Items[0].Note);
        Assert.Equal(@"D:\archive\文档\2026-11\方案.docx", plan.Items[1].Target);
    }

    [Fact]
    public void Classify_name_match_routes_unique_matches_and_safely_skips_unmatched_or_ambiguous_files()
    {
        var matched = Source(@"C:\hr\张三-身份证.jpg", "f1");
        var unmatched = Source(@"C:\hr\王五-简历.pdf", "f2");
        var ambiguous = Source(@"C:\hr\华东-上海-周报.xlsx", "f3");

        var existingRequest = new CopyRequest([matched.Path, unmatched.Path, ambiguous.Path], @"D:\hr-out", CopyLayout.Classify)
        {
            ClassifyBy = ClassifyPrimary.NameMatch,
            NameMatchRule = NameMatchRule.ExistingFolderFirst
        };
        var existingPlan = CopyPlanner.Create(existingRequest, [matched, unmatched, ambiguous], _ => null,
            existingTargetFolders: ["张三", "李四", "华东", "华东-上海"]);

        Assert.Equal(@"D:\hr-out\张三\张三-身份证.jpg", existingPlan.Items[0].Target);
        Assert.Equal("匹配已有同名夹", existingPlan.Items[0].Note);
        Assert.Equal(CopyAction.Skip, existingPlan.Items[1].Action);
        Assert.Equal(CopyAction.Skip, existingPlan.Items[2].Action);

        var prefixRequest = existingRequest with { NameMatchRule = NameMatchRule.PrefixAlways };
        var prefixPlan = CopyPlanner.Create(prefixRequest, [matched, Source(@"C:\hr\无分隔符文件.pdf", "f4")], _ => null);
        Assert.Equal(@"D:\hr-out\张三\张三-身份证.jpg", prefixPlan.Items[0].Target);
        Assert.Equal(@"D:\hr-out\未带前缀文件\无分隔符文件.pdf", prefixPlan.Items[1].Target);
    }

    [Fact]
    public void Skeleton_layout_replaces_keywords_ignores_files_and_forbids_move()
    {
        var rootDir = new CopySource(@"C:\2026-10项目", @"C:\2026-10项目", "", true, null);
        var subDir = new CopySource(@"C:\2026-10项目\2026-10月报\华东区", @"C:\2026-10项目", @"2026-10月报\华东区", true, null);
        var innerFile = Source(@"C:\2026-10项目\2026-10月报\华东区\a.xlsx", "x1");

        var request = new CopyRequest([@"C:\2026-10项目"], @"D:\templates", CopyLayout.Skeleton)
        {
            ReplaceSkeletonText = true,
            SkeletonFind = "2026-10",
            SkeletonReplace = "2026-11"
        };
        var plan = CopyPlanner.Create(request, [rootDir, subDir, innerFile], _ => null);

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(@"D:\templates\2026-11项目", plan.Items[0].Target);
        Assert.Equal(@"D:\templates\2026-11项目\2026-11月报\华东区", plan.Items[1].Target);
        Assert.Equal("已替换目录关键词", plan.Items[1].Note);

        var badReplace = request with { SkeletonReplace = "2026:11" };
        var rejectedPlan = CopyPlanner.Create(badReplace, [rootDir], _ => null);
        Assert.Equal(CopyAction.Rejected, Assert.Single(rejectedPlan.Items).Action);

        Assert.Throws<ArgumentException>(() =>
            CopyPlanner.Create(request with { Operation = CopyOperation.Move }, [rootDir], _ => null));
    }
}
