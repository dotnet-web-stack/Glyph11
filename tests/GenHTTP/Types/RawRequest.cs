using GenHTTP.Protocol.Raw;
using Glyph11.Protocol;

namespace GenHTTP.Types;

public sealed class RawRequest : IRawRequest
{
    private readonly BinaryRequest _source;

    private readonly RawKeyValueList _headers;

    private readonly RawKeyValueList _query;

    private readonly RawRequestTarget _target;

    internal BinaryRequest Source => _source;

    public ReadOnlyMemory<byte> Method => _source.Method;

    public ReadOnlyMemory<byte> Path => _source.Path;

    public IRawRequestTarget Target => _target;

    public ReadOnlyMemory<byte> Version => _source.Version;

    public IRawKeyValueList Headers => _headers;

    public IRawKeyValueList Query => _query;

    public ReadOnlyMemory<byte> Body { get; set; }

    public RawRequest()
    {
        _source = new();

        _headers = new(_source.Headers);
        _query = new(_source.QueryParameters);

        _target = new();
    }

    public void Reset()
    {
        _source.Clear();
    }

    public void Apply()
    {
        _target.Apply(Path);
    }

}
