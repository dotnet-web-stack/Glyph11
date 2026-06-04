using Glyph11;

namespace Tests;

public partial class UltraHardenedParserTests
{
    // ================================================================
    // StatusCode on HttpParseException
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MethodLimitThrows400(bool multi)
    {
        var limits = Defaults with { MaxMethodLength = 3 };
        var raw = "POST / HTTP/1.1\r\nHost: x\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi, limits));
        Assert.Equal(400, ex.StatusCode);
        Assert.False(ex.IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderNameLimitThrows431(bool multi)
    {
        var limits = Defaults with { MaxHeaderNameLength = 4 };
        var raw = "GET / HTTP/1.1\r\nHost: x\r\nLongHeaderName: val\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi, limits));
        Assert.Equal(431, ex.StatusCode);
        Assert.True(ex.IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UrlLimitThrows431(bool multi)
    {
        var limits = Defaults with { MaxUrlLength = 5 };
        var raw = "GET /toolong HTTP/1.1\r\nHost: x\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi, limits));
        Assert.Equal(431, ex.StatusCode);
        Assert.True(ex.IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TotalHeaderLimitThrows431(bool multi)
    {
        var limits = Defaults with { MaxTotalHeaderBytes = 20 };
        var raw = "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi, limits));
        Assert.Equal(431, ex.StatusCode);
        Assert.True(ex.IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderValueLimitThrows431(bool multi)
    {
        var limits = Defaults with { MaxHeaderValueLength = 3 };
        var raw = "GET / HTTP/1.1\r\nHost: x\r\nKey: longvalue\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi, limits));
        Assert.Equal(431, ex.StatusCode);
        Assert.True(ex.IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderCountLimitThrows431(bool multi)
    {
        var limits = Defaults with { MaxHeaderCount = 1 };
        var raw = "GET / HTTP/1.1\r\nHost: x\r\nH2: v2\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi, limits));
        Assert.Equal(431, ex.StatusCode);
        Assert.True(ex.IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuralErrorThrows400(bool multi)
    {
        var raw = "GET / HTTP/1.1\r\nHost: x\r\nBadHeader\r\n\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi));
        Assert.Equal(400, ex.StatusCode);
        Assert.False(ex.IsLimitViolation);
    }

    // ---- Semantic violations also throw 400 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SemanticViolationThrows400_MissingHost(bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\n\r\n", multi));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SemanticViolationThrows400_ConnectMethod(bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => Parse("CONNECT host:443 HTTP/1.1\r\nHost: host:443\r\n\r\n", multi));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SemanticViolationThrows400_ConflictingCL(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "Content-Length: 10\r\n" +
            "Content-Length: 20\r\n" +
            "\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SemanticViolationThrows400_TEplusCL(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Content-Length: 10\r\n" +
            "\r\n";

        var ex = Assert.Throws<HttpParseException>(() => Parse(raw, multi));
        Assert.Equal(400, ex.StatusCode);
    }
}
