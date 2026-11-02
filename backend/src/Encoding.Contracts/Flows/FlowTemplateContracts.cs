using System.Text.Json;

namespace Encoding.Contracts.Flows;

public sealed record FlowTemplateSummary(
    Guid Id,
    string Name,
    string Description,
    string MediaType,
    Guid SourceFlowId,
    int SourceVersion,
    string SourceFlowName,
    int NodeCount,
    DateTimeOffset CreatedAt,
    int Revision = 1,
    int ConfigurableFieldCount = 0);

public sealed record FlowTemplateDetails(
    FlowTemplateSummary Template,
    FlowDefinition Definition,
    FlowTemplateConfiguration Configuration);

public sealed record FlowTemplateConfiguration(IReadOnlyList<FlowTemplateConfigurableNode> Nodes);

public sealed record FlowTemplateConfigurableNode(string NodeId, IReadOnlyList<string> FieldIds);

public sealed record FlowTemplateNodeValues(string NodeId, IReadOnlyDictionary<string, JsonElement> Values);

public sealed record UpdateFlowTemplateRequest(
    int ExpectedRevision,
    string Name,
    string Description,
    FlowDefinition Definition,
    FlowTemplateConfiguration Configuration);

public sealed record CreateFlowTemplateFromFlowRequest(
    Guid SourceFlowId,
    int SourceVersion,
    string Name,
    string Description,
    FlowTemplateConfiguration? Configuration = null);
