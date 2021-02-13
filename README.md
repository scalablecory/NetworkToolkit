# NetworkToolkit

This project contains networking primitives for use with .NET. It can be obtained from NuGet. [![Nuget](https://img.shields.io/nuget/v/NetworkToolkit)](https://www.nuget.org/packages/NetworkToolkit)

This is a bit of a prototyping playground right now and APIs will not be perfectly stable.

## HTTP Primitives

NetworkToolkit provides a lower-level HTTP client that prioritizes performance and flexibility. Once warmed up, it processes requests using zero allocations.

### Performance

In the great majority of cases, `HttpClient` will be fast enough. Generally I/O will dominate a request's total time. However, if peak perf is needed, this library will be faster, especially as the number of headers in the request/response increase.

Benchmarks are incomplete, so this should not be viewed as an exhaustive comparison:

|            Method | RequestHeaders | ResponseBytes |      Mean |     Error |    StdDev |    Median | Ratio | RatioSD |
|------------------ |--------------- |-------------- |----------:|----------:|----------:|----------:|------:|--------:|
|         Primitive |        Minimal |       Minimal | 10.806 us | 0.3304 us | 0.9741 us | 10.985 us |  0.51 |    0.05 |
| PrimitivePrepared |        Minimal |       Minimal |  9.278 us | 0.2091 us | 0.6066 us |  9.298 us |  0.44 |    0.04 |
|    SocketsHandler |        Minimal |       Minimal | 21.323 us | 0.4380 us | 1.2136 us | 21.442 us |  1.00 |    0.00 |
|                   |                |               |           |           |           |           |       |         |
|         Primitive |        Minimal | StackOverflow | 13.665 us | 0.6509 us | 1.9089 us | 13.187 us |  0.16 |    0.02 |
| PrimitivePrepared |        Minimal | StackOverflow | 14.108 us | 0.6328 us | 1.8559 us | 13.432 us |  0.17 |    0.03 |
|    SocketsHandler |        Minimal | StackOverflow | 86.356 us | 1.7149 us | 4.2707 us | 87.476 us |  1.00 |    0.00 |
|                   |                |               |           |           |           |           |       |         |
|         Primitive |         Normal |       Minimal | 11.053 us | 0.2498 us | 0.7366 us | 11.149 us |  0.32 |    0.03 |
| PrimitivePrepared |         Normal |       Minimal | 10.636 us | 0.2867 us | 0.8455 us | 10.701 us |  0.31 |    0.03 |
|    SocketsHandler |         Normal |       Minimal | 34.775 us | 0.6940 us | 1.9231 us | 35.172 us |  1.00 |    0.00 |
|                   |                |               |           |           |           |           |       |         |
|         Primitive |         Normal | StackOverflow | 15.080 us | 0.6158 us | 1.7866 us | 14.874 us |  0.16 |    0.03 |
| PrimitivePrepared |         Normal | StackOverflow | 13.964 us | 0.5963 us | 1.7490 us | 13.257 us |  0.15 |    0.02 |
|    SocketsHandler |         Normal | StackOverflow | 94.221 us | 2.4801 us | 7.3127 us | 96.337 us |  1.00 |    0.00 |

### A simple GET request using exactly one HTTP/1 connection, no pooling

Avoid a connection pool to have exact control over connections.

```c#
await using ConnectionFactory connectionFactory = new SocketConnectionFactory();
await using Connection connection = await connectionFactory.ConnectAsync(new DnsEndPoint("microsoft.com", 80));
await using HttpConnection httpConnection = new Http1Connection(connection);
await using ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact)).Value;

request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
request.WriteRequest(HttpMethod.Get, new Uri("http://microsoft.com"));
request.WriteHeader("Accept", "text/html");
await request.CompleteRequestAsync();

await request.ReadToFinalResponseAsync();
Console.WriteLine($"Got response {request.StatusCode}.");

if(await request.ReadToHeadersAsync())
{
    await request.ReadHeadersAsync(...);
}

if(await request.ReadToContentAsync())
{
    int len;
    byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

    do
    {
        while((len = await request.ReadContentAsync(buffer)) != 0)
        {
            ForwardData(buffer[..len]);
        }
    }
    while(await request.ReadToNextContentAsync());

    ArrayPool<byte>.Shared.Return(buffer);
}

await request.DrainAsync();
```

### Using the opt-in connection pool

A single-host connection pool handles concurrent HTTP/1 requests, H2C upgrade, ALPN negotiation for HTTP/2, etc.

```c#
await using ConnectionFactory connectionFactory = new SocketConnectionFactory();
await using HttpConnection httpConnection = new PooledHttpConnection(connectionFactory, new DnsEndPoint("microsoft.com", 80), sslTargetHost: null);
await using ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact)).Value;
```

### Optimize frequently-used headers

Prepare frequently-used headers to reduce CPU costs by pre-validating and caching protocol encoding. In the future, this will light up dynamic table compression in HTTP/2 and HTTP/3.

```c#
PreparedHeaderSet preparedHeaders = new PreparedHeaderSet()
{
    { "User-Agent", "NetworkToolkit" },
    { "Accept", "text/html" }
};

await using ValueHttpRequest request = ...;

request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
request.WriteRequest(HttpMethod.Get, new Uri("http://microsoft.com"));
request.WriteHeader(preparedHeaders);
```

### Avoiding strings

Avoid `string` and `Uri` allocations, optimize away some related processing, and get tight control over encoding by passing in `ReadOnlySpan<byte>`:

```c#
ReadOnlySpan<byte> method = HttpRequest.GetMethod; // "GET"
ReadOnlySpan<byte> authority = ...; // "microsoft.com:80"
ReadOnlySpan<byte> pathAndQuery = ...; // "/"

ReadOnlySpan<byte> headerName = ...; // "Accept"
ReadOnlySpan<byte> headerValue = ...; // "text/html"

await using ValueHttpRequest request = ...;
request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
request.WriteRequest(method, authority, pathAndQuery);
request.WriteHeader(headerName, headerValue);
```

### Advanced processing

Access extensions like HTTP/2 ALT-SVC frames, or efficiently forward requests (reverse proxy) by processing requests as a loosely structured stream:

```c#
await using ValueHttpRequest request = ...;
while(await request.ReadAsync() != HttpReadType.EndOfStream)
{
    switch(request.ReadType)
    {
    case HttpReadType.InformationalResponse:
        ProcessInformationalResponse(request.StatusCode, request.Version);
        break;
    case HttpReadType.FinalResponse:
        ProcessResponse(request.StatusCode, request.Version);
        break;
    case HttpReadType.Headers:
        await request.ReadHeadersAsync(...);
        break;
    case HttpReadType.Content:
        int len;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

        while((len = await request.ReadContentAsync(buffer)) != 0)
        {
            ForwardData(buffer[..len]);
        }

        ArrayPool<byte>.Shared.Return(buffer);
        break;
    case HttpReadType.TrailingHeaders:
        await request.ReadHeadersAsync(...);
        break;
    case HttpReadType.AltSvc:
        ProcessAltSvc(request.AltSvc);
        break;
    }
}
```

## Connection Abstractions

Connection abstractions used to abstract establishment of `Stream`-based connections.