using Glyph11.Parser.UltraHardened;

namespace Glyph11.Parser.Hardened;

/// <summary>
/// Configurable resource limits for <see cref="UltraHardened.HardenedParser"/>.
/// Every field has a safe default via <see cref="Default"/>. Customize with <c>with</c> expressions:
/// <code>
/// var strict = ParserLimits.Default with { MaxHeaderCount = 50 };
/// </code>
/// </summary>
public readonly record struct ParserLimits
{
    /// <summary>Maximum number of header fields allowed (default 100).</summary>
    public int MaxHeaderCount { get; init; }

    /// <summary>Maximum length of a single header name in bytes (default 256).</summary>
    public int MaxHeaderNameLength { get; init; }

    /// <summary>Maximum length of a single header value in bytes (default 8192).</summary>
    public int MaxHeaderValueLength { get; init; }

    /// <summary>Maximum length of the request URL in bytes (default 8192).</summary>
    public int MaxUrlLength { get; init; }

    /// <summary>Maximum number of query string parameters (default 128).</summary>
    public int MaxQueryParameterCount { get; init; }

    /// <summary>Maximum length of the HTTP method token in bytes (default 16).</summary>
    public int MaxMethodLength { get; init; }

    /// <summary>Maximum total size of the header block including request line and terminators (default 1048576).</summary>
    public int MaxTotalHeaderBytes { get; init; }

    /// <summary>Returns a <see cref="ParserLimits"/> with safe production defaults.</summary>
    public static ParserLimits Default => new()
    {
        MaxHeaderCount = 100,
        MaxHeaderNameLength = 256,
        MaxHeaderValueLength = 8192,
        MaxUrlLength = 8192,
        MaxQueryParameterCount = 128,
        MaxMethodLength = 16,
        MaxTotalHeaderBytes = 1_048_576
    };
}
