using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glyph11.Native;

/// <summary>A byte range (offset + length) into the parsed input buffer (zero-copy).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Glyph11Span
{
    public uint Offset;
    public uint Length;
}

/// <summary>A parsed name/value pair (header or query parameter).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Glyph11Field
{
    public Glyph11Span Name;
    public Glyph11Span Value;
}

/// <summary>Resource limits; mirrors the C <c>glyph11_limits</c> struct.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Glyph11Limits
{
    public uint StructSize;
    public uint MaxHeaderCount;
    public uint MaxHeaderNameLength;
    public uint MaxHeaderValueLength;
    public uint MaxUrlLength;
    public uint MaxQueryParameterCount;
    public uint MaxMethodLength;
    public uint MaxTotalHeaderBytes;

    public static unsafe Glyph11Limits Default => new()
    {
        StructSize             = (uint)sizeof(Glyph11Limits),
        MaxHeaderCount         = 100,
        MaxHeaderNameLength    = 256,
        MaxHeaderValueLength   = 8192,
        MaxUrlLength           = 8192,
        MaxQueryParameterCount = 128,
        MaxMethodLength        = 16,
        MaxTotalHeaderBytes    = 1048576,
    };
}

/// <summary>Parsed request fields (spans into the input). Headers/query live in caller storage.</summary>
public struct Glyph11Result
{
    public Glyph11Span Method;
    public Glyph11Span Target;
    public Glyph11Span Path;
    public Glyph11Span Version;
    public int HeaderCount;
    public int QueryCount;
    public long Consumed;
}

/// <summary>Managed wrapper over the Glyph11 C hardened parser (<c>libglyph11</c>).</summary>
public static class Glyph11Parser
{
    public const int Ok = 0;
    public const int Incomplete = 1;

    /// <summary>
    /// Parse one HTTP/1.1 request header block. <paramref name="headers"/> and
    /// <paramref name="query"/> are caller-provided storage the parser fills.
    /// Returns the C <c>glyph11_status</c> (0 = OK, 1 = incomplete, else an error).
    /// </summary>
    public static unsafe int Parse(
        ReadOnlySpan<byte> input,
        Span<Glyph11Field> headers,
        Span<Glyph11Field> query,
        in Glyph11Limits limits,
        out Glyph11Result result)
    {
        result = default;
        Glyph11Limits lim = limits; // local so we can take its address
        fixed (byte* pin = input)
        fixed (Glyph11Field* ph = headers)
        fixed (Glyph11Field* pq = query)
        {
            NRequest req;
            req.Method = req.Target = req.Path = req.Version = default;
            req.Headers = ph; req.HeaderCap = (uint)headers.Length; req.HeaderCount = 0;
            req.Query   = pq; req.QueryCap   = (uint)query.Length;   req.QueryCount   = 0;

            nuint consumed;
            int st = glyph11_parse_request(pin, (nuint)input.Length, &lim, &req, &consumed);
            if (st == Ok)
            {
                result.Method  = req.Method;
                result.Target  = req.Target;
                result.Path    = req.Path;
                result.Version = req.Version;
                result.HeaderCount = (int)req.HeaderCount;
                result.QueryCount  = (int)req.QueryCount;
                result.Consumed    = (long)consumed;
            }
            return st;
        }
    }

    /// <summary>Parse with the default resource limits.</summary>
    public static int Parse(ReadOnlySpan<byte> input, Span<Glyph11Field> headers,
                            Span<Glyph11Field> query, out Glyph11Result result)
        => Parse(input, headers, query, Glyph11Limits.Default, out result);

    /// <summary>HTTP response code for a status (400 / 431, or 0 for OK / incomplete).</summary>
    public static int HttpCode(int status) => glyph11_status_http_code(status);

    /// <summary>Packed ABI version of the loaded native library.</summary>
    public static uint AbiVersion() => glyph11_abi_version();

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NRequest
    {
        public Glyph11Span Method, Target, Path, Version;
        public Glyph11Field* Headers; public uint HeaderCap; public uint HeaderCount;
        public Glyph11Field* Query;   public uint QueryCap;  public uint QueryCount;
    }

    private const string Lib = "glyph11";

    [SuppressGCTransition]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int glyph11_parse_request(
        byte* buf, nuint len, Glyph11Limits* limits, NRequest* req, nuint* consumed);

    [SuppressGCTransition]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int glyph11_status_http_code(int status);

    [SuppressGCTransition]
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint glyph11_abi_version();
}
