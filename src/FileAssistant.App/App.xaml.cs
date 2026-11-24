using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FileAssistant.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Indexing.Windows.SharedIndexEngine? indexRuntime;
    private Mutex? instanceMutex;
    private bool closing;
    private bool closed;
    public static Core.Settings.AppPreferences Preferences { get; private set; } = null!;
    internal static Infrastructure.FileSystem.CopyService CopyService { get; private set; } = null!;
    internal static Infrastructure.FileSystem.NamingService NamingService { get; private set; } = null!;
    internal static string ProfileDirectory { get; private set; } = Core.AppProfile.Directory;
    internal static Indexing.Windows.SharedIndexEngine? Index => ((App)Current).indexRuntime;
    private Core.Settings.AppSettings applied = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource inputLifetime = new();
    private Task indexWarmup = Task.CompletedTask;
    internal static CancellationToken InputStopping => ((App)Current).inputLifetime.Token;
    private Task collectionsLoading = Task.CompletedTask;
    private Task activations = Task.CompletedTask;
    private Bootstrap.TrayFileDrop? trayDrop;
    private Infrastructure.Windows.DesktopShell? shell;
    private Bootstrap.WindowPlacementStore? placement;
    private bool decidingClose;
    private Task startupMaintenance = Task.CompletedTask;
    internal static CancellationToken Stopping => ((App)Current).lifetime.Token;
    public static Features.Workspace.WorkspaceViewModel Workspace { get; private set; } = null!;
    public static Features.Workspace.WorkspaceViewModel CopyInput { get; private set; } = null!;
    public static Features.Search.SearchViewModel Search { get; private set; } = null!;
    public static Core.Engines.IFileProcessingService FileProcessing { get; private set; } = null!;
    public static Features.Convert.ConversionViewModel Conversion { get; private set; } = null!;
    public static Features.Organize.CopyViewModel Copy { get; private set; } = null!;
    public static Features.CreateOrRename.NamingViewModel Naming { get; private set; } = null!;
    public static Features.SpaceAnalysis.SpaceAnalysisViewModel Space { get; private set; } = null!;
    public static Features.Cleanup.CleanupViewModel Cleanup { get; private set; } = null!;
    public static Features.Outputs.OutputsViewModel Outputs { get; private set; } = null!;
    public static Features.Tasks.ProcessingJobsViewModel Jobs { get; private set; } = null!;
    public static Features.Settings.EnginesViewModel EngineSettings { get; private set; } = null!;
    public static Features.Settings.AppearanceViewModel Appearance { get; private set; } = null!;
    internal static Features.Settings.PaneWidthPreferences PaneWidths { get; private set; } = null!;
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, error) =>
        {
            var directory = Path.Combine(ProfileDirectory, "Logs");
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "app-errors.log"), $"{DateTimeOffset.Now:O}\n{error.Exception}\n"); }
            catch (Exception) { }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
#if SHARED_INDEX_TRIAL
        const bool indexTrial = true;
#else
        const bool indexTrial = false;
