using System.Net;
using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class OpenAiCompatibleAiClientTests
{
    [Fact]
    public async Task SuccessfulModelTextMentioningQuotaIsNotClassifiedAsRateLimit()
    {
        using var http = Client(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"HTTP 429 is a quota status code; this answer is valid.\"}}]}");

        var result = await new OpenAiCompatibleAiClient(http).GenerateTextAsync(new("system", "question"));

        Assert.True(result.Success);
        Assert.False(result.IsRateLimited);
        Assert.Contains("429", result.Content);
    }

    [Fact]
    public async Task Http429AndStructuredProviderQuotaErrorsAreRateLimited()
    {
        using var http429 = Client(HttpStatusCode.TooManyRequests, "{}");
        var statusResult = await new OpenAiCompatibleAiClient(http429).GenerateTextAsync(new("system", "question"));
        Assert.True(statusResult.IsRateLimited);

        using var structured = Client(HttpStatusCode.Forbidden,
            "{\"error\":{\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\"}}");
        var structuredResult = await new OpenAiCompatibleAiClient(structured).GenerateTextAsync(new("system", "question"));
        Assert.True(structuredResult.IsRateLimited);
    }

    [Fact]
    public async Task ProviderResponseSizeIsBounded()
    {
        using var http = Client(HttpStatusCode.OK, new string('x', 2 * 1024 * 1024 + 1));

        var result = await new OpenAiCompatibleAiClient(http).GenerateTextAsync(new("system", "question"));

        Assert.False(result.Success);
        Assert.False(result.IsRateLimited);
        Assert.Contains("taille maximale", result.ErrorMessage);
    }

    private static HttpClient Client(HttpStatusCode status, string body) =>
        new(new StaticResponseHandler(new HttpResponseMessage(status) { Content = new StringContent(body) }))
        {
            BaseAddress = new Uri("https://provider.test/v1/")
        };

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
