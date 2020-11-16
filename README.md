# NetworkToolkit

This project contains networking primitives for use with .NET. It can be obtained from [NuGet](https://www.nuget.org/packages/NetworkToolkit/).

This is a bit of a prototyping playground right now and APIs may not be stable.

## HTTP Primitives

NetworkToolkit provides a lower-level HTTP client that prioritizes performance and flexibility. Once warmed up, it processes requests using zero allocations.

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

await request.ReadToLastResponseAsync();
Console.WriteLine($"Got response {request.StatusCode}.");
```

### Using the opt-in connection pool

A single-host connection pool handles concurrent HTTP/1 requests, H2C upgrade, ALPN negotiation for HTTP/2, etc.

```c#
await using ConnectionFactory connectionFactory = new SocketConnectionFactory();
await using HttpConnection httpConnection = new PooledHttpConnection(connectionFactory, "microsoft.com", 80, sslTargetHost: null);
await using ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact)).Value;
```

### Optimize frequently-used headers

Preparing headers allows pre-validation and cached protocol encoding. In the future, this will light up dynamic table compression in HTTP/2 and HTTP/3.

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

Avoid `string` and `Uri` allocations and get tight control over encoding by passing in `ReadOnlySpan<byte>`:

```c#
ReadOnlySpan<byte> method = HttpRequest.GetMethod; // "GET"
ReadOnlySpan<byte> scheme = ...; // "https"
ReadOnlySpan<byte> authority = ...; // "microsoft.com:80"
ReadOnlySpan<byte> pathAndQuery = ...; // "/"

ReadOnlySpan<byte> headerName = ...; // "Accept"
ReadOnlySpan<byte> headerValue = ...; // "text/html"

await using ValueHttpRequest request = ...;
request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
request.WriteRequest(method, scheme, authority, pathAndQuery);
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