using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Settings;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FileAssistant.App.Features.Workspace;

public sealed partial class WorkspacePage
{
    private bool initialModuleApplied;

    internal void RebindShortcuts()
    {
        KeyboardAccelerators.Clear();
        var shortcuts = App.Preferences.Current.Shortcuts;
        foreach (var action in Enum.GetValues<ShortcutAction>().Except([ShortcutAction.ToggleWindow, ShortcutAction.GlobalSearch]))
        {
            if (!ShortcutChord.TryParse(shortcuts.Get(action), out var chord)) continue;
            var mods = VirtualKeyModifiers.None;
            if ((chord.Modifiers & 1) != 0) mods |= VirtualKeyModifiers.Menu;
            if ((chord.Modifiers & 2) != 0) mods |= VirtualKeyModifiers.Control;
            if ((chord.Modifiers & 4) != 0) mods |= VirtualKeyModifiers.Shift;
            if ((chord.Modifiers & 8) != 0) mods |= VirtualKeyModifiers.Windows;
            var accel = new KeyboardAccelerator { Key = (VirtualKey)chord.Key, Modifiers = mods };
            accel.Invoked += async (_, args) =>
            {
                if (settingsDialog is not null || AnyModalOpen()) return;
                if (mods == VirtualKeyModifiers.None && chord.Key is not (>= 0x70 and <= 0x87) && EditingText()) return;
                args.Handled = true;
                try { await RunShortcutAsync(action); } catch (Exception error) { App.ReportFailure(error); }
            };
            KeyboardAccelerators.Add(accel);
        }
    }

    private bool AnyModalOpen() => XamlRoot is not null && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
        .Any(p => p.IsOpen && (p.Child is ContentDialog || Descendant<ContentDialog>(p.Child) is not null));

    private bool EditingText()
    {
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or RichEditBox or PasswordBox) return true;
        return false;
    }

    internal void OpenGlobalSearch()
    {
        settingsDialog?.Hide();
        App.Search.ScopeIndex = (int)SearchScope.Everything;
        NavigateTo("search");
        DispatcherQueue.TryEnqueue(() => SearchPanel?.FocusQuery());
    }

    private async Task RunShortcutAsync(ShortcutAction action)
    {
        if (action == ShortcutAction.Settings) { await OpenSettingsAsync(); return; }
        if (action == ShortcutAction.Filter)
        {
            switch (currentPage)
            {
                case "search": SearchPanel?.FocusQuery(); break;
                case "organize": CopyPanel?.FocusQuery(); break;
                case "naming": NamingPanel?.FocusQuery(); break;
                case "convert": ConversionPanel?.FocusQuery(); break;
                case "outputs": OutputsPanel?.FocusQuery(); break;
                case "space": if (spacePanel is not null) await spacePanel.RunShortcutAsync(action); break;
                case "cleanup": cleanupPanel?.FocusQuery(); break;
                default: StagingPanel.FocusQuery(); break;
            }
            return;
        }
        if (currentPage == "search" && SearchPanel is not null) { await SearchPanel.RunShortcutAsync(action); return; }
        if (currentPage == "space" && spacePanel is not null) { await spacePanel.RunShortcutAsync(action); return; }
        if (currentPage == "cleanup" && action == ShortcutAction.Refresh)
        {
            if (!App.Cleanup.IsBusy)
            {
                if (App.Cleanup.FilesMode) await App.Cleanup.PlanAsync();
                else if (App.Cleanup.ShowResidues && App.Cleanup.CanScanResidue) await App.Cleanup.ScanResidueAsync();
                else await App.Cleanup.LoadSoftwareAsync();
            }
            return;
        }
        if (currentPage == "outputs")
        {
            if (action is ShortcutAction.QuickLook or ShortcutAction.Compare)
                await Preview.QuickLookDialog.ShowAsync(this, App.Outputs.SelectedFiles, 0, action == ShortcutAction.Compare);
            else if (action == ShortcutAction.Refresh) App.Outputs.RefreshDisplay();
            return;
        }
        var input = currentPage switch
        {
            "workspace" => StagingPanel,
            "convert" => Descendant<FileInputView>(ConversionPanel),
            "organize" => Descendant<FileInputView>(CopyPanel),
            "naming" => Descendant<FileInputView>(NamingPanel),
            _ => null
        };
        if (input is null) return;
        if (action == ShortcutAction.Refresh) await input.ViewModel.RefreshReferencesAsync();
        if (action is ShortcutAction.QuickLook or ShortcutAction.Compare) await input.OpenQuickLookAsync(action == ShortcutAction.Compare);
    }

    private static T? Descendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ResetRailWidth(object? sender, EventArgs e)
    {
        preferredRailWidth = null;
        UpdateNavigationLayout(false);
    }

    private void External_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Handled && e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void External_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var def = e.GetDeferral();
        e.Handled = true;
        try { await ImportExternalAsync(await FileInputSources.ReadAsync(e.DataView), App.Preferences.Current.DropTarget); }
        catch (Exception error) { App.ReportFailure(error); }
        finally { def.Complete(); }
    }

    internal async Task ImportExternalAsync(IReadOnlyList<string> paths, DropDestination dest)
    {
        if (paths.Count == 0) return;
        var page = dest == DropDestination.CurrentModule ? currentPage : "workspace";
        if (dest != DropDestination.SilentStaging && page is not ("convert" or "organize" or "naming" or "cleanup")) NavigateTo("workspace");
        if (page == "cleanup" && !App.Cleanup.IsBusy) { App.Cleanup.AddPaths(paths, navigate: true); return; }
        var input = page switch { "convert" => App.Conversion.Input, "organize" => App.CopyInput, "naming" => App.Naming.Input, _ => App.Workspace };
        var token = App.InputStopping;
        try { await input.QueueImportPathsAsync(paths, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
