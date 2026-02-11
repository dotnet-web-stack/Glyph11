using Glyph11;
using Glyph11.Parser.Hardened;

namespace Tests;

public partial class UltraHardenedParserTests
{
    // ================================================================
    // Resource limits
    // ================================================================

    // ---- Header count ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenHeaderCountExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxHeaderCount = 2 };
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "H1: v1\r\n" +
            "H2: v2\r\n" +
            "\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsExactlyMaxHeaderCount(bool multi)
    {
        var limits = Defaults with { MaxHeaderCount = 2 };
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "H1: v1\r\n" +
            "\r\n";

        var (ok, _) = Parse(raw, multi, limits);
        Assert.True(ok);
        Assert.Equal(2, _request.Headers.Count);
    }

    // ---- Header name length ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenHeaderNameExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxHeaderNameLength = 4 };
        var raw = "GET / HTTP/1.1\r\nHost: x\r\nLongName: val\r\n\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }

    // ---- Header value length ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenHeaderValueExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxHeaderValueLength = 3 };
        var raw = "GET / HTTP/1.1\r\nHost: x\r\nKey: longvalue\r\n\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }

    // ---- Method length ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenMethodExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxMethodLength = 3 };
        var raw = "POST / HTTP/1.1\r\nHost: x\r\n\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsMethodAtExactLimit(bool multi)
    {
        var limits = Defaults with { MaxMethodLength = 3 };
        var raw = "GET / HTTP/1.1\r\nHost: x\r\n\r\n";

        var (ok, _) = Parse(raw, multi, limits);
        Assert.True(ok);
    }

    // ---- URL length ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenUrlExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxUrlLength = 5 };
        var raw = "GET /toolong HTTP/1.1\r\nHost: x\r\n\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }

    // ---- Query parameter count ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenQueryParamCountExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxQueryParameterCount = 2 };
        var raw = "GET /p?a=1&b=2&c=3 HTTP/1.1\r\nHost: x\r\n\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsExactlyMaxQueryParamCount(bool multi)
    {
        var limits = Defaults with { MaxQueryParameterCount = 2 };
        var raw = "GET /p?a=1&b=2 HTTP/1.1\r\nHost: x\r\n\r\n";

        var (ok, _) = Parse(raw, multi, limits);
        Assert.True(ok);
        Assert.Equal(2, _request.QueryParameters.Count);
    }

    // ---- Total header bytes ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_WhenTotalHeaderBytesExceedsLimit(bool multi)
    {
        var limits = Defaults with { MaxTotalHeaderBytes = 20 };
        var raw = "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n";

        Assert.Throws<HttpParseException>(
            () => Parse(raw, multi, limits));
    }
}
