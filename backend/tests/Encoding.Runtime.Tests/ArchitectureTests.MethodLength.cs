using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed partial class ArchitectureTests
{
    // Dette connue : méthodes déjà au-dessus de SoftMaxMethodLines. Ne jamais augmenter une valeur.
    // Clé = "cheminRelatif::NomMethode(types…)" (espaces normalisés).
    private static readonly Dictionary<string, int> OversizedMethodCeilings = new()
    {
        ["Encoding.Api/Executions/RuntimeRepository.Claim.cs::ClaimAvailableAsync(Guid,ClaimCursor?,CancellationToken)"] = 312,
        ["Encoding.Api/Flows/FlowDefinitionValidator.cs::Validate(FlowDefinition,bool,bool)"] = 257,
        ["Encoding.Api/Infrastructure/ClusterEntityReconciliation.cs::CaptureLocalChangesAsync(CancellationToken)"] = 214,
        ["Encoding.Api/Plugins/PluginStoreEndpoints.cs::MapPluginStoreEndpoints(WebApplication)"] = 199,
        ["Encoding.Api/Tools/AiAssistantEndpoints.cs::MapAiAssistantEndpoints(WebApplication)"] = 197,
        ["Encoding.NodeAgent/Runtime/RuntimeTransferService.cs::TransferAsync(RuntimeTransfer,RuntimeClient,CancellationToken)"] = 166,
        ["Encoding.Api/Flows/FlowSimulator.cs::Simulate(FlowSimulationRequest,IReadOnlyDictionary<string, JsonElement>?)"] = 162,
        ["Encoding.Api/Plugins/PluginConfigurationEndpoints.cs::MapPluginConfigurationEndpoints(WebApplication)"] = 158,
        ["Encoding.Api/Executions/RuntimeEligibility.cs::CanRun(FlowDefinition,IReadOnlyList<NodeToolCapability>,Guid,string?,bool,RuntimeContext?,NodeIdentityResponse?,NodeInventory?,IReadOnlyDictionary<string,Guid>?)"] = 156,
        ["Encoding.Api/Executions/RuntimeRepository.Steps.cs::CompleteStepAsync(Guid,Guid,CompleteRuntimeStep,CancellationToken)"] = 155,
        ["Encoding.Api/Logging/SystemLogEndpoints.cs::MapSystemLogEndpoints(WebApplication)"] = 152,
        ["Encoding.Contracts/Flows/SubtitleManagement.cs::Plan(IReadOnlyList<SubtitleTrack>,bool?,(int Width, int Height),SubtitleOptions)"] = 143,
        ["Encoding.Api/Executions/FlowPlacementPlanner.Requirements.cs::BuildRequirements(List<FlowNodeDefinition>,JsonElement?)"] = 141,
        ["Encoding.Api/Executions/ExecutionEndpoints.cs::MapExecutionEndpoints(WebApplication)"] = 137,
        ["Encoding.Api/Infrastructure/ClusterEntityReconciliation.Apply.cs::ApplyOneAsync(PendingChange,CancellationToken)"] = 131,
        ["Encoding.Api/Infrastructure/OpenApiEndpoints.cs::BuildSpecification(EndpointDataSource)"] = 128,
        ["Encoding.Api/Flows/FlowAiEndpoints.cs::MapFlowAiEndpoints(WebApplication)"] = 126,
        ["Encoding.Api/Flows/StandardFlowTemplateSeeder.cs::EnsureOneAsync(DbDataSource,Guid,Template,CancellationToken)"] = 121,
    };

    [Fact]
    public void MethodsStayWithinSizeCeilings()
    {
        var offenders = new List<string>();
        foreach (var method in EnumerateMethods())
        {
            var ceiling = OversizedMethodCeilings.GetValueOrDefault(method.Key, ArchitectureCeilings.SoftMaxMethodLines);
            if (method.Lines > ceiling)
                offenders.Add($"{method.Key} : {method.Lines} lignes (plafond {ceiling})");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void MethodSizeCeilingsAreNotStale()
    {
        // Plusieurs méthodes peuvent partager une signature textuelle (types imbriqués homonymes) :
        // on retient la longueur max pour la clé.
        var measured = EnumerateMethods()
            .GroupBy(method => method.Key)
            .ToDictionary(group => group.Key, group => group.Max(method => method.Lines));

        var stale = OversizedMethodCeilings.Where(entry =>
        {
            if (!measured.TryGetValue(entry.Key, out var lines))
                return true;
            return lines <= ArchitectureCeilings.SoftMaxMethodLines;
        }).Select(entry => entry.Key).ToArray();

        Assert.True(stale.Length == 0,
            "Retirer de OversizedMethodCeilings (disparu ou ≤ SoftMaxMethodLines) : " + string.Join(", ", stale));
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static IEnumerable<(string Key, int Lines)> EnumerateMethods()
    {
        foreach (var project in AllowedProjectReferences.Keys)
        foreach (var file in SourceFiles(project))
        {
            var relative = Relative(file);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var root = tree.GetCompilationUnitRoot();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var span = tree.GetLineSpan(method.Span);
                var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
                var types = string.Join(",", method.ParameterList.Parameters.Select(parameter =>
                    Whitespace.Replace(parameter.Type?.ToString() ?? "?", " ").Trim()));
                var key = $"{relative}::{method.Identifier.Text}({types})";
                yield return (key, lines);
            }
        }
    }
}
