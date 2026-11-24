using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.Core.Tests;

public sealed class DirectoryTreeTests
{
    private static NamingRequest Request(params string[] paths) => new(NamingMode.Create, [], new(),
        Destination: @"C:\out", DirectoryPaths: paths);
    [Fact] public void Mixed_tree_preserves_templates_and_numbers_file_conflicts_without_changing_extensions()
    {
        var request = Request() with { Entries = [new(@"甲\合同.doc", false, @"C:\templates\blank.doc"), new(@"甲\说明.txt", false), new(@"乙\素材")], ReuseExistingDirectories = true, Conflict = NamingConflict.Number };
        var plan = NamingPlanner.Create(request, p => p is @"C:\out\甲" or @"C:\out\甲\合同.doc", null, p => p == @"C:\out\甲");
        Assert.Equal(4, plan.ReadyCount); Assert.Equal(0, plan.InvalidCount);
        var word = plan.Items.Single(x => x.TemplatePath is not null);
        Assert.Equal(NamingAction.Numbered, word.Action); Assert.EndsWith(".doc", word.Target);
        Assert.Equal(@"甲\合同.doc", word.RequestedRelativePath); Assert.False(word.IsDirectory);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void A_file_cannot_be_used_as_an_implicit_directory_in_either_input_order(bool fileFirst)
    {
        CreationEntry[] entries = [new("甲", false), new(@"甲\子目录")];
        var plan = NamingPlanner.Create(Request() with { Entries = fileFirst ? entries : entries.Reverse().ToArray() }, _ => false, null);
        Assert.True(plan.InvalidCount > 0);
    }
    [Fact] public void Missing_ancestors_are_planned_before_descendants()
    {
        var plan = NamingPlanner.Create(Request(@"甲\素材\原稿", @"乙\交付"), _ => false, null);
        Assert.Equal(5, plan.ReadyCount);
        foreach (var item in plan.Items)
        {
            var parent = Path.GetDirectoryName(item.Target);
            Assert.True(parent == @"C:\out" || plan.Items.TakeWhile(x => x != item).Any(x => x.Target == parent));
        }
    }
    [Theory]
    [InlineData(NamingConflict.Block, NamingAction.Invalid)]
    [InlineData(NamingConflict.Skip, NamingAction.Skipped)]
    public void Occupied_parent_blocks_or_skips_its_entire_subtree(NamingConflict conflict, NamingAction expected)
    {
        var plan = NamingPlanner.Create(Request("甲", @"甲\素材") with { Conflict = conflict }, p => p == @"C:\out\甲", null);
        Assert.All(plan.Items, x => Assert.Equal(expected, x.Action)); Assert.Equal(0, plan.ReadyCount);
    }
    [Fact] public void Numbered_parent_remaps_only_its_own_descendants()
    {
        var plan = NamingPlanner.Create(Request("甲", @"甲\素材", "乙", @"乙\交付") with { Conflict = NamingConflict.Number }, p => p == @"C:\out\甲", null);
        var parent = plan.Items.Single(x => x.Action == NamingAction.Numbered);
        Assert.Contains(plan.Items, x => x.Target == Path.Combine(parent.Target, "素材"));
        Assert.Contains(plan.Items, x => x.Target == @"C:\out\乙\交付");
        Assert.DoesNotContain(plan.Items, x => x.Target == @"C:\out\甲\素材");
    }
    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"C:\escape")]
    [InlineData(@"safe\\bad")]
    [InlineData("CON")]
    public void Invalid_paths_cannot_escape_the_destination(string path)
    {
        var plan = NamingPlanner.Create(Request(path), _ => false, null);
        Assert.Equal(1, plan.InvalidCount); Assert.Equal(0, plan.ReadyCount);
    }
    [Fact] public void Duplicate_names_are_not_silently_merged()
    {
        var plan = NamingPlanner.Create(Request("素材", "素材"), _ => false, null);
        Assert.True(plan.InvalidCount > 0);
    }
    [Fact] public void Shared_tree_can_reuse_existing_directories_and_only_create_missing_descendants()
    {
        var request = Request(@"甲\素材\原片", @"甲\交付", @"乙\合同") with { ReuseExistingDirectories = true };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\out\甲", @"C:\out\甲\素材" };
        var plan = NamingPlanner.Create(request, existing.Contains, null, existing.Contains);
        Assert.Equal(4, plan.ReadyCount); Assert.Equal(0, plan.InvalidCount);
        Assert.Equal(2, plan.Items.Count(x => x.Action == NamingAction.Existing));
        Assert.Equal(@"甲\素材\原片", plan.Items.Single(x => x.Target.EndsWith("原片")).RequestedRelativePath);
    }
    [Fact] public void A_file_at_an_ancestor_path_is_never_treated_as_a_reusable_directory()
    {
        var plan = NamingPlanner.Create(Request(@"甲\素材") with { ReuseExistingDirectories = true }, p => p == @"C:\out\甲", null, _ => false);
        Assert.Equal(2, plan.InvalidCount); Assert.Equal(0, plan.ReadyCount);
    }
    [Fact] public void Numbering_a_file_conflict_keeps_original_identity_for_navigation()
    {
        var plan = NamingPlanner.Create(Request(@"甲\素材") with { ReuseExistingDirectories = true, Conflict = NamingConflict.Number }, p => p == @"C:\out\甲", null, _ => false);
        Assert.Equal("甲", plan.Items[0].RequestedRelativePath);
        Assert.Equal(@"甲\素材", plan.Items[1].RequestedRelativePath);
        Assert.StartsWith(plan.Items[0].Target + "\\", plan.Items[1].Target);
    }
}
