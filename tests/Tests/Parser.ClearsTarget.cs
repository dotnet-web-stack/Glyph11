using System.Buffers;
using System.Text;
using Glyph11.Parser;
using Glyph11.Parser.FlexibleParser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// The parsers own their target for the duration of a call: a <see cref="BinaryRequest"/> or
/// <see cref="BinaryResponse"/> reused across keep-alive messages must not carry one message's
/// fields into the next, whether or not the caller remembered to <c>Clear()</c> it.
/// </summary>
public class ParserClearsTargetTests
{
    public enum RequestEntry
    {
        UltraHardenedRom,
        UltraHardenedSequenceSingle,
        UltraHardenedSequenceMulti,
        FlexibleRom,
        FlexibleSequenceSingle,
        FlexibleSequenceMulti,
    }

    public enum ResponseEntry
    {
        Rom,
        SequenceSingle,
        SequenceMulti,
    }

    // Request N carries credentials and a query string; request N+1 carries neither.
    private const string FirstRequest =
        "POST /login?user=alice&token=secret HTTP/1.1\r\n" +
        "Host: a.example\r\n" +
        "Authorization: Bearer secret\r\n" +
        "Cookie: session=abc\r\n" +
        "Content-Length: 0\r\n\r\n";

    private const string SecondRequest =
        "GET /public HTTP/1.0\r\n" +
        "Host: b.example\r\n\r\n";

    private const string IncompleteRequest =
        "GET /next HTTP/1.1\r\n" +
        "Host: b.example\r\n";

    private const string FirstResponse =
        "HTTP/1.1 401 Unauthorized\r\n" +
        "WWW-Authenticate: Bearer\r\n" +
        "Set-Cookie: session=abc\r\n" +
        "Content-Length: 0\r\n\r\n";

    private const string SecondResponse =
        "HTTP/1.0 204\r\n" +
        "Date: Mon, 02 Nov 2026 00:00:00 GMT\r\n\r\n";

    private const string IncompleteResponse =
        "HTTP/1.1 200 OK\r\n" +
        "Content-Length: 0\r\n";

    // ================================================================
    // Requests
    // ================================================================

    [Theory]
    [InlineData(RequestEntry.UltraHardenedRom)]
    [InlineData(RequestEntry.UltraHardenedSequenceSingle)]
    [InlineData(RequestEntry.UltraHardenedSequenceMulti)]
    [InlineData(RequestEntry.FlexibleRom)]
    [InlineData(RequestEntry.FlexibleSequenceSingle)]
    [InlineData(RequestEntry.FlexibleSequenceMulti)]
    public void Request_SecondParse_HoldsOnlyItsOwnFields(RequestEntry entry)
    {
        using var request = new BinaryRequest();

        Assert.True(Parse(entry, FirstRequest, request));
        Assert.Equal(4, request.Headers.Count);
        Assert.Equal(2, request.QueryParameters.Count);

        // No Clear() in between: the parser must not append to the previous request.
        Assert.True(Parse(entry, SecondRequest, request));

        AssertAscii.Equal("GET", request.Method);
        AssertAscii.Equal("/public", request.Path);

        if (IsUltraHardened(entry))
            AssertAscii.Equal("HTTP/1.0", request.Version);
        else
            Assert.True(request.Version.IsEmpty); // FlexibleParser never sets Version

        Assert.Equal(1, request.Headers.Count);
        AssertAscii.Equal("Host", request.Headers[0].Key);
        AssertAscii.Equal("b.example", request.Headers[0].Value);

        Assert.Equal(0, request.QueryParameters.Count);
        Assert.True(request.Body.IsEmpty);
    }

    [Theory]
    [InlineData(RequestEntry.UltraHardenedRom)]
    [InlineData(RequestEntry.UltraHardenedSequenceSingle)]
    [InlineData(RequestEntry.UltraHardenedSequenceMulti)]
    [InlineData(RequestEntry.FlexibleRom)]
    [InlineData(RequestEntry.FlexibleSequenceSingle)]
    [InlineData(RequestEntry.FlexibleSequenceMulti)]
    public void Request_NoCredentialFromPreviousRequestSurvives(RequestEntry entry)
    {
        using var request = new BinaryRequest();

        Assert.True(Parse(entry, FirstRequest, request));
        Assert.True(Parse(entry, SecondRequest, request));

        var headers = request.Headers.AsSpan();
        foreach (var header in headers)
        {
            var name = Encoding.ASCII.GetString(header.Key.Span);
            Assert.NotEqual("Authorization", name);
            Assert.NotEqual("Cookie", name);
        }
    }

    [Theory]
    [InlineData(RequestEntry.UltraHardenedRom)]
    [InlineData(RequestEntry.UltraHardenedSequenceSingle)]
    [InlineData(RequestEntry.UltraHardenedSequenceMulti)]
    [InlineData(RequestEntry.FlexibleRom)]
    [InlineData(RequestEntry.FlexibleSequenceSingle)]
    [InlineData(RequestEntry.FlexibleSequenceMulti)]
    public void Request_IncompleteParse_LeavesPreviousContentsIntact(RequestEntry entry)
    {
        using var request = new BinaryRequest();

        Assert.True(Parse(entry, FirstRequest, request));
        Assert.False(Parse(entry, IncompleteRequest, request));

        // An incomplete call writes nothing, so it does not clear either.
        AssertAscii.Equal("POST", request.Method);
        AssertAscii.Equal("/login", request.Path);
        Assert.Equal(4, request.Headers.Count);
        Assert.Equal(2, request.QueryParameters.Count);
    }

