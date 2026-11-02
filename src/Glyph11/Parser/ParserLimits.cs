namespace Glyph11.Parser;

/// <summary>
/// Configurable resource limits.
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

    /// <summary>
    /// Maximum length of a response's reason phrase in bytes (default 512). Responses only; a
    /// request has no equivalent field.
    /// </summary>
    public int MaxReasonPhraseLength { get; init; }

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
        MaxReasonPhraseLength = 512,
        MaxTotalHeaderBytes = 1_048_576
    };

    // ---- Leniencies (all off by default) ----

    /// <summary>
    /// Accept messages whose lines end in a bare LF instead of CRLF (default <c>false</c>).
    /// <para>
    /// RFC 9112 §2.2 lets a recipient recognize a single LF as a line terminator. It is accepted
    /// only uniformly: the message's first line terminator decides its style, a bare-LF message
    /// must use bare LF throughout (its header section ends at the first empty line), and a
    /// message that mixes CRLF and bare LF is still rejected — mixing is where two parsers
    /// disagree about where a line ended. A message that starts with CRLF gets the strict rules.
    /// </para>
    /// <para>
    /// Enable it for a client reading responses from an origin it chose, with no intermediary in
    /// between. Never enable it on a server in front of or behind other HTTP implementations:
    /// bare LF is a request-smuggling primitive precisely because implementations disagree on it.
    /// </para>
    /// </summary>
    public bool AllowBareLf { get; init; }
}
