namespace FileAssistant.Core.Features.SpaceAnalysis;

/// <summary>The first link or cloud placeholder encountered on the path to a requested directory.</summary>
public sealed class SpaceTraversalBoundaryException(string boundaryPath)
    : IOException("此路径经过链接或云端占位目录，请选择可直接读取的位置。")
{
    public string BoundaryPath { get; } = boundaryPath;
}
