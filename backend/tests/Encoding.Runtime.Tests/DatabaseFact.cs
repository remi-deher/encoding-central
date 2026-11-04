using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Scenario qui utilise une base SQLite temporaire.</summary>
public sealed class DatabaseFactAttribute : FactAttribute { }

/// <summary>Opt-out local : <c>ENCODING_TEST_SKIP_MEDIA=1</c> ignore les tests média (CI : jamais défini ; ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1 l'emporte).</summary>
public static class MediaTestPolicy
{
    public static bool SkipLocally =>
        Environment.GetEnvironmentVariable("ENCODING_TEST_SKIP_MEDIA") == "1" && Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_MEDIA_TOOLS") != "1";
}

/// <summary>Fact that needs ffmpeg with libx265 and ffprobe, skipped locally when they are absent.</summary>
public sealed class MediaToolsFactAttribute : FactAttribute
{
    public MediaToolsFactAttribute()
    {
        if (MediaTestPolicy.SkipLocally) { Skip = "Tests média délégués à la CI (ENCODING_TEST_SKIP_MEDIA=1)."; return; }
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_MEDIA_TOOLS") == "1") return;
        if (Encoding.WorkerAgent.Runtime.ProcessToolRunner.Find("ffmpeg") is null || Encoding.WorkerAgent.Runtime.ProcessToolRunner.Find("ffprobe") is null)
            Skip = "ffmpeg and ffprobe are required (included in backend/tests/Dockerfile).";
    }
}

/// <summary>Extraction and consumption use the real Matroska tools.</summary>
public sealed class MkvToolsFactAttribute : FactAttribute
{
    public MkvToolsFactAttribute()
    {
        if (MediaTestPolicy.SkipLocally) { Skip = "Tests média délégués à la CI (ENCODING_TEST_SKIP_MEDIA=1)."; return; }
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_MEDIA_TOOLS") == "1") return;
        if (Encoding.WorkerAgent.Runtime.ProcessToolRunner.Find("ffmpeg") is null || Encoding.WorkerAgent.Runtime.ProcessToolRunner.Find("ffprobe") is null || Encoding.WorkerAgent.Runtime.ProcessToolRunner.Find("mkvmerge") is null)
            Skip = "ffmpeg, ffprobe and mkvmerge are required (included in backend/tests/Dockerfile).";
    }
}

/// <summary>One SQLite file shared by database scenarios, run sequentially in this collection.</summary>
public sealed class RuntimeFixture : IAsyncLifetime
{
    public static string? ConnectionString => TestDatabase.ConfiguredConnection;
    internal Harness.Fixture? Scenarios { get; private set; }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var (registry, validator, eligibility) = Harness.Services();
        Scenarios = await Harness.Fixture.CreateAsync(ConnectionString, registry, validator, eligibility);
    }

    public async Task DisposeAsync()
    {
        if (Scenarios is not null) await Scenarios.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RuntimeCollection : ICollectionFixture<RuntimeFixture>
{
    public const string Name = "sqlite";
}
