using System.Runtime.InteropServices;

namespace Glyph11.Diff;

[StructLayout(LayoutKind.Sequential)]
internal struct NSpan { public uint Off; public uint Len; }

[StructLayout(LayoutKind.Sequential)]
internal struct NField { public NSpan Name; public NSpan Value; }

[StructLayout(LayoutKind.Sequential)]
internal struct NLimits
{
    public uint StructSize, MaxHeaderCount, MaxHeaderNameLen, MaxHeaderValueLen,
                MaxUrlLen, MaxQueryParamCount, MaxMethodLen, MaxTotalHeaderBytes;

    public static NLimits Default() => new()
    {
        StructSize          = (uint)Marshal.SizeOf<NLimits>(),
        MaxHeaderCount      = 100,
        MaxHeaderNameLen    = 256,
        MaxHeaderValueLen   = 8192,
        MaxUrlLen           = 8192,
        MaxQueryParamCount  = 128,
        MaxMethodLen        = 16,
        MaxTotalHeaderBytes = 1048576,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NRequest
{
    public NSpan Method, Target, Path, Version;
    public NField* Headers; public uint HeaderCap; public uint HeaderCount;
    public NField* Query;   public uint QueryCap;  public uint QueryCount;
}

internal static unsafe class Native
{
    public const int OK = 0, INCOMPLETE = 1;

    [DllImport("glyph11", CallingConvention = CallingConvention.Cdecl)]
    public static extern int glyph11_parse_request(
        byte* buf, nuint len, NLimits* limits, NRequest* req, nuint* consumed);

    [DllImport("glyph11", CallingConvention = CallingConvention.Cdecl)]
    public static extern int glyph11_status_http_code(int status);
}
