using GenHTTP.Protocol.Raw;

namespace GenHTTP.Protocol;

public interface IRequest
{

    IRawRequest Raw { get; }

    RequestMethod Method { get; }

}
