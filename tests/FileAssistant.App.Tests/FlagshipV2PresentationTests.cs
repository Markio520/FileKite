using FileAssistant.App.Features.Workspace;

namespace FileAssistant.App.Tests;

public sealed class FlagshipV2PresentationTests
{
    [Theory]
    [InlineData("→ 01.png", "")]
    [InlineData("→ 01 (1).png", "")]
    [InlineData("→ 3.mp4", "需调整")]
    [InlineData("无法转换", "不支持")]
    public void Preview_preserves_the_whole_target_and_separate_status(string target, string status)
    {
        var preview = new FileNamePreview();
        preview.ApplyPreview(target, status, "详情");
        Assert.Equal(target, preview.Text);
        Assert.True(preview.IsVisible);
        Assert.Equal(status, preview.Status);
        Assert.Equal("详情", preview.Details);
    }

    [Fact]
    public void Location_badge_compacts_deep_directories_without_losing_drive_and_leaf_folder()
    {
        Assert.Equal("D: / … / 产品主图", WorkspaceFile.FormatLocationBadge(@"D:\2026项目\视觉资产\产品主图"));
        Assert.Equal("D: / 素材", WorkspaceFile.FormatLocationBadge(@"D:\素材"));
    }
}
