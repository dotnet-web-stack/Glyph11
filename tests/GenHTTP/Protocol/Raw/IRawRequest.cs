namespace GenHTTP.Protocol.Raw;

public interface IRawRequest
{

    ReadOnlyMemory<byte> Method { get; }

    ReadOnlyMemory<byte> Path { get; }

    IRawRequestTarget Target { get; }

    IRawKeyValueList Query { get; }

    ReadOnlyMemory<byte> Version { get; }

    IRawKeyValueList Headers { get; }

}
