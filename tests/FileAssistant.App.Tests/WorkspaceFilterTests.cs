using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Tests;

public sealed class WorkspaceFilterTests
{
    [Fact]
    public void Preview_follows_selected_groups_even_when_a_filter_hides_them()
    {
        var vm = new WorkspaceViewModel();
        var group = vm.CreateFolder("资料");
        var empty = vm.CreateFolder("空组");
        vm.EnterFolder(group);
        vm.AddSearchFiles([new(@"D:\chosen.txt", false, 1, null)]);
        vm.EnterFolder(Guid.Empty);
        vm.AddSearchFiles([new(@"D:\other.txt", false, 2, null)]);
        vm.SetSelection([vm.VisibleFiles.Single(row => row.FolderId == empty)]);
        Assert.False(vm.CanPreviewSelection);
        vm.SetSelection([vm.VisibleFiles.Single(row => row.FolderId == group)]);
        vm.SearchText = "other";
        Assert.Equal(0, vm.VisibleSelectedCount);
        Assert.True(vm.CanPreviewSelection);
        Assert.Equal("chosen.txt", Assert.Single(vm.SelectedEntries).Name);
        vm.SearchText = "no-matching-row";
        Assert.Empty(vm.VisibleFiles);
        Assert.True(vm.ShowFloatingIsland);
        vm.SetSelection([]);
        Assert.False(vm.CanPreviewSelection);
        Assert.False(vm.ShowFloatingIsland);
    }

    [Fact]
    public void Filtering_refreshes_once_and_preserves_rows_selection_and_staging_snapshots()
    {
        var vm = new WorkspaceViewModel();
        vm.AddSearchFiles([new(@"D:\first.txt", false, 1, null), new(@"D:\second.txt", false, 2, null)]);
        var first = vm.VisibleFiles[0];
        var snapshot = vm.SearchEntries;
        vm.SetSelection([first], first);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.VisibleFiles.CollectionChanged += (_, change) => changes.Add(change.Action);
        vm.SearchText = "second";
        Assert.Equal("second.txt", Assert.Single(vm.VisibleFiles).Name);
        vm.SearchText = "";
        Assert.Same(first, vm.VisibleFiles[0]);
        Assert.Equal(1, vm.SelectionCount);
        Assert.Same(snapshot, vm.SearchEntries);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, action => Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, action));
        vm.AddSearchFiles([new(@"D:\third.txt", false, 3, null)]);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(3, vm.SearchEntries.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void All_type_choices_filter_staging_and_independent_inputs(bool isStaging)
    {
        SearchFile[] files =
        [
            new(@"D:\image.JPG", false, 10, null), new(@"D:\document.PDF", false, 10, null),
            new(@"D:\archive.7z", false, 10, null), new(@"D:\video.mp4", false, 10, null),
            new(@"D:\audio.FLAC", false, 10, null), new(@"D:\tool.EXE", false, 10, null),
            new(@"D:\unknown.bin", false, 10, null), new(@"D:\folder.jpg", true, null, null)
        ];
        var expected = new Dictionary<SearchKind, string[]>
        {
            [SearchKind.All] = files.Select(file => file.Name).ToArray(),
            [SearchKind.Audio] = ["audio.FLAC"], [SearchKind.Archives] = ["archive.7z"],
            [SearchKind.Documents] = ["document.PDF"], [SearchKind.Executables] = ["tool.EXE"],
            [SearchKind.Folders] = ["folder.jpg"], [SearchKind.Images] = ["image.JPG"],
            [SearchKind.Video] = ["video.mp4"],
            [SearchKind.Files] = files.Where(file => !file.IsFolder).Select(file => file.Name).ToArray()
        };
        var viewModel = new WorkspaceViewModel(isStaging: isStaging);
        viewModel.AddSearchFiles(files);

        Assert.Equal(expected.Count, WorkspaceSelectionOptions.Types.Count);
        foreach (var option in WorkspaceSelectionOptions.Types)
        {
            viewModel.FilterIndex = option.Value;
            Assert.Equal(expected[(SearchKind)option.Value], viewModel.VisibleFiles.Select(file => file.Name));
            Assert.Equal(files.Length, viewModel.SearchEntries.Count);
        }

        viewModel.SearchText = "audio";
        viewModel.FilterIndex = (int)SearchKind.Audio;
        Assert.Equal("audio.FLAC", Assert.Single(viewModel.VisibleFiles).Name);
        viewModel.FilterIndex = (int)SearchKind.Folders;
        Assert.Empty(viewModel.VisibleFiles);
        viewModel.SearchText = string.Empty;
        Assert.True(Assert.Single(viewModel.VisibleFiles).Source!.IsFolder);
    }
}
