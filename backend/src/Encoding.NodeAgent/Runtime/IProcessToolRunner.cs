namespace Encoding.NodeAgent.Runtime;

/// <summary>Process launch seam used by media/runtime adapters (substitutable in tests).</summary>
public interface IProcessToolRunner
{
    string? Find(string executable);

    Task<ToolResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        string? input = null,
        int outputLimit = 2 * 1024 * 1024,
        bool sandbox = false,
        IReadOnlyList<string>? readOnlyPaths = null,
        bool trackProgress = false,
        double? progressDurationSeconds = null);

    Task<string> CheckedAsync(string executable, IEnumerable<string> args, string directory, CancellationToken ct);

    Task<ToolResult> CheckedWithResultAsync(string executable, IEnumerable<string> args, string directory, CancellationToken ct);
}
