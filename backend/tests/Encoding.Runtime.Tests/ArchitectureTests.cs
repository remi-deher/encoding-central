using System.Text.RegularExpressions;
using Xunit;

namespace Encoding.Runtime.Tests;

// Garde-fous de séparation des responsabilités (BACKEND_RULES.md §3 et §8).
// Les plafonds de taille sont un cliquet : ils ne peuvent que baisser.
// Constantes centralisées dans ArchitectureCeilings (ce fichier + MethodLength).
public sealed partial class ArchitectureTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Encoding.Contracts"] = [],
        ["Encoding.Core"] = ["Encoding.Contracts"],
        ["Encoding.Api"] = ["Encoding.Contracts", "Encoding.Core"],
        ["Encoding.NodeAgent"] = ["Encoding.Contracts", "Encoding.Core"],
        ["Encoding.Launcher"] = ["Encoding.Core"],
    };

    // Dette connue : fichiers déjà au-dessus de ArchitectureCeilings.MaxFileLines. Ne jamais augmenter une valeur.
    private static readonly Dictionary<string, int> OversizedFileCeilings = new()
    {
    };

    private static readonly string[] ProcessLaunchAllowList =
    [
        "Encoding.Api/Flows/FlowMetadataImporter.cs",
        "Encoding.Api/Tools/AiCliRuntime.cs",
        "Encoding.Core/Runtime/ProcessSandbox.cs",
        "Encoding.Launcher/SystemProcess.cs",
        "Encoding.NodeAgent/Runtime/ProcessToolRunner.cs",
    ];

    private static readonly string[] EndpointStorageDebt = [];

    // Dette connue : endpoints qui injectent encore un *Repository. Ne jamais agrandir la liste.
    private static readonly string[] EndpointRepositoryDebt =
    [
        "Encoding.Api/Cluster/LeadershipEndpoints.cs",
        "Encoding.Api/Executions/ExecutionEndpoints.cs",
        "Encoding.Api/Flows/FlowAiEndpoints.cs",
        "Encoding.Api/Flows/FlowScriptEndpoints.cs",
        "Encoding.Api/Flows/FlowTemplateEndpoints.cs",
        "Encoding.Api/Infrastructure/ClusterEndpoints.cs",
        "Encoding.Api/Infrastructure/ClusterReconciliationEndpoints.cs",
        "Encoding.Api/Infrastructure/ClusterTaskEndpoints.cs",
        "Encoding.Api/Libraries/LibraryActivityEndpoints.cs",
        "Encoding.Api/Libraries/LibraryEndpoints.cs",
        "Encoding.Api/Libraries/LibraryWorkflowEndpoints.cs",
        "Encoding.Api/Logging/SystemLogEndpoints.cs",
        "Encoding.Api/Nodes/LocalNodeEndpoints.cs",
        "Encoding.Api/Nodes/NodeGroupEndpoints.cs",
        "Encoding.Api/Plugins/ArrWebhookEndpoints.cs",
        "Encoding.Api/Plugins/PluginConfigurationEndpoints.cs",
        "Encoding.Api/Plugins/PluginStoreEndpoints.cs",
        "Encoding.Api/Resources/ResourceEndpoints.cs",
    ];

    // Dette connue : new HttpClient() hors IHttpClientFactory. Ne jamais agrandir la liste.
    private static readonly string[] HttpClientConstructionDebt =
    [
        "Encoding.Api/Mcp/McpCommand.cs",
        "Encoding.NodeAgent/Services/WorkerClusterDiscovery.cs",
        "Encoding.NodeAgent/Services/WorkerEnrollmentService.cs",
    ];

    [Fact]
    public void ProjectReferencesFollowTheLayering()
    {
        foreach (var (project, allowed) in AllowedProjectReferences)
        {
            var csproj = Path.Combine(SourceRoot, project, project + ".csproj");
            var references = Regex.Matches(File.ReadAllText(csproj), @"ProjectReference Include=""[^""]*?([\w.]+)\.csproj""")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.True(references.All(allowed.Contains), $"{project} référence {string.Join(", ", references.Except(allowed))}.");
        }
    }

    [Fact]
    public void SourcesDoNotUseNamespacesOutsideTheirLayer()
    {
        foreach (var (project, allowed) in AllowedProjectReferences)
        {
            var forbidden = AllowedProjectReferences.Keys.Where(other => other != project && !allowed.Contains(other)).ToArray();
            foreach (var file in SourceFiles(project))
            {
                var text = File.ReadAllText(file);
                foreach (var other in forbidden)
                    Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(other)}[.;](?!dll)"), $"{Relative(file)} utilise {other}.");
            }
        }
    }

    [Fact]
    public void EndpointsDoNotDependOnDatabaseTypes()
    {
        var offenders = SourceFiles("Encoding.Api").Where(file => file.EndsWith("Endpoints.cs"))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bDbException\b|\bSql\.|\bCommandText\b|\bDbDataSource\b|System\.Data"))
            .Select(Relative).ToArray();
        var unexpected = offenders.Except(EndpointStorageDebt).ToArray();
        Assert.True(unexpected.Length == 0, "Endpoints dépendant du stockage : " + string.Join(", ", unexpected));
        var stale = EndpointStorageDebt.Except(offenders).ToArray();
        Assert.True(stale.Length == 0, "Retirer de EndpointStorageDebt : " + string.Join(", ", stale));
    }

    [Fact]
    public void EndpointsDoNotTouchTheFileSystemDirectly()
    {
        var offenders = SourceFiles("Encoding.Api").Where(file => file.EndsWith("Endpoints.cs") || file.EndsWith("PeerHandler.cs"))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\b(File|Directory)\.\w+\(|\bFileStream\b|\bProcess\b"))
            .Select(Relative).ToArray();
        Assert.True(offenders.Length == 0, "Accès fichier ou processus direct dans les endpoints (utiliser FileResults ou un service) : " + string.Join(", ", offenders));
    }

    [Fact]
    public void EndpointsDoNotInjectRepositoriesDirectly()
    {
        var repositoryParam = new Regex(@"\b([A-Z][A-Za-z0-9]*Repository)\s+[a-zA-Z_]");
        var offenders = SourceFiles("Encoding.Api").Where(file => file.EndsWith("Endpoints.cs"))
            .Where(file => repositoryParam.IsMatch(File.ReadAllText(file)))
            .Select(Relative).ToArray();
        var unexpected = offenders.Except(EndpointRepositoryDebt).ToArray();
        Assert.True(unexpected.Length == 0, "Endpoints injectant un dépôt (passer par un service) : " + string.Join(", ", unexpected));
        var stale = EndpointRepositoryDebt.Except(offenders).ToArray();
        Assert.True(stale.Length == 0, "Retirer de EndpointRepositoryDebt : " + string.Join(", ", stale));
    }

    [Fact]
    public void HttpClientsAreCreatedOnlyViaAllowListedFactories()
    {
        var offenders = AllowedProjectReferences.Keys.SelectMany(SourceFiles)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bnew\s+HttpClient\b"))
            .Select(Relative).Except(HttpClientConstructionDebt).ToArray();
        Assert.True(offenders.Length == 0, "new HttpClient() hors fabrique allow-listée : " + string.Join(", ", offenders));
        var stale = HttpClientConstructionDebt.Where(relative =>
        {
            var path = Path.Combine(SourceRoot, relative);
            return !File.Exists(path) || !Regex.IsMatch(File.ReadAllText(path), @"\bnew\s+HttpClient\b");
        }).ToArray();
        Assert.True(stale.Length == 0, "Retirer de HttpClientConstructionDebt : " + string.Join(", ", stale));
    }

    [Fact]
    public void ContractsAndCoreAreFreeOfStorageTypes()
    {
        var offenders = SourceFiles("Encoding.Contracts").Concat(SourceFiles("Encoding.Core"))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"Microsoft\.Data|System\.Data\.Common|Microsoft\.EntityFramework"))
            .Select(Relative).ToArray();
        Assert.True(offenders.Length == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void ProcessesAreLaunchedOnlyFromDedicatedAdapters()
    {
        var offenders = AllowedProjectReferences.Keys.SelectMany(SourceFiles)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"Process\.Start\(|new ProcessStartInfo\("))
            .Select(Relative).Except(ProcessLaunchAllowList).ToArray();
        Assert.True(offenders.Length == 0, "Lancement de processus hors adaptateur : " + string.Join(", ", offenders));
    }

    [Fact]
    public void FilesStayWithinSizeCeilings()
    {
        var offenders = new List<string>();
        foreach (var project in AllowedProjectReferences.Keys)
            foreach (var file in SourceFiles(project))
            {
                var relative = Relative(file);
                var lines = File.ReadLines(file).Count();
                var ceiling = OversizedFileCeilings.GetValueOrDefault(relative, ArchitectureCeilings.MaxFileLines);
                if (lines > ceiling) offenders.Add($"{relative} : {lines} lignes (plafond {ceiling})");
            }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void SizeCeilingsAreNotStale()
    {
        var stale = OversizedFileCeilings.Where(entry =>
        {
            var path = Path.Combine(SourceRoot, entry.Key);
            return !File.Exists(path) || File.ReadLines(path).Count() <= ArchitectureCeilings.MaxFileLines;
        }).Select(entry => entry.Key).ToArray();
        Assert.True(stale.Length == 0, "Retirer de OversizedFileCeilings : " + string.Join(", ", stale));
    }

    // Dépôts audités Phase 2 : pas d'interpolation de fragments SQL nommés filter/where/table/obsolete.
    private static readonly string[] SqlFilterAuditFiles =
    [
        "Encoding.Api/Resources/ResourceRepository.cs",
        "Encoding.Api/Executions/ExecutionRepository.cs",
        "Encoding.Api/Executions/ClusterTaskMaintenance.cs",
        "Encoding.Api/Infrastructure/ClusterEntityReconciliation.Storage.cs",
    ];

    [Fact]
    public void AuditedRepositoriesDoNotInterpolateFreeFormSqlFilters()
    {
        // Exact fragment names only ({table}=@id, {filter}, {obsolete}) — not {whereSql}/{table.Name}.
        var pattern = new Regex(@"\$""(?:[^""]|"""")*\{(?:filter|where|table|obsolete)\}", RegexOptions.IgnoreCase);
        var offenders = SqlFilterAuditFiles
            .Select(relative => Path.Combine(SourceRoot, relative))
            .Where(File.Exists)
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(Relative)
            .ToArray();
        Assert.True(offenders.Length == 0,
            "Fragments SQL dynamiques non allow-listés (utiliser des constantes + @params) : " + string.Join(", ", offenders));
    }

    [Fact]
    public void LongCodeLinesDoNotGrow()
    {
        var count = new[] { "Encoding.Api", "Encoding.NodeAgent", "Encoding.Contracts", "Encoding.Core", "Encoding.Launcher" }
            .SelectMany(SourceFiles).SelectMany(File.ReadLines)
            .Count(line => line.Length > 200 && !line.TrimStart().StartsWith("//"));
        Assert.True(count <= ArchitectureCeilings.MaxLongLines,
            $"{count} lignes de code dépassent 200 caractères (max {ArchitectureCeilings.MaxLongLines}) : les couper.");
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(SourceRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Relative(string file) => Path.GetRelativePath(SourceRoot, file).Replace('\\', '/');

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Encoding.Api");
            if (Directory.Exists(candidate)) return Path.Combine(directory.FullName, "src");
            var backend = Path.Combine(directory.FullName, "backend", "src", "Encoding.Api");
            if (Directory.Exists(backend)) return Path.Combine(directory.FullName, "backend", "src");
        }
        throw new DirectoryNotFoundException("Sources backend introuvables.");
    }

    /// <summary>Plafonds d’architecture centralisés (cliquets : ne baissent que volontairement).</summary>
    internal static class ArchitectureCeilings
    {
        public const int MaxFileLines = 600;
        public const int MaxLongLines = 227;
        public const int SoftMaxMethodLines = 120;
    }
}
