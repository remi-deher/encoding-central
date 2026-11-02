using Encoding.Api.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowScriptFileTests
{
    [Theory]
    [InlineData("trackfix.py", "python")]
    [InlineData("Outil.JS", "javascript")]
    [InlineData("script.sh", null)]
    [InlineData("sans-extension", null)]
    public void Language_Follows_Extension(string file, string? expected) => Assert.Equal(expected, FlowScriptFiles.LanguageOf(file));

    [Fact]
    public void Name_Uses_File_Stem_Without_Directory() => Assert.Equal("trackfix", FlowScriptFiles.NameOf("C:/dossier/trackfix.py"));

    [Fact]
    public void Export_File_Name_Is_Safe_And_Has_Extension()
    {
        Assert.Equal("a_b.py", FlowScriptFiles.FileName("a/b", "python"));
        Assert.Equal("script.js", FlowScriptFiles.FileName("  ", "javascript"));
    }
}
