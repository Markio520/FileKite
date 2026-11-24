using System.ComponentModel;
using FileAssistant.App.Features.FileInput;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed partial class NamingView : UserControl
{
    public NamingViewModel ViewModel => App.Naming;
    public event EventHandler? SearchRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForCopyRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForConversionRequested;
    private readonly DispatcherQueueTimer timer;
    private bool active, pending;
    public NamingView()
    {
        InitializeComponent();
        ViewModel.CreationCopyRequested += (_, files) => FilesForCopyRequested?.Invoke(this, new(files));
        ViewModel.CreationConversionRequested += (_, files) => FilesForConversionRequested?.Invoke(this, new(files));
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(400); timer.IsRepeating = false; timer.Tick += PreviewTick;
        Loaded += (_, _) => { ViewModel.PlanInvalidated += Invalidated; ViewModel.PropertyChanged += Changed; _ = ViewModel.RefreshRecoveriesCommand.ExecuteAsync(null); Request(); };
        Unloaded += (_, _) => { timer.Stop(); ViewModel.PlanInvalidated -= Invalidated; ViewModel.PropertyChanged -= Changed; };
    }
    public void SetActive(bool value) { active = value; if (value && ViewModel.Plan is null && ViewModel.Report is null) Request(); else if (!value) timer.Stop(); }
    public void FocusQuery() { if (ViewModel.IsRename) InputView.FocusQuery(); }
    private async void PickDestination_Click(object sender, RoutedEventArgs e)
    {
        try { var paths = await FileInputSources.PickFolderAsync(); if (paths.Count > 0) ViewModel.Destination = paths[0]; }
        catch (Exception error) { ViewModel.Feedback = "无法添加文件夹，请重试：" + error.Message; }
    }
    private void Rename_Click(object sender, RoutedEventArgs e) { ViewModel.ModeValue = 0; ((ToggleButton)sender).IsChecked = true; }
    private void Create_Click(object sender, RoutedEventArgs e) { ViewModel.ModeValue = 1; ((ToggleButton)sender).IsChecked = true; }
    private void Invalidated(object? sender, EventArgs e) => Request();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NamingViewModel.RenameAction) && ViewModel.Manual) InputView.ShowListView();
        if (e.PropertyName == nameof(NamingViewModel.IsBusy) && !ViewModel.IsBusy && pending) Request();
    }
    private void Request() { pending = true; if (active && IsLoaded) { timer.Stop(); timer.Start(); } }
    private async void PreviewTick(DispatcherQueueTimer sender, object e)
    {
        if (!active || !IsLoaded || !pending || ViewModel.IsBusy || ViewModel.BuildPlanCommand.IsRunning) return;
        pending = false;
        if (ViewModel.Plan is null && ViewModel.CanBuild) await ViewModel.BuildPlanCommand.ExecuteAsync(null);
    }
    private void Input_SearchRequested(object? sender, EventArgs e) => SearchRequested?.Invoke(this, e);
    private void Input_FilesForCopyRequested(object? sender, FileInputTransferEventArgs e) => FilesForCopyRequested?.Invoke(this, e);
    private void Input_FilesForConversionRequested(object? sender, FileInputTransferEventArgs e) => FilesForConversionRequested?.Invoke(this, e);
}