    [Theory]
    [InlineData(RequestEntry.UltraHardenedRom)]
    [InlineData(RequestEntry.UltraHardenedSequenceMulti)]
    [InlineData(RequestEntry.FlexibleRom)]
    [InlineData(RequestEntry.FlexibleSequenceMulti)]
    public void Request_ManyReuses_DoNotAccumulate(RequestEntry entry)
    {
        using var request = new BinaryRequest();

        for (int i = 0; i < 50; i++)
        {
            Assert.True(Parse(entry, FirstRequest, request));
            Assert.Equal(4, request.Headers.Count);
            Assert.Equal(2, request.QueryParameters.Count);
        }
    }

    // ================================================================
    // Responses
    // ================================================================

    [Theory]
    [InlineData(ResponseEntry.Rom)]
    [InlineData(ResponseEntry.SequenceSingle)]
    [InlineData(ResponseEntry.SequenceMulti)]
    public void Response_SecondParse_HoldsOnlyItsOwnFields(ResponseEntry entry)
    {
        using var response = new BinaryResponse();

        Assert.True(Parse(entry, FirstResponse, response));
        Assert.Equal(3, response.Headers.Count);
        AssertAscii.Equal("Unauthorized", response.ReasonPhrase);

        // No Clear() in between: the parser must not append to the previous response.
        Assert.True(Parse(entry, SecondResponse, response));

        AssertAscii.Equal("HTTP/1.0", response.Version);
        Assert.Equal(204, response.Status);
        AssertAscii.Equal("204", response.StatusCode);
        Assert.True(response.ReasonPhrase.IsEmpty);

        Assert.Equal(1, response.Headers.Count);
        AssertAscii.Equal("Date", response.Headers[0].Key);
        AssertAscii.Equal("Mon, 02 Nov 2026 00:00:00 GMT", response.Headers[0].Value);
        Assert.True(response.Body.IsEmpty);
    }

    [Theory]
    [InlineData(ResponseEntry.Rom)]
    [InlineData(ResponseEntry.SequenceSingle)]
    [InlineData(ResponseEntry.SequenceMulti)]
    public void Response_IncompleteParse_LeavesPreviousContentsIntact(ResponseEntry entry)
    {
        using var response = new BinaryResponse();

        Assert.True(Parse(entry, FirstResponse, response));
        Assert.False(Parse(entry, IncompleteResponse, response));

        Assert.Equal(401, response.Status);
        AssertAscii.Equal("Unauthorized", response.ReasonPhrase);
        Assert.Equal(3, response.Headers.Count);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static bool IsUltraHardened(RequestEntry entry)
        => entry is RequestEntry.UltraHardenedRom
            or RequestEntry.UltraHardenedSequenceSingle
            or RequestEntry.UltraHardenedSequenceMulti;

    private static bool Parse(RequestEntry entry, string raw, BinaryRequest request)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var limits = ParserLimits.Default;

        switch (entry)
        {
            case RequestEntry.UltraHardenedRom:
            {
                ReadOnlyMemory<byte> rom = bytes;
                return UltraHardenedParser.TryExtractFullHeaderROM(ref rom, request, in limits, out _);
            }
            case RequestEntry.UltraHardenedSequenceSingle:
            {
                var seq = new ReadOnlySequence<byte>(bytes);
                return UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, request, in limits, out _);
            }
            case RequestEntry.UltraHardenedSequenceMulti:
            {
                var seq = ThreeSegments(bytes);
                return UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, request, in limits, out _);
            }
            case RequestEntry.FlexibleRom:
            {
                ReadOnlyMemory<byte> rom = bytes;
                return FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref rom, request, out _);
            }
            case RequestEntry.FlexibleSequenceSingle:
            {
                var seq = new ReadOnlySequence<byte>(bytes);
                return FlexibleParser.TryExtractFullHeader(ref seq, request, out _);
            }
            case RequestEntry.FlexibleSequenceMulti:
            {
                var seq = ThreeSegments(bytes);
                return FlexibleParser.TryExtractFullHeader(ref seq, request, out _);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(entry));
        }
    }

    private static bool Parse(ResponseEntry entry, string raw, BinaryResponse response)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var limits = ParserLimits.Default;

        switch (entry)
        {
            case ResponseEntry.Rom:
            {
                ReadOnlyMemory<byte> rom = bytes;
                return UltraHardenedParser.TryExtractFullResponseHeaderROM(ref rom, response, in limits, out _);
            }
            case ResponseEntry.SequenceSingle:
            {
                var seq = new ReadOnlySequence<byte>(bytes);
                return UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, response, in limits, out _);
            }
            case ResponseEntry.SequenceMulti:
            {
                var seq = ThreeSegments(bytes);
                return UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, response, in limits, out _);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(entry));
        }
    }

    private static ReadOnlySequence<byte> ThreeSegments(byte[] data)
    {
        int split1 = data.Length / 3;
        int split2 = 2 * data.Length / 3;

        var first = new BufferSegment(data.AsMemory(0, split1));
        var last = first.Append(data.AsMemory(split1, split2 - split1)).Append(data.AsMemory(split2));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
