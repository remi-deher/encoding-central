using Encoding.Api.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Encoding.Runtime.Tests;

internal sealed class AiCliTestProfile : IDisposable
{
    public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "encoding-ai-" + Guid.NewGuid().ToString("N"));
    public IConfiguration Configuration { get; }
    public AiCliRuntime Runtime { get; }
    public AiCliTestProfile(string? script = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var executable = Path.Combine(DirectoryPath, "cli");
        if (script is not null)
        {
            File.WriteAllText(executable, "#!/bin/sh\n" + script);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiTools:CredentialsDirectory"] = Path.Combine(DirectoryPath, "credentials"),
            ["PluginSettings:KeyDirectory"] = Path.Combine(DirectoryPath, "keys"),
            ["AiTools:Executables:codex-cli"] = executable,
            ["AiTools:Executables:claude-cli"] = executable
        }).Build();
        Runtime = new TestCliRuntime(Configuration, new EphemeralDataProtectionProvider());
    }
    // Only the controlled shell fixtures bypass the OS sandbox; production has no configuration switch to disable it.
    private sealed class TestCliRuntime(IConfiguration configuration, IDataProtectionProvider protection) : AiCliRuntime(configuration, protection)
    {
        protected override bool UseSandbox => false;
    }
    public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
}
