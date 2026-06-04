using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;

namespace Tests;

public class BinaryRequestTests
{
    [Fact]
    public void Clear_ResetsAllFields()
    {
        using var request = new BinaryRequest();
        var limits = ParserLimits.Default;

        // Populate via parser
        ReadOnlyMemory<byte> rom = "GET /path?a=1 HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray();
        UltraHardenedParser.TryExtractFullHeaderROM(ref rom, request, in limits, out _);

        // Verify fields are populated
        Assert.False(request.Method.IsEmpty);
        Assert.False(request.Path.IsEmpty);
        Assert.False(request.Version.IsEmpty);
        Assert.True(request.Headers.Count > 0);
        Assert.True(request.QueryParameters.Count > 0);

        request.Clear();

        Assert.True(request.Method.IsEmpty);
        Assert.True(request.Path.IsEmpty);
        Assert.True(request.Version.IsEmpty);
        Assert.True(request.Body.IsEmpty);
        Assert.Equal(0, request.Headers.Count);
        Assert.Equal(0, request.QueryParameters.Count);
    }
}
