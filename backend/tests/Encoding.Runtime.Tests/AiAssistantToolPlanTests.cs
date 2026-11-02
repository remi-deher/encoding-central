using Encoding.Api.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiAssistantToolPlanTests
{
    [Fact]
    public void ParsesDirectAnswerWithoutSelectingATool()
    {
        var plan = AiAssistantToolPlanParser.Parse("""{"kind":"answer","content":"Les sources ne donnent pas cette information."}""");

        Assert.Equal("answer", plan.Kind);
        Assert.Null(plan.Name);
    }

    [Fact]
    public void ParsesOnlyWhitelistedReadCallsWithTypedArguments()
    {
        var plan = AiAssistantToolPlanParser.Parse(
            """{"kind":"tool_call","name":"list_executions","arguments":{"state":"failed","limit":5}}""");

        var call = AiAssistantToolPlanParser.ParseToolCall(plan);

        Assert.Equal("list_executions", call.Name);
        Assert.Equal("failed", call.State);
        Assert.Equal(5, call.Limit);
    }

    [Theory]
    [InlineData("""{"kind":"tool_call","name":"delete_flow","arguments":{}}""")]
    [InlineData("""{"kind":"tool_call","name":"list_flows","arguments":{"flowId":"11111111-1111-4111-8111-111111111111"}}""")]
    [InlineData("""{"kind":"tool_call","name":"list_executions","arguments":{"limit":100000}}""")]
    [InlineData("""{"kind":"tool_call","name":"get_flow","arguments":{"flowId":"invalid"}}""")]
    [InlineData("""{"kind":"tool_call","name":"list_flows","arguments":{},"extra":true}""")]
    public void RejectsUnknownToolsAndInvalidArguments(string json)
    {
        var exception = Record.Exception(() =>
        {
            var plan = AiAssistantToolPlanParser.Parse(json);
            AiAssistantToolPlanParser.ParseToolCall(plan);
        });

        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void RejectsMalformedStructuredOutput()
    {
        Assert.Throws<ArgumentException>(() => AiAssistantToolPlanParser.Parse("not json"));
    }
}
