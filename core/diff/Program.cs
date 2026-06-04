using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;

namespace Glyph11.Diff;

internal enum Kind { Parsed, Incomplete, Error }

internal sealed class Result
{
    public Kind Kind;
    public int HttpCode;
    public string? Note;
    public byte[] Method = [], Path = [], Version = [];
    public readonly List<(byte[] n, byte[] v)> Headers = new();
    public readonly List<(byte[] n, byte[] v)> Query = new();
    public long Consumed;
}

internal static class Program
{
    // ---- C side (P/Invoke) ----
    private static unsafe Result RunC(byte[] input)
    {
        var lim = NLimits.Default();
        var hdrs = stackalloc NField[256];
        var qry  = stackalloc NField[256];
        var req = new NRequest { Headers = hdrs, HeaderCap = 256, Query = qry, QueryCap = 256 };
        nuint consumed = 0;
        int st;
        fixed (byte* p = input)
            st = Native.glyph11_parse_request(p, (nuint)input.Length, &lim, &req, &consumed);

        var r = new Result();
        if (st == Native.OK)
        {
            r.Kind    = Kind.Parsed;
            r.Method  = Slice(input, req.Method);
            r.Path    = Slice(input, req.Path);
            r.Version = Slice(input, req.Version);
            for (uint i = 0; i < req.HeaderCount; i++)
                r.Headers.Add((Slice(input, hdrs[i].Name), Slice(input, hdrs[i].Value)));
            for (uint i = 0; i < req.QueryCount; i++)
                r.Query.Add((Slice(input, qry[i].Name), Slice(input, qry[i].Value)));
            r.Consumed = (long)consumed;
        }
        else if (st == Native.INCOMPLETE) r.Kind = Kind.Incomplete;
        else { r.Kind = Kind.Error; r.HttpCode = Native.glyph11_status_http_code(st); }
        return r;
    }

    private static byte[] Slice(byte[] buf, NSpan s)
    {
        var r = new byte[s.Len];
        if (s.Len != 0) Array.Copy(buf, (int)s.Off, r, 0, (int)s.Len);
        return r;
    }

    // ---- C# side (reference) ----
    private static Result RunCs(byte[] input)
    {
        var r = new Result();
        var req = new BinaryRequest();
        var lim = ParserLimits.Default;
        try
        {
            var rom = (ReadOnlyMemory<byte>)input;
            bool ok = UltraHardenedParser.TryExtractFullHeaderROM(ref rom, req, in lim, out int br);
            if (ok)
            {
                r.Kind    = Kind.Parsed;
                r.Method  = req.Method.ToArray();
                r.Path    = req.Path.ToArray();
                r.Version = req.Version.ToArray();
                var hs = req.Headers;
                for (int i = 0; i < hs.Count; i++) r.Headers.Add((hs[i].Key.ToArray(), hs[i].Value.ToArray()));
                var qs = req.QueryParameters;
                for (int i = 0; i < qs.Count; i++) r.Query.Add((qs[i].Key.ToArray(), qs[i].Value.ToArray()));
                r.Consumed = br + 1; // C# returns total-1; the C ABI returns the clean total
            }
            else r.Kind = Kind.Incomplete;
        }
        catch (HttpParseException ex) { r.Kind = Kind.Error; r.HttpCode = ex.StatusCode; }
        catch (Exception ex) { r.Kind = Kind.Error; r.HttpCode = -1; r.Note = ex.GetType().Name; }
        finally { req.Dispose(); }
        return r;
    }

