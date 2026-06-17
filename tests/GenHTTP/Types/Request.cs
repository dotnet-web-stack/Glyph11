using GenHTTP.Protocol;
using GenHTTP.Protocol.Raw;
using GenHTTP.Utils;
using Glyph11.Protocol;

namespace GenHTTP.Types;

public sealed class Request : IRequest
{
    private readonly RawRequest _raw = new();

    private RequestMethod? _method;

    public IRawRequest Raw => _raw;

    internal BinaryRequest Source => _raw.Source;

    public RequestMethod Method
    {
        get
        {
            if (_method == null)
            {
                var m = _raw.Method.Span;

                _method = m.Length switch
                {
                    3 when AsciiComparer.EqualsIgnoreCase(m, "GET"u8) => RequestMethod.Get,
                    4 when AsciiComparer.EqualsIgnoreCase(m, "POST"u8) => RequestMethod.Post,
                    3 when AsciiComparer.EqualsIgnoreCase(m, "PUT"u8) => RequestMethod.Put,
                    6 when AsciiComparer.EqualsIgnoreCase(m, "DELETE"u8) => RequestMethod.Delete,
                    4 when AsciiComparer.EqualsIgnoreCase(m, "HEAD"u8) => RequestMethod.Head,
                    7 when AsciiComparer.EqualsIgnoreCase(m, "OPTIONS"u8) => RequestMethod.Options,
                    5 when AsciiComparer.EqualsIgnoreCase(m, "PATCH"u8) => RequestMethod.Patch,
                    5 when AsciiComparer.EqualsIgnoreCase(m, "TRACE"u8) => RequestMethod.Trace,
                    7 when AsciiComparer.EqualsIgnoreCase(m, "CONNECT"u8) => RequestMethod.Connect,
                    _ => RequestMethod.Other
                };
            }

            return _method.Value;
        }
    }

    public void Reset()
    {
        _raw.Reset();

        _method = null;
    }

    public void Apply()
    {
        _raw.Apply();
    }

}
