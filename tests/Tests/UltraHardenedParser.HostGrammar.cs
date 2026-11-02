using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    public static TheoryData<string, bool> ValidHostCases() => WithBothShapes(ValidHosts);

    public static TheoryData<string, bool> InvalidHostCases() => WithBothShapes(InvalidHosts);

    // Each host as one contiguous buffer and split across segments.
    private static TheoryData<string, bool> WithBothShapes(IEnumerable<string> hosts)
    {
        var data = new TheoryData<string, bool>();
        foreach (var host in hosts)
        {
            data.Add(host, false);
            data.Add(host, true);
        }
        return data;
    }

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

    // ================================================================
    // IP-literal against an independent reading of RFC 3986 §3.2.2
    // ================================================================

    [Fact]
    public void IPLiteral_AcceptedExactlyWhenRfc3986Allows()
    {
        var rng = new Random(20261102);
        int valid = 0;
        for (int i = 0; i < 4000; i++)
        {
            string address = RandomIPv6Literal(rng);
            bool expected = Rfc3986IPv6Address.IsMatch(address);
            if (expected) valid++;

            _request.Clear();
            bool accepted;
            try { accepted = Parse($"GET / HTTP/1.1\r\nHost: [{address}]\r\n\r\n", i % 2 == 1).success; }
            catch (HttpParseException) { accepted = false; }

            Assert.True(expected == accepted,
                $"[{address}]: RFC 3986 says {(expected ? "valid" : "invalid")}, the parser {(accepted ? "accepted" : "refused")} it");
        }

        // Both sides of the grammar must be well represented for the comparison to mean anything.
        Assert.InRange(valid, 1000, 3000);
    }

    // The IPv6address ABNF, transcribed rule by rule: the reference the parser is checked against.
    private static readonly Regex Rfc3986IPv6Address = BuildRfc3986IPv6Address();

    private static Regex BuildRfc3986IPv6Address()
    {
        const string h16 = "[0-9A-Fa-f]{1,4}";
        const string decOctet = "(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9][0-9]|[0-9])";
        string ls32 = $@"(?:{h16}:{h16}|{decOctet}\.{decOctet}\.{decOctet}\.{decOctet})";
        string Times(int n) => $"(?:{h16}:){{{n}}}";                 // n( h16 ":" )
        string Prefix(int n) => $"(?:(?:{h16}:){{0,{n}}}{h16})?";     // [ *n( h16 ":" ) h16 ]
        string[] forms =
        [
            Times(6) + ls32,
            "::" + Times(5) + ls32,
            Prefix(0) + "::" + Times(4) + ls32,
            Prefix(1) + "::" + Times(3) + ls32,
            Prefix(2) + "::" + Times(2) + ls32,
            Prefix(3) + "::" + h16 + ":" + ls32,
            Prefix(4) + "::" + ls32,
            Prefix(5) + "::" + h16,
            Prefix(6) + "::",
        ];
        return new Regex(@"\A(?:" + string.Join("|", forms) + @")\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    // A valid address in one of the nine forms, then - half the time - one or two random edits,
    // which usually but not always break it. Octets include values above 255 and leading zeros.
    private static string RandomIPv6Literal(Random rng)
    {
        string H16() => RandomHexDigits(rng, rng.Next(1, 5));
        string Octet() => rng.Next(5) switch
        {
            0 => rng.Next(0, 10).ToString(CultureInfo.InvariantCulture),
            1 => rng.Next(10, 100).ToString(CultureInfo.InvariantCulture),
            2 => rng.Next(100, 256).ToString(CultureInfo.InvariantCulture),
            3 => rng.Next(256, 1000).ToString(CultureInfo.InvariantCulture),
            _ => "0" + rng.Next(0, 100).ToString(CultureInfo.InvariantCulture),
        };
        string Ls32() => rng.Next(3) == 0 ? $"{Octet()}.{Octet()}.{Octet()}.{Octet()}" : $"{H16()}:{H16()}";
        string Times(int n) => string.Concat(Enumerable.Range(0, n).Select(_ => H16() + ":"));
        string Prefix(int n) => string.Join(":", Enumerable.Range(0, rng.Next(0, n + 2)).Select(_ => H16()));

        string address = rng.Next(9) switch
        {
            0 => Times(6) + Ls32(),
            1 => "::" + Times(5) + Ls32(),
            2 => Prefix(0) + "::" + Times(4) + Ls32(),
            3 => Prefix(1) + "::" + Times(3) + Ls32(),
            4 => Prefix(2) + "::" + Times(2) + Ls32(),
            5 => Prefix(3) + "::" + H16() + ":" + Ls32(),
            6 => Prefix(4) + "::" + Ls32(),
            7 => Prefix(5) + "::" + H16(),
            _ => Prefix(6) + "::",
        };

        if (rng.Next(2) == 0)
            for (int edits = rng.Next(1, 3); edits > 0; edits--)
                address = MutateOnce(address, rng);
        return address;
    }

    private static string MutateOnce(string s, Random rng)
    {
        const string alphabet = ":.0123456789abcdefABCDEFg%";
        string c = alphabet[rng.Next(alphabet.Length)].ToString();
        if (s.Length == 0)
            return c;
        int at = rng.Next(s.Length);
        return rng.Next(3) switch
        {
            0 => s.Insert(at, c),
            1 => s.Remove(at, 1),
            _ => s.Remove(at, 1).Insert(at, c),
        };
    }

    private static string RandomHexDigits(Random rng, int count)
    {
        const string digits = "0123456789abcdefABCDEF";
        var chars = new char[count];
        for (int i = 0; i < count; i++)
            chars[i] = digits[rng.Next(digits.Length)];
        return new string(chars);
    }
}
