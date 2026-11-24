using FileAssistant.Core.Engines;
using FileAssistant.Core.Settings;

namespace FileAssistant.Core.Tests;

public sealed class BatchRuleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Processing_limits_bound_real_leases_and_release_cancelled_waiters(int maximum)
    {
        var slots = new BatchExecutionSlots(maximum);
        var limit = maximum == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 8) : maximum;
        Assert.Equal(limit, slots.Limit);
        var held = new List<IDisposable>();
        try
        {
            for (var index = 0; index < limit; index++) held.Add(await slots.EnterAsync(default));
            using var cancellation = new CancellationTokenSource();
            var waiting = slots.EnterAsync(cancellation.Token).AsTask();
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            var next = slots.EnterAsync(default).AsTask();
            Assert.False(next.IsCompleted);
            held[0].Dispose();
            using var admitted = await next.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    }

    [Fact]
    public async Task Resizing_preserves_active_work_and_admits_against_the_new_limit()
    {
        var slots = new BatchExecutionSlots(2);
        using var first = await slots.EnterAsync(default);
        using var second = await slots.EnterAsync(default);
        var third = slots.EnterAsync(default).AsTask();
        Assert.False(third.IsCompleted);
        slots.SetMaximum(4);
        using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(3));
        using var fourth = await slots.EnterAsync(default);
        slots.SetMaximum(2);
        var waiting = slots.EnterAsync(default).AsTask();
        first.Dispose();
        second.Dispose();
        Assert.False(waiting.IsCompleted);
        thirdLease.Dispose();
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(OutputLocation.Source)]
    [InlineData(OutputLocation.Subfolder)]
    public void Merged_pdf_uses_first_image_parent_while_independent_outputs_follow_each_input(OutputLocation location)
    {
        var root = Path.GetTempPath();
        var first = new ProcessingSource(Path.Combine(root, "first", "same.png"), 2, 1);
        var second = new ProcessingSource(Path.Combine(root, "second", "same.png"), 2, 1);
        var request = new ConversionRequest([first.Path, second.Path], "", ConversionTarget.Pdf) { Location = location };
        var merged = ConversionPlanner.Create(request, [second, first], _ => false);
        Assert.Equal(Path.Combine(request.DirectoryFor(second.Path), "合并图片.pdf"), Assert.Single(merged.Items).OutputPath);
        var singles = ConversionPlanner.Create(request with { Target = ConversionTarget.Jpeg }, [first, second], _ => false);
        Assert.Equal([Path.Combine(request.DirectoryFor(first.Path), "same.jpg"), Path.Combine(request.DirectoryFor(second.Path), "same.jpg")],
            singles.Items.Select(item => item.OutputPath));
    }
}
