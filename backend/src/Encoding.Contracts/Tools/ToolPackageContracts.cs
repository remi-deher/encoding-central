namespace Encoding.Contracts.Tools;

/// <summary>Paquet d'outil autonome distribué par le Store, indépendant d'un plugin.</summary>
public sealed record ToolPackageManifest(
    int ContractVersion,
    string Id,
    string Version,
    string Name,
    string Platform,
    IReadOnlyList<ToolPackageExecutable> Executables,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>Un rôle de l'application fourni par un exécutable dont le contenu est vérifié séparément.</summary>
public sealed record ToolPackageExecutable(string ToolId, string Path, string Sha256);

/// <summary>Dépendance d'un plugin vers un paquet d'outil partagé du Store.</summary>
public sealed record PluginToolDependency(
    string ToolPackageId,
    string MinimumVersion,
    string? MaximumVersionExclusive = null,
    IReadOnlyList<string>? RequiredCapabilities = null);

/// <summary>Plateforme de distribution stable, indépendante du nom de l'hôte qui exécute l'application.</summary>
public static class ToolPackagePlatform
{
    public static string Current => (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) switch
    {
        (true, _, System.Runtime.InteropServices.Architecture.X64) => "windows-x64",
        (true, _, System.Runtime.InteropServices.Architecture.Arm64) => "windows-arm64",
        (_, true, System.Runtime.InteropServices.Architecture.X64) => "macos-x64",
        (_, true, System.Runtime.InteropServices.Architecture.Arm64) => "macos-arm64",
        (_, _, System.Runtime.InteropServices.Architecture.X64) => "linux-x64",
        (_, _, System.Runtime.InteropServices.Architecture.Arm64) => "linux-arm64",
        (_, _, System.Runtime.InteropServices.Architecture.Arm) => "linux-arm",
        _ => "unsupported"
    };
}