#endif
        var instance = Infrastructure.Windows.InstanceActivation.GetInstanceName(
            identity.User!.Value, Core.AppProfile.OverrideDirectory, indexTrial);
        instanceMutex = new Mutex(false, @"Local\" + instance + ".App");
        bool ownsInstance;
        try { ownsInstance = instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            if (!BackgroundLaunch)
            {
                try { await new Infrastructure.Windows.InstanceActivation(instance).SendAsync(LaunchPaths(), CancellationToken.None); }
                catch (Exception e) { ReportFailure(e); }
            }
            instanceMutex.Dispose();
            Exit();
            return;
        }
        var helper = Path.Combine(AppContext.BaseDirectory, "FileAssistant.SpaceScan.exe");
        indexRuntime = new Indexing.Windows.SharedIndexEngine(Path.Combine(Core.AppProfile.Directory, "Indexes"), helper,
            settings: () => Preferences?.Current ?? new()) { MaintainIndexStorage = true };
        await InitializeWorkspaceAsync(Core.AppProfile.Directory, indexRuntime, indexRuntime);
        try
        {
            CreateWorkspaceWindow();
            ObserveIndex(indexRuntime);
            placement = new(ProfileDirectory);
            await placement.RestoreAsync(Window.AppWindow);
            placement.Observe(Window.AppWindow);
            Window.AppWindow.Changed += (w, _) => placement.Observe(w);
            InstallDesktopShell();
            collectionsLoading = Task.WhenAll(Workspace.LoadCollectionAsync(), CopyInput.LoadCollectionAsync(),
                Conversion.Input.LoadCollectionAsync(), Naming.Input.LoadCollectionAsync());
            activations = new Infrastructure.Windows.InstanceActivation(instance).ListenAsync(
                paths => DispatcherQueue.TryEnqueue(() => _ = ActivatePathsAsync(paths)), ReportFailure, lifetime.Token);
            Window.AppWindow.Closing += OnWindowClosing;
            if (!BackgroundLaunch) Window.Activate();
            if (LaunchPaths() is { Length: > 0 } paths) await ActivatePathsAsync(paths);
        }
        catch (Exception ex)
        {
            var directory = Path.Combine(ProfileDirectory, "Logs");
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "app-errors.log"), $"{DateTimeOffset.Now:O}\n{ex}\n"); }
            catch (Exception) { }
            throw;
        }
        if (Preferences.Current.PrewarmIndex || BackgroundLaunch) indexWarmup = WarmIndexAsync();
        startupMaintenance = RunStartupMaintenanceAsync();
    }

    private async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        await RequestExitAsync(forceQuit: false);
    }

    private async Task RequestExitAsync(bool forceQuit)
    {
        if (closing || decidingClose) return;
        decidingClose = true;
        try { if (!await ShouldExitAsync(forceQuit)) return; }
        catch (Exception error) { ReportFailure(error); return; }
        finally { decidingClose = false; }
        closing = true;
        inputLifetime.Cancel();
        Search.Deactivate();
        Workspace.CancelLoadingCommand.Execute(null);
        CopyInput.CancelLoadingCommand.Execute(null);
        Conversion.Input.CancelLoadingCommand.Execute(null);
        Conversion.CancelCommand.Execute(null);
        Copy.CancelCommand.Execute(null);
        Naming.Input.CancelLoadingCommand.Execute(null);
        Naming.CancelCommand.Execute(null);
        var spaceStopping = Space.StopAsync();
        var cleanupStopping = Cleanup.StopAsync();
        EngineSettings.CancelCommand.Execute(null);
        try
        {
            if (placement is not null) await placement.SaveAsync(Window.AppWindow);
            Window.AppWindow.Hide();
            var completing = new Task?[] { Conversion.RunCommand.ExecutionTask, Conversion.BuildPlanCommand.ExecutionTask,
                Copy.RunCommand.ExecutionTask, Copy.BuildPlanCommand.ExecutionTask, Copy.WaitForIdleAsync(),
                Naming.WaitForIdleAsync(), spaceStopping, cleanupStopping, EngineSettings.DownloadVideoCommand.ExecutionTask,
                EngineSettings.DownloadOfficeCommand.ExecutionTask, collectionsLoading };
            // One failed or cancelled job must not bypass the other shutdown work and the collection flush.
            try { await Task.WhenAll(completing.OfType<Task>()); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ReportFailure(error); }
            await Task.WhenAll(Workspace.FlushCollectionAsync(), CopyInput.FlushCollectionAsync(), Conversion.Input.FlushCollectionAsync(), Naming.Input.FlushCollectionAsync());
            if (Workspace.HasUnsavedCollection || CopyInput.HasUnsavedCollection || Conversion.Input.HasUnsavedCollection || Naming.Input.HasUnsavedCollection)
            { ResumeAfterFailedClose(); return; }
            await Search.FlushPreferencesAsync();
            await Appearance.FlushPreferencesAsync();
            await PaneWidths.FlushAsync();
            await Preferences.FlushAsync();
            await Space.ReleaseIndexViewAsync();
            await lifetime.CancelAsync();
            await activations;
            await startupMaintenance;
            await shortcutUpdates;
            if (indexRuntime is not null) await indexRuntime.DisposeAsync();
        }
        catch (Exception error)
        {
            ReportFailure(error);
            if (!lifetime.IsCancellationRequested) { ResumeAfterFailedClose(); return; }
        }
        trayDrop?.Dispose();
        shell?.Dispose();
        closed = true;
        instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        inputLifetime.Dispose();
        if (Window is MainWindow main) main.ReleaseThemeHandlers();
        Window.Close();
    }

    private void ResumeAfterFailedClose()
    {
        inputLifetime.Dispose();
        inputLifetime = new();
        closing = false;
        RestoreMain();
    }

    private void ObserveIndex(Indexing.Windows.SharedIndexEngine engine)
    {
        engine.Changed += root => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            Search.IndexChanged(root);
            Space.IndexChanged(root);
        });
    }

    private static void CreateWorkspaceWindow()
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window = new MainWindow();
    }

    private async Task InitializeWorkspaceAsync(string profile, Core.Abstractions.IFileSearch searchService,
        Core.Features.SpaceAnalysis.ISpaceScanner scanner)
    {
        ProfileDirectory = profile;
        Preferences = new(new Infrastructure.Persistence.JsonSettingsStore(Path.Combine(profile, "settings-v6.json")));
        Appearance = new(new Infrastructure.Persistence.JsonSettingsStore(Path.Combine(profile, "appearance-settings.json")));
        PaneWidths = new(new Infrastructure.Persistence.JsonSettingsStore(Path.Combine(profile, "pane-widths.json")));
        await Task.WhenAll(Appearance.LoadPreferencesAsync(), PaneWidths.LoadAsync());
        await Preferences.LoadAsync(new() { Mode = (Core.Settings.AppearanceMode)(int)Appearance.Style,
            Palette = Appearance.IsLightStyle ? "sand" : "charcoal", TopNavigation = Appearance.IsTopNavigation });
        Appearance.Connect(Preferences);
        Jobs = new();
        var inputReader = new Infrastructure.FileSystem.FileInputReader();
        var collectionsDirectory = Path.Combine(profile, "Collections");
        Workspace = new(inputReader, Features.Workspace.WorkspaceInputPurpose.Staging,
            new Infrastructure.Persistence.JsonFileCollectionStore(Path.Combine(collectionsDirectory, "staging.json")));
        CopyInput = new(inputReader, Features.Workspace.WorkspaceInputPurpose.Copy,
            new Infrastructure.Persistence.JsonFileCollectionStore(Path.Combine(collectionsDirectory, "copy.json")));
        var paths = Infrastructure.Engines.EnginePaths.Installed with { Preferences = () => Preferences.Current };
        FileProcessing = new Infrastructure.Engines.LocalFileProcessingService(paths, Jobs.Slots);
        EngineSettings = new(FileProcessing, new Infrastructure.Engines.EnginePackageInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            Infrastructure.Engines.EnginePaths.ExtensionsDirectory), Infrastructure.Engines.EnginePackageCatalog.Ffmpeg, Infrastructure.Engines.EnginePackageCatalog.LibreOffice);
        Outputs = new(new Infrastructure.Windows.WindowsFileInteraction());
        CopyService = new(Path.Combine(profile, "CopyRecovery"));
        NamingService = new(Path.Combine(profile, "NamingRecovery"));
        Copy = new(CopyService, CopyInput, Outputs, Jobs);
        Conversion = new(FileProcessing, new(inputReader, Features.Workspace.WorkspaceInputPurpose.Conversion,
            new Infrastructure.Persistence.JsonFileCollectionStore(Path.Combine(collectionsDirectory, "conversion.json"))), Outputs, Jobs);
        Naming = new(NamingService,
            new(inputReader, Features.Workspace.WorkspaceInputPurpose.Rename, new Infrastructure.Persistence.JsonFileCollectionStore(Path.Combine(collectionsDirectory, "rename.json"))), Outputs, Jobs, new Infrastructure.FileSystem.DirectoryContentReader(), new Infrastructure.Windows.WindowsFileInteraction());
        void SynchronizePathChanges(object? sender, IReadOnlyList<Core.Features.CreateOrRename.PathChange> changes)
        {
            if (changes.Count == 0) return;
            foreach (var input in new[] { Workspace, CopyInput, Conversion.Input, Naming.Input }) input.ApplyPathChanges(changes);
            Outputs.ApplyPathChanges(changes);
            Search.FilePathsChanged(changes);
            Cleanup.ApplyPathChanges(changes);
            if (!closing) _ = Space.ApplyPathChangesAsync(changes);
        }
        Naming.PathsChanged += SynchronizePathChanges;
        Copy.PathsChanged += SynchronizePathChanges;
        Space = new(scanner, new Infrastructure.Windows.WindowsSpaceRecycler(() => WindowHandle),
            new Infrastructure.Windows.WindowsFileInteraction(), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), preferences: () => Preferences.Current);
        Naming.FilesRemoved += SynchronizeRemovedPaths;
        Space.FilesRemoved += SynchronizeRemovedPaths;
        Cleanup = new(new Infrastructure.Cleanup.CleanupService(Path.Combine(AppContext.BaseDirectory, "FileAssistant.CleanupHost.exe")), Jobs);
        Cleanup.FilesRemoved += SynchronizeRemovedPaths;
        Cleanup.SoftwareChanged += (_, _) => { if (!closing) _ = Space.RefreshAfterCleanupAsync(); Search.FilePathsChanged(); };
        void SynchronizeRemovedPaths(object? sender, IReadOnlyList<string> removed)
        {
            if (removed.Count == 0) return;
            var reason = ReferenceEquals(sender, Space) ? "已移入回收站，原路径已失效。" : null;
            foreach (var input in new[] { Workspace, CopyInput, Conversion.Input, Naming.Input }) input.ApplyPathChanges([], removed, reason);
            Outputs.ApplyPathChanges([], removed);
            Search.FilePathsChanged(removed: removed);
            if (!ReferenceEquals(sender, Cleanup)) Cleanup.ApplyPathChanges([], removed);
            if (!closing && !ReferenceEquals(sender, Space)) _ = Space.ApplyPathChangesAsync(removed: removed);
        }
        EngineSettings.AvailabilityChanged += (_, _) => Conversion.RefreshCapabilities();
        Search = new(searchService, () => Workspace.SearchEntries, async files =>
            await Features.FileInput.FileCollectionDialogs.ImportAsync(Workspace, Core.Features.FileCollections.FileCollectionBundle.FromFiles(files), (Microsoft.UI.Xaml.FrameworkElement)Window.Content)
                ?? new([], "已取消加入暂存区。"), new Infrastructure.Windows.WindowsFileInteraction(),
            new Infrastructure.Persistence.JsonSettingsStore(Path.Combine(profile, "search-settings.json")),
            new Infrastructure.FileSystem.DirectoryContentReader(), preferences: () => Preferences.Current)
            { InputDispatcher = SynchronizationContext.Current };
        await Search.LoadPreferencesAsync();
        try { await Features.Settings.SettingsOperations.FinishInterruptedImportAsync(); }
        catch (Exception e) { ReportFailure(e); }
        if (!Preferences.HasDocument && Search.LoadIndexOnDemand)
            await Preferences.UpdateAsync(s => s with { PrewarmIndex = false });
        ApplyPreferences(initial: true);
        Preferences.Changed += (_, _) => ApplyPreferences(initial: false);
        Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Search.LoadIndexOnDemand) && Search.LoadIndexOnDemand == Preferences.Current.PrewarmIndex)
                _ = SavePreferenceAsync(s => s with { PrewarmIndex = !Search.LoadIndexOnDemand });
        };
        Jobs.BatchCompleted += OnBatchCompleted;
    }

    private async Task WarmIndexAsync()
    {
        try { await indexRuntime!.EnsureReadyAsync(null, lifetime.Token); }
        catch (Exception) { /* The search view presents the retained failure and an explicit retry. */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}
