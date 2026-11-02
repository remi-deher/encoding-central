using System.Text.Json.Serialization;

namespace Encoding.Contracts.Logging;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}

public sealed record SystemLogEntry(
    string Id,
    DateTimeOffset Timestamp,
    SystemLogLevel Level,
    string Category,
    string Message,
    string? Exception = null,
    Guid? NodeId = null,
    string? NodeName = null,
    int? EventId = null
);

public sealed record SystemLogFilterQuery(
    SystemLogLevel? MinLevel = null,
    string? Category = null,
    string? Search = null,
    Guid? NodeId = null,
    int Limit = 100,
    DateTimeOffset? Since = null
);

public sealed record SystemLogCategoriesResponse(
    IReadOnlyList<string> Categories
);

public sealed record SystemLogOverviewResponse(
    IReadOnlyList<SystemLogEntry> Entries,
    int TotalBuffered
);
