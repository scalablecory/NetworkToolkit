using System;

namespace NetworkToolkit.Tests.Http.Servers
{
    internal record HttpTestRequest(
        string Method,
        string PathAndQuery,
        Version Version,
        TestHeadersSink Headers);

    internal record HttpTestFullRequest(
        string Method,
        string PathAndQuery,
        Version Version,
        TestHeadersSink Headers,
        string Content,
        TestHeadersSink TrailingHeaders);
}
