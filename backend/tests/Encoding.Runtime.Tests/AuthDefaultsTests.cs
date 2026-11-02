using Encoding.Api.Auth;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AuthDefaultsTests
{
    [Fact]
    public void AuthenticationIsRequiredUnlessExplicitlyDisabled()
    {
        Assert.True(AuthOptions.From(new ConfigurationBuilder().Build()).Enabled);
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Enabled"] = "false" }).Build();
        Assert.False(AuthOptions.From(disabled).Enabled);
    }

    [Fact]
    public void TokenScopesAreNormalizedAndMappedToDomains()
    {
        var (scopes, unknown) = ApiTokenScopes.Normalize([" Flows:Write ", "flows:write", "root", ""]);
        Assert.Equal(["flows:write"], scopes);
        Assert.Equal(["root"], unknown);
        Assert.Equal("libraries:write", ApiTokenScopes.RequiredFor("libraries"));
        Assert.Equal("flows:write", ApiTokenScopes.RequiredFor("scripts"));
        Assert.Equal(ApiTokenScopes.AllWrites, ApiTokenScopes.RequiredFor("tools-and-validation"));
        Assert.Equal("flows:write,executions:control", ApiTokenScopes.Serialize(["flows:write", "executions:control"]));
        Assert.Empty(ApiTokenScopes.Parse(null));
    }
}