    private static bool Eq(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    private static string? Compare(Result c, Result cs)
    {
        if (cs.Note != null) return $"C# threw {cs.Note}";
        if (c.Kind != cs.Kind) return $"outcome C={c.Kind} C#={cs.Kind}";
        if (c.Kind == Kind.Error)
            return c.HttpCode == cs.HttpCode ? null : $"http C={c.HttpCode} C#={cs.HttpCode}";
        if (c.Kind != Kind.Parsed) return null;
        if (!Eq(c.Method, cs.Method)) return "method";
        if (!Eq(c.Path, cs.Path)) return "path";
        if (!Eq(c.Version, cs.Version)) return "version";
        if (c.Headers.Count != cs.Headers.Count) return $"hcount C={c.Headers.Count} C#={cs.Headers.Count}";
        for (int i = 0; i < c.Headers.Count; i++)
            if (!Eq(c.Headers[i].n, cs.Headers[i].n) || !Eq(c.Headers[i].v, cs.Headers[i].v)) return $"header[{i}]";
        if (c.Query.Count != cs.Query.Count) return $"qcount C={c.Query.Count} C#={cs.Query.Count}";
        for (int i = 0; i < c.Query.Count; i++)
            if (!Eq(c.Query[i].n, cs.Query[i].n) || !Eq(c.Query[i].v, cs.Query[i].v)) return $"query[{i}]";
        if (c.Consumed != cs.Consumed) return $"consumed C={c.Consumed} C#={cs.Consumed}";
        return null;
    }

    private static int Main(string[] args)
    {
        int iterations = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 1_000_000;
        int total = 0, diverged = 0;
        var firsts = new List<string>();

        void Check(byte[] input)
        {
            total++;
            var d = Compare(RunC(input), RunCs(input));
            if (d != null) { diverged++; if (firsts.Count < 25) firsts.Add($"  [{Show(input)}] : {d}"); }
        }

        var seeds = Curated.Select(Bytes).ToArray();
        foreach (var s in seeds) Check(s);

        var rng = new Random(1234567);
        for (int it = 0; it < iterations; it++)
        {
            var buf = (byte[])seeds[rng.Next(seeds.Length)].Clone();
            int flips = rng.Next(6);
            for (int k = 0; k < flips && buf.Length > 0; k++) buf[rng.Next(buf.Length)] = (byte)rng.Next(256);
            Check(buf[..rng.Next(buf.Length + 1)]);
        }

        Console.WriteLine($"differential: {total - diverged}/{total} agree, {diverged} diverged");
        foreach (var f in firsts) Console.WriteLine(f);
        return diverged == 0 ? 0 : 1;
    }

    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    private static string Show(byte[] b)
    {
        var s = Encoding.Latin1.GetString(b)
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0");
        return s.Length > 70 ? s[..70] + "…" : s;
    }

    private static readonly string[] Curated =
    {
        // valid
        "GET /api/users?a=1&b=2 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n",
        "GET / HTTP/1.1\r\nHost: x\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n",
        "OPTIONS * HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET / HTTP/1.0\r\nHost: x\r\n\r\n",
        "GET /?flag HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /?a=1&&b=2&=v&c HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /p HTTP/1.1\r\nHost: x\r\nX-Trailing: val  \r\n\r\n",
        // incomplete
        "GET / HTTP/1.1\r\nHost: x\r\n",
        "",
        // structural
        "GET /  HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET / HTTP/2.0\r\nHost: x\r\n\r\n",
        "G(T / HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /\n/ HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /\x01 HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET / HTTP/1.1\r\nHost : x\r\n\r\n",
        "GET / HTTP/1.1\r\nHost: x\r\n more\r\n\r\n",
        "GET / HTTP/1.1\r\n: x\r\nHost: x\r\n\r\n",
        "GET / HTTP/1.1\r\nHostx\r\n\r\n",
        "GET / HTTP/1.1\r\nHost: x\r\nA: b\nc\r\n\r\n",
        // semantic
        "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5, 6\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 05\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: abc\r\n\r\n",
        "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip\r\n\r\n",
        "GET / HTTP/1.1\r\n\r\n",
        "GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n",
        "GET / HTTP/1.1\r\nHost: a/b\r\n\r\n",
        "CONNECT x:443 HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET * HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /a/../b HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /a\\b HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /%25 HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /%00 HTTP/1.1\r\nHost: x\r\n\r\n",
        "GET /a#b HTTP/1.1\r\nHost: x\r\n\r\n",
    };
}
