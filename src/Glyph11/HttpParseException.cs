namespace Glyph11;

/// <summary>
/// Thrown when an HTTP/1.1 request violates protocol rules during parsing or semantic validation.
/// </summary>
public class HttpParseException : Exception
{
    /// <param name="message">A description of the protocol violation.</param>
    /// <param name="statusCode">HTTP status code to return (default 400).</param>
    public HttpParseException(string message, int statusCode = 400) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// The HTTP status code to return: 400 for structural or semantic errors, 431 for limit
    /// breaches, 505 for a well-formed HTTP version the parser does not implement.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>True when the error is a size/count limit breach (→ 431); false otherwise (400, 505).</summary>
    public bool IsLimitViolation => StatusCode == 431;
}
