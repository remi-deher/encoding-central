using System.Net;
using System.Net.Http.Json;
using Encoding.Api.Mcp;
using Encoding.Api.Updates;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ReleaseServiceTests
{
    private static ReleaseManifest Manifest(string version) => new(version, "owner/repo", "abc123",
        [new(version, DateTimeOffset.Parse("2026-10-24T12:00:00Z"), "Correction de l’encodage.")]);

    [Theory]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("1.10.0", "1.9.0", false)]
    [InlineData("2.0.0", "2.0.0", false)]
    public async Task ComparesVersionsNumericallyAndCachesChecks(string installed, string latest, bool available)
    {
        var http = new FakeHttp(() => JsonContent.Create(Manifest(latest)));
        var service = new ReleaseService(http, Manifest(installed));
        var status = await service.GetUpdatesAsync(default);
        Assert.Equal(available, status.UpdateAvailable);
        Assert.Equal(available ? "available" : "current", status.Status);
        Assert.Equal(installed, status.InstalledVersion);
        Assert.Equal(latest, status.LatestVersion);
        Assert.Contains(status.Releases, r => r.Version == installed);
        Assert.Same(status, await service.GetUpdatesAsync(default));
        Assert.Equal(1, http.Calls);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("{\"version\":\"3.0.0\",\"repository\":\"owner/repo\",\"releases\":null}")]
    [InlineData("{\"version\":\"3.0.0\",\"repository\":\"other/repo\",\"releases\":[]}")]
    public async Task InvalidRemoteDataDoesNotClaimUpToDate(string body)
    {
        var service = new ReleaseService(new FakeHttp(() => new StringContent(body)), Manifest("1.0.0"));
        var status = await service.GetUpdatesAsync(default);
        Assert.Equal("unavailable", status.Status);
        Assert.Null(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
        Assert.Single(status.Releases);
    }

    [Fact]
    public async Task NetworkFailurePreservesEmbeddedNotes()
    {
        var service = new ReleaseService(new FakeHttp(() => throw new HttpRequestException()), Manifest("1.0.0"));
        var status = await service.GetUpdatesAsync(default);
        Assert.Equal("unavailable", status.Status);
        Assert.Null(status.UpdateAvailable);
        Assert.NotNull(status.CheckedAt);
        Assert.Equal("Correction de l’encodage.", Assert.Single(status.Releases).Notes);
    }

    [Fact]
    public async Task DevelopmentDoesNotMakeNetworkRequests()
    {
        var http = new FakeHttp(() => throw new InvalidOperationException());
        var service = new ReleaseService(http, Manifest("0.0.0-dev"));
        Assert.Equal("development", (await service.GetUpdatesAsync(default)).Status);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData("get_version")]
    [InlineData("get_updates")]
    public void McpVersionToolsAreReadOnly(string name)
    {
        var tool = BusinessOperationCatalog.FindTool(name);
        Assert.NotNull(tool);
        Assert.True(tool.ReadOnly);
        Assert.False(tool.RequiresAdministrator);
    }

    private sealed class FakeHttp(Func<HttpContent> content) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("https://github.com/owner/repo/releases/latest/download/release-manifest.json", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
        }
    }
}
