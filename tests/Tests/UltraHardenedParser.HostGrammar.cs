using System.Text;
using Glyph11;
using Glyph11.Parser.UltraHardened;

namespace Tests;

/// <summary>
/// Host field grammar — RFC 9110 §7.2, RFC 3986 §3.2.2:
/// <c>Host = uri-host [ ":" port ]</c>, <c>uri-host = IP-literal / IPv4address / reg-name</c>.
/// Anything routing or authorizing on Host relies on this being validation, not a hint.
/// </summary>
public partial class UltraHardenedParserTests
{
    private static readonly string[] ValidHosts =
    {
        // reg-name
        "example.com",
        "EXAMPLE.COM",
        "example.com.",                 // fully qualified, trailing dot
        "localhost",
        "x",
        "xn--bcher-kva.example",        // IDN arrives as punycode
        "a-b.c_d~e",                    // every unreserved punctuation
        "%41bc",                        // pct-encoded
        "a!$&'()*+,;=b",                // every sub-delim
        // IPv4address (a reg-name, syntactically)
        "127.0.0.1",
        "127.0.0.1:80",
        // port
        "example.com:8080",
        "example.com:65535",
        "example.com:0",
        // IP-literal
        "[::1]",
        "[::1]:443",
        "[::]",
        "[1::]",
        "[2001:db8::1]",
        "[2001:DB8:0:0:8:800:200C:417A]",
        "[1:2:3:4:5:6:7:8]",
        "[1:2:3:4:5:6::8]",
        "[1:2:3:4:5:6:7::]",
        "[::ffff:192.0.2.1]",
        "[::1.2.3.4]",
        "[1:2:3:4:5:6:1.2.3.4]",
    };

    private static readonly string[] InvalidHosts =
    {
        // empty
        "",
        // characters outside reg-name
        "a b",
        "a\tb",
        "a@b",
        "user@example.com",
        "a/b",
        "example.com/path",
        "a?b",
        "a#b",
        "a\\b",
        "a\"b",
        "a<b>",
        // pct-encoded must be '%' HEXDIG HEXDIG, and never a NUL
        "%4",
        "%zz",
        "%",
        "evil.com%00.good.com",
        // IP-literal
        "[::1",
        "::1",
        "[]",
        "[1.2.3.4]",
        "[:1]",
        "[1:]",
        "[1:2:3:4:5:6:7:8:9]",
        "[1:2:3:4:5:6:7:8::]",
        "[1:2:3:4:5:6:7::8]",
        "[1:2:3:4:5:6:7:1.2.3.4]",
        "[::1::2]",
        "[1:::2]",
        "[g::1]",
        "[12345::1]",
        "[::256.1.1.1]",
        "[::1.2.3]",
        "[::01.2.3.4]",
        "[v1.x]",                       // IPvFuture
        "[fe80::1%25eth0]",             // zone identifier
        "[::1]x",
        "[::1]]",
        // port
        "host:",
        "[::1]:",
        "host:65536",
        "host:000080",
        "host:abc",
        "host:-1",
        "host:+80",
        "host:80:80",
        "host: 80",
        ":80",
    };

    public static IEnumerable<object[]> ValidHostCases() =>
        from host in ValidHosts
        from multi in new[] { false, true }
        select new object[] { host, multi };

    public static IEnumerable<object[]> InvalidHostCases() =>
        from host in InvalidHosts
        from multi in new[] { false, true }
        select new object[] { host, multi };

    [Theory]
    [MemberData(nameof(ValidHostCases))]
    public void HostGrammar_Accepts(string host, bool multi)
    {
        var (ok, _) = Parse($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n", multi);

        Assert.True(ok);
        AssertHeader(_request.Headers, 0, "Host", host);
    }

    [Theory]
    [MemberData(nameof(InvalidHostCases))]
    public void HostGrammar_Rejects(string host, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => Parse($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n", multi));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Invalid Host header.", ex.Message);
    }

    [Theory]
    [InlineData("example.com ", false), InlineData("example.com ", true)]
    [InlineData("example.com\t", false), InlineData("example.com\t", true)]
    [InlineData("example.com:8080 \t ", false), InlineData("example.com:8080 \t ", true)]
    [InlineData("[::1]  ", false), InlineData("[::1]  ", true)]
    public void HostGrammar_IgnoresTrailingOws(string host, bool multi)
    {
        // OWS around a field value is not part of it (RFC 9110 §5.5); the parser keeps trailing
        // whitespace in the stored value, so the Host check must not count it against the host.
        var (ok, _) = Parse($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostGrammar_IgnoresLeadingHtab(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost:\texample.com\r\n\r\n", multi);
        Assert.True(ok);
        AssertHeader(_request.Headers, 0, "Host", "example.com");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostGrammar_Rejects_ValueOfOnlyWhitespace(bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost:  \t \r\n\r\n", multi));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostGrammar_Rejects_ObsText(bool multi)
    {
        // obs-text (0x80-0xFF) is a legal field-value byte, so only the Host grammar stops it.
        var bytes = Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\nHost: bücher.example\r\n\r\n");

        var ex = Assert.Throws<HttpParseException>(() =>
        {
            if (multi)
            {
                var seq = SplitIntoSegments(bytes);
                UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, Defaults, out _);
            }
            else
            {
                ReadOnlyMemory<byte> rom = bytes;
                UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, Defaults, out _);
            }
        });
        Assert.Equal(400, ex.StatusCode);
    }
}
