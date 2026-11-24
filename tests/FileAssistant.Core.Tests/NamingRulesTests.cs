using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Core.Tests;

public class NamingRulesTests
{
    [Fact] public void Custom_names_preserve_extensions_and_do_not_apply_hidden_rules()
    {
        var sources = new[] { new NamingSource(@"C:\data\a.TXT", false, null), new NamingSource(@"C:\data\folder", true, null), new NamingSource(@"C:\data\keep.jpg", false, null) };
        var request = new NamingRequest(NamingMode.Rename, [], new(Prefix: "hidden_"), CustomNames: new Dictionary<string, string>
            { [@"c:\DATA\a.TXT"] = "项目{n}", [sources[1].Path] = "目录.v2" });
        var plan = NamingPlanner.Rename(request, sources, _ => false);
        Assert.Equal("项目{n}.TXT", plan.Items[0].NewName);
        Assert.Equal("目录.v2", plan.Items[1].NewName);
        Assert.Equal(NamingAction.Skipped, plan.Items[2].Action);
    }
    [Theory]
    [InlineData("")][InlineData("../outside")][InlineData("CON")][InlineData("name.")]
    public void Custom_invalid_stems_are_blocked_before_appending_extension(string name)
    {
        var source = new NamingSource(@"C:\data\a.txt", false, null);
        var plan = NamingPlanner.Rename(new(NamingMode.Rename, [], new(), CustomNames: new Dictionary<string, string> { [source.Path] = name }), [source], _ => false);
        Assert.Equal(1, plan.InvalidCount); Assert.Equal(source.Path, plan.Items[0].Target);
    }
    [Fact] public void Custom_duplicate_targets_use_existing_conflict_resolution()
    {
        var sources = new[] { new NamingSource(@"C:\data\a.txt", false, null), new NamingSource(@"C:\data\b.txt", false, null) };
        var plan = NamingPlanner.Rename(new(NamingMode.Rename, [], new(), CustomNames: sources.ToDictionary(x => x.Path, _ => "same")), sources, _ => false);
        Assert.Equal(1, plan.InvalidCount);
        Assert.Contains(plan.Items, x => x.Message == "目标名称已存在或本批次重名。");
    }
    [Theory]
    [InlineData("CON.txt")][InlineData("COM1")][InlineData("LPT¹.log")][InlineData("..")][InlineData("name.")][InlineData("name ")][InlineData("a/b")][InlineData("a:b")][InlineData("")]
    public void Invalid_windows_names_are_rejected(string name) => Assert.Throws<ArgumentException>(() => NamingRulesEngine.ValidateName(name));
    [Fact] public void Rules_transform_name_before_template_and_preserve_source_braces()
    {
        var result = NamingRulesEngine.Generate("old😀-x{name}", "客户A", new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), 2,
            new("{parent}_{date}_{name}_{n}", Prefix: "P_", Find: "old", Replacement: "new", Delete: "-x", RemoveAt: 4, RemoveCount: 1, Start: 5, Step: 2, Digits: 3));
        Assert.Equal("P_客户A_20261104_new{name}_009", result);
    }
    [Fact] public void Removing_a_conflicting_source_rechecks_targets_that_depend_on_it()
    {
        var sources = new[] { new NamingSource(@"C:\data\a.txt", false, new FileStamp(1, DateTime.UtcNow.Ticks, "a")), new NamingSource(@"C:\data\b.txt", false, new FileStamp(1, DateTime.UtcNow.Ticks, "b")) };
        var plan = NamingPlanner.Rename(new(NamingMode.Rename, [], new("b")), sources, p => sources.Any(x => x.Path.Equals(p, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(NamingAction.Invalid, plan.Items[0].Action); Assert.Equal(NamingAction.Skipped, plan.Items[1].Action);
    }
    [Fact] public void Invalid_child_path_does_not_partially_create_root_plan()
    {
        var plan = NamingPlanner.Create(new(NamingMode.Create, [], new("客户"), Destination: @"C:\out", Subdirectories: "素材\n../outside"), _ => false, null);
        Assert.Equal(0, plan.ReadyCount); Assert.Equal(1, plan.InvalidCount);
    }
}
