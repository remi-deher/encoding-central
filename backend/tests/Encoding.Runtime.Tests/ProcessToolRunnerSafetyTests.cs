using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Commandes externes : les arguments ne passent jamais par un interpréteur, la durée et la sortie sont bornées.</summary>
public sealed class ProcessToolRunnerSafetyTests
{
    private static string? Tool(string name) => OperatingSystem.IsWindows() ? null : ProcessToolRunner.Find(name);

    [Fact]
    public async Task ShellMetacharactersInArgumentsAreHandedOverVerbatim()
    {
        var printf = Tool("printf");
        if (printf is null) return;
        var directory = Directory.CreateTempSubdirectory("encoding-runner-").FullName;
        try
        {
            var hostile = "a; touch injected && echo $(id) `id` | cat > injected2 \"quoted\" 'single' ../../x";
            var result = await new ProcessToolRunner().RunAsync(printf, ["%s", hostile], directory, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(hostile, result.Output);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProcessExceedingItsTimeoutIsKilledAndReported()
    {
        var sleep = Tool("sleep");
        if (sleep is null) return;
        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessToolRunner().RunAsync(sleep, ["30"], Path.GetTempPath(), TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15), "Le processus doit être tué à l’échéance, pas attendu jusqu’à sa fin.");
    }

    [Fact]
    public async Task OutputBeyondTheContractLimitIsRefused()
    {
        var yes = Tool("yes");
        if (yes is null) return;
        var started = DateTimeOffset.UtcNow;
        // Le processus est interrompu dès que la limite est franchie : l'échec (IOException ou annulation) arrive bien avant l'échéance.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ProcessToolRunner().RunAsync(yes, [], Path.GetTempPath(), TimeSpan.FromSeconds(60), CancellationToken.None, outputLimit: 64 * 1024));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task MissingToolIsReportedInsteadOfFallingBackToTheShell()
    {
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new ProcessToolRunner().CheckedAsync("encoding-tool-that-does-not-exist", ["--version"], Path.GetTempPath(), CancellationToken.None));
        Assert.Contains("indisponible", error.Message, StringComparison.Ordinal);
    }
}
