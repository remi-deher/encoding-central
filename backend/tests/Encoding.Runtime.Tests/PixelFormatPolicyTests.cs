using Encoding.Contracts.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class PixelFormatPolicyTests
{
    private static VideoEncoderDescriptor Encoder(string acceleration) =>
        new("hevc_test", "hevc", "Test", acceleration, "crf", "-crf", "-preset", 0, 51, 25, []);

    [Theory]
    [InlineData("auto", "yuv420p", "yuv420p")]
    [InlineData("auto", "yuv420p10le", "yuv420p10le")]
    [InlineData("auto", "yuv420p12le", "yuv420p10le")]
    [InlineData("auto", "p010le", "yuv420p10le")]
    [InlineData("auto", null, "yuv420p")]
    [InlineData("source", "yuv420p", "yuv420p")]
    [InlineData("source", "yuv420p10le", "yuv420p10le")]
    [InlineData("8-bit", "yuv420p10le", "yuv420p")]
    [InlineData("10-bit", "yuv420p", "yuv420p10le")]
    public void Output_Depth_Follows_The_Input_In_Auto_Mode(string depth, string? source, string expected) =>
        Assert.Equal(expected, MediaCodecPolicy.ResolvePixelFormat(Encoder("cpu"), depth, source));

    [Fact]
    public void Hardware_Encoders_Use_The_Hardware_Ten_Bit_Format() =>
        Assert.Equal("p010le", MediaCodecPolicy.ResolvePixelFormat(Encoder("qsv"), "auto", "yuv420p10le"));
}
