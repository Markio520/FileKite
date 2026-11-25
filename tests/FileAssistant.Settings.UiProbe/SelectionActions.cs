using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static IInvokeProvider SelectionButtonAtCenter(FrameworkElement root, Button button)
    {
        var center = button.TransformToVisual(root).TransformPoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(center, root).OfType<FrameworkElement>().ToArray();
        DependencyObject? hit = hits.FirstOrDefault();
        while (hit is not null && hit is not ButtonBase) hit = VisualTreeHelper.GetParent(hit);
        Require(button.IsEnabled && ReferenceEquals(hit, button),
            $"'{button.Content}' at {center} hit {string.Join(" > ", hits.Take(12).Select(f => f.GetType().Name + "." + f.Name))}.");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement((Button)hit!);
        return peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("Button does not expose native Invoke: " + button.Content);
    }

    private static async Task CheckSelectionActions(FrameworkElement root, WorkspacePage workspace)
    {
        await Preferences.UpdateAsync(s => s with { Mode = AppearanceMode.Light, Animations = true });
        var fixture = Directory.CreateDirectory(Path.Combine(profile, "selection-fixture"));
        var paths = new[] { "pick-1.txt", "pick-2.txt", "pick-3.txt", "keep.txt" }
            .Select(name => Path.Combine(fixture.FullName, name)).ToArray();
        foreach (var path in paths) File.WriteAllText(path, Path.GetFileName(path));
        var picked = paths.Take(3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var model = Naming.Input;
        Naming.ModeValue = 0;
        model.AddSearchFiles(paths.Select(path => new SearchFile(path, false, new FileInfo(path).Length, DateTimeOffset.Now)).ToArray());
        var navigate = (ToggleButton)workspace.FindName("LeftNamingNav");
        InvokeButton(navigate);
        var panel = (FrameworkElement)workspace.FindName("NamingPanel");
        var input = (FileInputView)panel.FindName("InputView");
        var list = (ListView)input.FindName("FileList");
        var bar = (FrameworkElement)input.FindName("FloatingActionIsland");
        await Until(() => list.IsLoaded && list.Items.Count == 4, "Four rename inputs did not load.");

        void SelectThree()
        {
            foreach (var row in list.Items.OfType<WorkspaceFile>().Where(row => picked.Contains(row.FullPath)))
                if (!list.SelectedItems.Contains(row)) list.SelectedItems.Add(row);
            Require(model.SelectionCount == 3, "Expected three selected inputs.");
        }
        async Task ClickAction(string label)
        {
            await Until(() => bar.Visibility == Visibility.Visible && bar.ActualWidth > 0, "Selection bar did not open.");
            var button = Elements(bar).OfType<Button>().Single(b => Equals(b.Content, label));
            SelectionButtonAtCenter(root, button).Invoke();
            Log("Activated hit-tested button: " + label);
        }
        ContentDialog? OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault();
        async Task PreviewSelection()
        {
            await ClickAction("快速预览");
            await Until(() => OpenDialog() is not null, "Quick Look did not open.");
            var dialog = OpenDialog()!;
            try
            {
                await Until(() => Elements(dialog).OfType<TextBlock>().Any(t => t.Text == "1 / 3"),
                    "Quick Look did not contain the three selected files.", 3);
                Require(Elements(dialog).OfType<TextBlock>().Any(t => picked.Any(p => Path.GetFileName(p) == t.Text)),
                    "Quick Look displayed an unselected file.");
            }
            finally { dialog.Hide(); await Until(() => OpenDialog() is null, "Quick Look did not close."); }
        }
        async Task Transfer(string action, WorkspaceViewModel destination, string? targetPanel)
        {
            await ClickAction(action);
            await Until(() => OpenDialog() is not null, action + " did not open the destination picker.");
            var picker = Elements(OpenDialog()!).OfType<CollectionFolderPicker>().Single();
            var confirm = (Button)picker.FindName("ModalConfirmButton");
            await Until(() => confirm.IsEnabled, "The destination was not available.");
            InvokeButton(confirm);
            await Until(() => picked.SetEquals(destination.SearchEntries.Select(f => f.FullPath)), action + " sent the wrong files.");
            if (targetPanel is not null)
            {
                await Until(() => workspace.FindName(targetPanel) is FrameworkElement { Visibility: Visibility.Visible },
                    action + " did not open the target module.");
                InvokeButton(navigate);
            }
            await Settle(root);
        }

        SelectThree();
        await ResizeWindow(root, 1140, 820);
        await Task.Delay(350);
        await CheckSelectionColors(root, workspace, (ContentControl)bar);
        await SavePng(input, "selection-bar");
        await PreviewSelection();
        await Transfer("加入暂存", Workspace, null);
        await Transfer("转格式", Conversion.Input, "ConversionPanel");
        await Transfer("复制整理", CopyInput, "CopyPanel");
        await ClickAction("✕");
        await Until(() => model.SelectionCount == 0 && list.SelectedItems.Count == 0, "Clear did not clear the selection.");
        SelectThree();
        await Task.Delay(350);
        Require(bar.Visibility == Visibility.Visible, "A previous hide animation collapsed a new selection.");

        model.SearchText = "keep.txt";
        await Until(() => list.Items.Count == 1 && model.SelectionCount == 3, "Filtering lost the pending selection.");
        await PreviewSelection();
        model.SearchText = "no-matching-row";
        await Until(() => list.Items.Count == 0 && bar.Visibility == Visibility.Visible, "A filter hid the actions for a saved selection.");
        await ClickAction("✕");
        await Until(() => model.SelectionCount == 0, "Clear left hidden rows selected.");
        model.SearchText = "";
        SelectThree();
        model.SearchText = "keep.txt";
        await Task.Delay(350);
        await ClickAction("移出");
        await Until(() => model.SearchEntries.Count == 1, "Remove left filtered selections in the input.");
        Require(model.SearchEntries.Single().FullPath == paths[3] && paths.All(File.Exists),
            "Remove affected an unselected entry or a source file.");
        await CheckSearchAndOutputBars(root, workspace, picked);
    }

    private static async Task CheckSearchAndOutputBars(FrameworkElement root, WorkspacePage workspace, HashSet<string> picked)
    {
        async Task CheckBar(FrameworkElement bar, ListView list, int expectedButtons)
        {
            await Until(() => list.Items.Count == picked.Count && list.IsEnabled, "Fixture inputs did not load.");
            list.SelectAll();
            await Until(() => bar.Visibility == Visibility.Visible && bar.ActualWidth > 0, "Selection bar was not shown.");
            await Task.Delay(300);
            var buttons = Elements(bar).OfType<Button>().ToArray();
            Require(buttons.Length == expectedButtons, "Missing actions on " + bar.Name);
            foreach (var button in buttons) SelectionButtonAtCenter(root, button);
            SelectionButtonAtCenter(root, buttons.Single(b => Equals(b.Content, "✕"))).Invoke();
            await Until(() => list.SelectedItems.Count == 0 && bar.Visibility == Visibility.Collapsed, "Cancel failed on " + bar.Name);
            Log($"{bar.Name}: {buttons.Length} button centers hit their controls; selection cleared.");
        }

        await ResizeWindow(root, 1460, 820);
        Search.ScopeIndex = (int)SearchScope.Batch;
        InvokeButton((ToggleButton)workspace.FindName("LeftSearchNav"));
        var search = (FrameworkElement)workspace.FindName("SearchPanel");
        await CheckBar((FrameworkElement)search.FindName("SearchFloatingIsland"), (ListView)search.FindName("ResultsList"), 7);

        await Preferences.UpdateAsync(s => s with { Animations = false });
        var badge = (Border)workspace.FindName("LeftOutputsBadge");
        Require(badge.Visibility == Visibility.Collapsed && Outputs.TotalCount == 0, "The empty outputs badge is visible.");
        Outputs.Add(1, "操作栏测试", picked.Select(path => new ProducedFile(path, new FileInfo(path).Length, DateTimeOffset.Now, [path])).ToArray());
        InvokeButton((ToggleButton)workspace.FindName("LeftOutputsNav"));
        await CheckOutputsNavigation(root, workspace);
        var outputs = (FrameworkElement)workspace.FindName("OutputsPanel");
        var files = (ListView)outputs.FindName("Files");
        var sources = WithName<ListView>(outputs, "输出来源任务");
        Require(ReferenceEquals(sources.ItemContainerStyle, files.ItemContainerStyle), "Output sources do not share the workspace list style.");
        sources.SelectedValue = 1;
        await Until(() => Outputs.TaskId == 1 && files.Items.Count == 3, "Selecting an output task did not filter the file list.");
        sources.SelectedIndex = -1;
        await Until(() => Outputs.TaskId == 0 && Equals(sources.SelectedValue, 0), "Clearing the task selection did not restore All tasks.");
        sources.SelectedValue = 1;
        await Until(() => Outputs.TaskId == 1, "The task could not be selected again.");
        await CheckBar((FrameworkElement)outputs.FindName("OutputsFloatingIsland"), files, 6);
        Outputs.ClearRecordsCommand.Execute(null);
        await Until(() => badge.Visibility == Visibility.Collapsed && files.Items.Count == 0 && Equals(sources.SelectedValue, 0),
            "Clearing outputs did not hide the count and select All tasks.");
        Require(picked.All(File.Exists), "Clearing output records removed a source file.");
        evidence.Add(new { check = "clear a filtered output batch, retain all files and return to All tasks with its badge hidden" });
    }
}
