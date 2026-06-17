namespace GenHTTP.Protocol.Raw;

public interface IRawRequestTarget
{

    ReadOnlyMemory<byte>? Current { get; }

    void Advance(int segments = 1);

}
