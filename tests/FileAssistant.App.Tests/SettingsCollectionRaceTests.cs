using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Tests;

public sealed class SettingsCollectionRaceTests
{
    private sealed class PausingStore : IFileCollectionStore
    {
        public TaskCompletionSource<CollectionLoadResult> Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileCollectionSnapshot? Saved { get; private set; }
        public Task<CollectionLoadResult> LoadAsync(CancellationToken token) => Read.Task;
        public async Task SaveAsync(FileCollectionSnapshot snapshot, CancellationToken token)
        {
            Writing.TrySetResult();
            await Proceed.Task.WaitAsync(token);
            Saved = snapshot;
        }
    }

    [Fact]
    public async Task Close_flush_persists_an_edit_that_arrives_during_the_first_write()
    {
        var store = new PausingStore();
        store.Read.SetResult(new(null));
        var vm = new WorkspaceViewModel(null, WorkspaceInputPurpose.Staging, store);
        await vm.LoadCollectionAsync();
        vm.CreateFolder("First");
        var flush = vm.FlushCollectionAsync();
        await store.Writing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.CreateFolder("Second");
        store.Proceed.SetResult();
        await flush;
        Assert.False(vm.HasUnsavedCollection);
        Assert.Equal(["First", "Second"], store.Saved!.Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task Changes_observed_during_profile_restore_apply_to_the_restored_references()
    {
        var source = new FileCollection();
        var file = new SearchFile(@"C:\fixtures\before.png", false, 42, null);
        const string moved = @"C:\fixtures\after.png";
        source.Import(Guid.Empty, FileCollectionBundle.FromFiles([file]), false);
        var store = new PausingStore();
        var vm = new WorkspaceViewModel(null, WorkspaceInputPurpose.Staging, store);
        var load = vm.LoadCollectionAsync();
        vm.ApplyPathChanges([new PathChange(file.FullPath, moved, false)]);
        store.Read.SetResult(new(source.Snapshot(Guid.Empty)));
        await load;
        Assert.Equal(moved, Assert.Single(vm.SearchEntries).FullPath);
        store.Proceed.SetResult();
        await vm.FlushCollectionAsync();
        Assert.Equal(moved, Assert.Single(store.Saved!.Entries).File.FullPath);
    }
}
