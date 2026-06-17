using System.Buffers;
using System.Text;
using Glyph11.Utils;

namespace Benchmarks;

public static class BenchmarkData
{
    /// <summary>
    /// Builds a small (~80B) HTTP/1.1 request header with 2 headers.
    /// </summary>
    public static byte[] BuildSmallHeader()
    {
        return Encoding.ASCII.GetBytes(
            "GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 100\r\n" +
            "Server: Glyph11\r\n\r\n");
    }

    /// <summary>
    /// Builds a valid HTTP/1.1 request header block of approximately targetBytes size.
    /// Fills with realistic headers until the target is reached.
    /// </summary>
    public static byte[] BuildHeader(int targetBytes)
    {
        var sb = new StringBuilder(targetBytes + 128);
        sb.Append("GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n");
        sb.Append("Host: localhost\r\n");

        int index = 0;
        while (sb.Length < targetBytes - 4) // leave room for final \r\n
        {
            // Pad value to fill space — realistic long header values
            string name = $"X-Header-{index++}";
            int remaining = targetBytes - sb.Length - name.Length - 4; // ": " + value + "\r\n"
            int valueLen = Math.Min(Math.Max(remaining, 1), 200);
            string value = new string('A', valueLen);
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Splits a byte array into exactly 3 segments.
    /// </summary>
    public static ReadOnlySequence<byte> ToThreeSegments(byte[] data)
    {
        int split1 = data.Length / 3;
        int split2 = 2 * data.Length / 3;

        var first = new BufferSegment(data.AsMemory(0, split1));
        var last = first.Append(data.AsMemory(split1, split2 - split1)).Append(data.AsMemory(split2));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
