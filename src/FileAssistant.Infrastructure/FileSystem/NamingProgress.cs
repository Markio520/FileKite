using System.Diagnostics;
using FileAssistant.Core.Engines;

namespace FileAssistant.Infrastructure.FileSystem;

internal sealed class NamingProgress(IProgress<ProcessingProgress>? target) : IProgress<ProcessingProgress>
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool first = true;
    public void Report(ProcessingProgress value)
    {
        if (!first && clock.ElapsedMilliseconds < 100 && value.Completed < value.Total) return;
        first = false; clock.Restart(); target?.Report(value);
    }
}
