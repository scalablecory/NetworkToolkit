# NetworkToolkit

This project contains networking primitives for use with .NET. It can be obtained from [NuGet](https://www.nuget.org/packages/NetworkToolkit/).

This is a bit of a prototyping playground right now and APIs may not be stable.

## HTTP Primitives

NetworkToolkit provides a lower-level HTTP client that prioritizes performance and flexibility. Once warmed up, it processes requests using zero allocations.

### Performance

In the great majority of cases, `HttpClient` will be fast enough. Generally I/O will dominate a request's total time. However, if peak perf is needed, this library will be faster, especially as the number of headers in the request/response increase.

Benchmarks are incomplete, so this should not be viewed as an exhaustive comparison:

|            Method | RequestHeaders | ResponseBytes |       Mean |     Error |     StdDev |     Median | Ratio | RatioSD |
|------------------ |--------------- |-------------- |-----------:|----------:|-----------:|-----------:|------:|--------:|
|         Primitive |        Minimal |       Minimal |   6.743 us | 0.1270 us |  0.1651 us |   6.746 us |  0.29 |    0.04 |
| PrimitivePrepared |        Minimal |       Minimal |   9.833 us | 0.3539 us |  1.0434 us |   9.903 us |  0.42 |    0.07 |
|    SocketsHandler |        Minimal |       Minimal |  23.676 us | 0.9795 us |  2.7946 us |  23.330 us |  1.00 |    0.00 |
|                   |                |               |            |           |            |            |       |         |
|         Primitive |        Minimal | StackOverflow |  14.772 us | 0.6929 us |  2.0430 us |  14.493 us |  0.16 |    0.03 |
| PrimitivePrepared |        Minimal | StackOverflow |  13.070 us | 0.4057 us |  1.1961 us |  13.022 us |  0.14 |    0.02 |
|    SocketsHandler |        Minimal | StackOverflow |  92.568 us | 2.5592 us |  7.5458 us |  94.691 us |  1.00 |    0.00 |
|                   |                |               |            |           |            |            |       |         |
|         Primitive |         Normal |       Minimal |  11.128 us | 0.2688 us |  0.7925 us |  11.174 us |  0.29 |    0.04 |
| PrimitivePrepared |         Normal |       Minimal |  10.643 us | 0.3478 us |  1.0256 us |  10.717 us |  0.28 |    0.05 |
|    SocketsHandler |         Normal |       Minimal |  38.998 us | 1.5401 us |  4.5411 us |  40.723 us |  1.00 |    0.00 |
|                   |                |               |            |           |            |            |       |         |
|         Primitive |         Normal | StackOverflow |  15.768 us | 0.5396 us |  1.5655 us |  15.549 us |  0.15 |    0.03 |
| PrimitivePrepared |         Normal | StackOverflow |  14.192 us | 0.7050 us |  2.0452 us |  13.880 us |  0.14 |    0.03 |
|    SocketsHandler |         Normal | StackOverflow | 104.513 us | 4.9165 us | 14.4964 us | 109.489 us |  1.00 |    0.00 |

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
PreparedHeaderSet preparedHeaders =
    new PreparedHeaderSetBuilder()
    .AddHeader("User-Agent", "NetworkToolkit")
    .AddHeader("Accept", "text/html")
    .Build();

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
    case HttpReadType.Request:
        ProcessRequest(request.StatusCode, request.Version);
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