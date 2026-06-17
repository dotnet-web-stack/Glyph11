using GenHTTP.Types;

namespace GenHTTP.Context;

public class ClientContext
{
    public Request Request { get; set; } = null!;

    internal void Clear()
    {
        Request.Reset();
    }
}
