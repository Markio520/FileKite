using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.FileCollections;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Controls;

public sealed class StagingQuickImportEventArgs(FileCollectionBundle bundle, string sourceLabel) : EventArgs
{
    public FileCollectionBundle Bundle { get; } = bundle;
    public string SourceLabel { get; } = sourceLabel;
}

/// <summary>
/// Scheme B toolbar split button for importing from Staging:
/// main click opens the wide two-column picker; chevron opens rich 1-click direct imports into the current folder.
/// </summary>
public sealed class StagingImportButton : UserControl
{
    public static readonly DependencyProperty TargetViewModelProperty = DependencyProperty.Register(
        nameof(TargetViewModel),
        typeof(WorkspaceViewModel),
        typeof(StagingImportButton),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TargetLabelProperty = DependencyProperty.Register(
        nameof(TargetLabel),
        typeof(string),
        typeof(StagingImportButton),
        new PropertyMetadata("当前列表"));

    public WorkspaceViewModel? TargetViewModel
    {
        get => (WorkspaceViewModel?)GetValue(TargetViewModelProperty);
        set => SetValue(TargetViewModelProperty, value);
    }

    public string TargetLabel
    {
        get => (string)GetValue(TargetLabelProperty);
        set => SetValue(TargetLabelProperty, value);
    }

    private readonly Button mainButton;
    private readonly Button chevronButton;
    private readonly Flyout quickFlyout;
    private readonly StackPanel menuStack;

    public event RoutedEventHandler? PickerRequested;
    public event EventHandler<StagingQuickImportEventArgs>? QuickImportRequested;

    public StagingImportButton()
    {
        VerticalAlignment = VerticalAlignment.Center;
        UseLayoutRounding = true;

        var segmentStyle = (Style)Application.Current.Resources["WorkspaceSplitSegmentButtonStyle"];
        mainButton = new Button
        {
            Style = segmentStyle,
            CornerRadius = new CornerRadius(5, 0, 0, 5),
            Padding = new Thickness(12, 0, 10, 0),
            Height = 30
        };
        WorkspaceLocalization.SetContent(mainButton, "从暂存区导入");
        WorkspaceLocalization.SetName(mainButton, "从暂存区导入");
        WorkspaceLocalization.SetTip(mainButton, "打开宽屏挑选器精细勾选或更改导入位置");
        mainButton.Click += (_, args) => PickerRequested?.Invoke(this, args);

        menuStack = new StackPanel { Spacing = 2, Width = 284 };
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 296d));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 320d));

        quickFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = presenterStyle,
            Content = menuStack
        };
        quickFlyout.Opening += QuickFlyout_Opening;

        chevronButton = new Button
        {
            Style = segmentStyle,
            CornerRadius = new CornerRadius(0, 5, 5, 0),
            Padding = new Thickness(0),
            Width = 28,
            Height = 30,
            Content = new FontIcon { Glyph = "\uE70D", FontSize = 9 },
            Flyout = quickFlyout
        };
        WorkspaceLocalization.SetName(chevronButton, "一键快捷从暂存区直导");
        WorkspaceLocalization.SetTip(chevronButton, "快捷导入整个分组或全部暂存文件");

        var divider = new Border
        {
            Width = 1,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 5, 0, 5)
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(mainButton, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(chevronButton, 2);
        layout.Children.Add(mainButton);
        layout.Children.Add(divider);
        layout.Children.Add(chevronButton);

        var shell = new Border
        {
            Style = (Style)Application.Current.Resources["WorkspaceSplitButtonShellStyle"],
            Child = layout
        };
        divider.SetBinding(Border.BackgroundProperty, new Binding
        {
            Source = shell,
            Path = new PropertyPath(nameof(Border.BorderBrush))
        });

        Content = shell;
    }

    private void QuickFlyout_Opening(object? sender, object e)
    {
        menuStack.Children.Clear();
        var captionStyle = (Style)Application.Current.Resources["WorkspaceCaptionStyle"];
        var itemStyle = (Style)Application.Current.Resources["WorkspaceQuickMenuItemStyle"];
        var pillStyle = (Style)Application.Current.Resources["WorkspacePillBadgeStyle"];
        var folderBrush = Application.Current.Resources.TryGetValue("WorkspaceCategoryFolderBrush", out var fb) ? fb as Brush : null;

        var workspace = App.Workspace;
        if (!workspace.HasFiles || workspace.HasSamples)
        {
            menuStack.Children.Add(new TextBlock
            {
                Text = WorkspaceLocalization.Translate(workspace.HasSamples ? "暂存区当前为示例文件，请先添加真实文件" : "暂存区暂无可导入文件"),
                Style = captionStyle,
                Padding = new Thickness(10, 8, 10, 8),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            var allBtn = BuildMenuRow(
                itemStyle,
                pillStyle,
                captionStyle,
                glyph: "\uE734",
                iconBrush: null,
                title: WorkspaceLocalization.Translate("暂存区全部文件"),
                subtitle: WorkspaceLocalization.Translate("含全部软件文件夹，自动去重"),
                badgeText: WorkspaceLocalization.Translate($"{workspace.TotalReferenceCount:N0} 项"));
            allBtn.Click += (_, _) =>
            {
                quickFlyout.Hide();
                RaiseQuickImport(Guid.Empty, WorkspaceLocalization.Translate("暂存区全部文件"));
            };
            menuStack.Children.Add(allBtn);

            var topFolders = workspace.FolderNavigation
                .Where(folder => folder.Id != Guid.Empty && folder.ParentId == Guid.Empty)
                .ToArray();
            foreach (var folder in topFolders)
            {
                var folderId = folder.Id;
                var folderName = folder.Name;
                var folderBtn = BuildMenuRow(
                    itemStyle,
                    pillStyle,
                    captionStyle,
                    glyph: "\uE8B7",
                    iconBrush: folderBrush,
                    title: folderName,
                    subtitle: WorkspaceLocalization.Translate("保留分组结构 · 自动去重"),
                    badgeText: WorkspaceLocalization.Translate($"{folder.Count:N0} 项"));
                folderBtn.IsEnabled = folder.Count > 0 || workspace.HasFolder(folderId);
                folderBtn.Click += (_, _) =>
                {
                    quickFlyout.Hide();
                    RaiseQuickImport(folderId, folderName);
                };
                menuStack.Children.Add(folderBtn);
            }
        }

        menuStack.Children.Add(new MenuFlyoutSeparator { Margin = new Thickness(6, 4, 6, 4) });

        var openPickerBtn = BuildMenuRow(
            itemStyle,
            pillStyle,
            captionStyle,
            glyph: "\uE762",
            iconBrush: null,
            title: WorkspaceLocalization.Translate("挑选部分文件 / 更改导入位置…"),
            subtitle: WorkspaceLocalization.Translate("打开宽屏双栏挑选器"),
            badgeText: null);
        openPickerBtn.Click += (_, _) =>
        {
            quickFlyout.Hide();
            DispatcherQueue.TryEnqueue(() => PickerRequested?.Invoke(this, new RoutedEventArgs()));
        };
        menuStack.Children.Add(openPickerBtn);
    }

    private static Button BuildMenuRow(
        Style itemStyle,
        Style pillStyle,
        Style captionStyle,
        string glyph,
        Brush? iconBrush,
        string title,
        string subtitle,
        string? badgeText)
    {
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (iconBrush is not null) icon.Foreground = iconBrush;

        var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = captionStyle,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textStack, 1);
        row.Children.Add(icon);
        row.Children.Add(textStack);

        if (!string.IsNullOrEmpty(badgeText))
        {
            var badge = new Border
            {
                Style = pillStyle,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    Style = captionStyle,
                    FontSize = 11
                }
            };
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
        }

        return new Button
        {
            Style = itemStyle,
            Content = row
        };
    }

    private void RaiseQuickImport(Guid folderId, string label)
    {
        var workspace = App.Workspace;
        if (!workspace.HasFiles || workspace.HasSamples) return;
        if (folderId != Guid.Empty && !workspace.HasFolder(folderId)) return;
        var bundle = workspace.ExportFolder(folderId);
        if (!bundle.HasItems) return;
        QuickImportRequested?.Invoke(this, new StagingQuickImportEventArgs(bundle, label));
    }
}
