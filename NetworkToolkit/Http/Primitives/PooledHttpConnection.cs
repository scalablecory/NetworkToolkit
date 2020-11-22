using NetworkToolkit.Connections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A pooled connection.
    /// </summary>
    public sealed class PooledHttpConnection : HttpConnection
    {
        private static readonly List<SslApplicationProtocol> s_http1Alpn = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 };

        private readonly ConnectionFactory _connectionFactory;
        private readonly DnsEndPointWithProperties _endPoint;
        private readonly object _sync = new object();

        private PooledHttp1Connection? _http1Head;
        private PooledHttp1Connection? _http1Tail;

        private PooledHttpRequest? _requestCacheHead;
        private PooledHttpRequest? _requestCacheTail;

        /// <summary>
        /// The maximum lifetime of a pooled connection.
        /// By default, <see cref="Timeout.InfiniteTimeSpan"/> is used to indicate no maximum lifetime.
        /// </summary>
        public TimeSpan PooledConnectionLifetimeLimit { get; init; } = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// The maximum idle time of a pooled connection.
        /// By default, <see cref="Timeout.InfiniteTimeSpan"/> is used to indicate no idle limit.
        /// </summary>
        public TimeSpan PooledConnectionIdleLimit { get; init; } = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// The maximum number of bytes to drain from a request.
        /// Once elapsed, the request (and possibly connection) will be immediately terminated.
        /// </summary>
        public int MaximumDrainBytes { get; init; } = int.MaxValue;

        /// <inheritdoc/>
        public override HttpConnectionStatus Status => HttpConnectionStatus.Open;

        /// <summary>
        /// Instantiates a new <see cref="PooledHttpConnection"/>.
        /// </summary>
        /// <param name="connectionFactory">A connection factory used to establish connections for HTTP/1 and HTTP/2.</param>
        /// <param name="host">The host being connected to.</param>
        /// <param name="port">The port being connected to.</param>
        /// <param name="sslTargetHost">The target host of SSL connections, sent via SNI.</param>
        public PooledHttpConnection(ConnectionFactory connectionFactory, string host, int port, string? sslTargetHost)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

            SslClientAuthenticationOptions? sslOptions = null;

            if (sslTargetHost != null)
            {
                sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = sslTargetHost,
                    ApplicationProtocols = s_http1Alpn
                };
            }

            _endPoint = new DnsEndPointWithProperties(host, port, sslOptions);
        }

        /// <inheritdoc/>
        public override ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public override async ValueTask<ValueHttpRequest?> CreateNewRequestAsync(HttpPrimitiveVersion version, HttpVersionPolicy versionPolicy, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                PooledHttp1Connection? connection;

                while (true)
                {
                    connection = PopHttp1ConnectionFromTail();
                    if (connection != null)
                    {
                        if (connection.IsExpired(Environment.TickCount64, PooledConnectionLifetimeLimit, PooledConnectionIdleLimit))
                        {
                            await connection.DisposeAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                    else
                    {
                        Connection con = await _connectionFactory.ConnectAsync(_endPoint, _endPoint, cancellationToken).ConfigureAwait(false);
                        connection = new PooledHttp1Connection(con, version);
                    }
                    break;
                }

                ValueHttpRequest? request;
                try
                {
                    request = await connection.CreateNewRequestAsync(version, versionPolicy, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await connection.DisposeAsync(cancellationToken).ConfigureAwait(false);
                    throw;
                }

                if (request != null)
                {
                    PooledHttpRequest? pooledRequest = PopCachedRequestFromTail();
                    if (pooledRequest != null)
                    {
                        pooledRequest.Init(request.GetValueOrDefault(), this);
                    }
                    else
                    {
                        pooledRequest = new PooledHttpRequest(request.GetValueOrDefault(), this);
                    }

                    return pooledRequest.GetValueRequest();
                }
                else
                {
                    await connection.DisposeAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private PooledHttp1Connection? PopHttp1ConnectionFromTail()
        {
            PooledHttp1Connection? connection;

            lock (_sync)
            {
                connection = _http1Tail;
                if (connection == null)
                {
                    return null;
                }

                _http1Tail = connection._prev;
                if (_http1Tail != null)
                {
                    _http1Tail._next = null;
                }
                else
                {
                    _http1Head = null;
                    _http1Tail = null;
                }
            }

            return connection;
        }

        private PooledHttp1Connection? PopExpiredHttp1ConnectionFromHead(long curTicks, TimeSpan lifetimeLimit, TimeSpan idleLimit)
        {
            PooledHttp1Connection? first;

            lock (_sync)
            {
                first = _http1Head;

                if (first != null && first.IsExpired(curTicks, lifetimeLimit, idleLimit))
                {
                    PooledHttp1Connection? last = first;

                    while (last._next is PooledHttp1Connection next && next.IsExpired(curTicks, lifetimeLimit, idleLimit))
                    {
                        last = next;
                    }

                    _http1Head = last._next;
                    if (_http1Head != null)
                    {
                        _http1Head._prev = null;
                    }
                    else
                    {
                        _http1Tail = null;
                    }

                    last._next = null;
                }
            }

            return first;
        }

        private void PushHttp1Connection(PooledHttp1Connection connection)
        {
            Debug.Assert(connection._prev == null);
            Debug.Assert(connection._next == null);

            lock (_sync)
            {
                if (_http1Tail is PooledHttp1Connection tail)
                {
                    tail._next = connection;
                    connection._prev = tail;
                    _http1Tail = connection;
                }
                else
                {
                    _http1Head = connection;
                    _http1Tail = connection;
                }
            }
        }

        private PooledHttpRequest? PopCachedRequestFromTail()
        {
            PooledHttpRequest? request;

            lock (_sync)
            {
                request = _requestCacheTail;
                if (request != null)
                {
                    _requestCacheTail = request._prev;
                    if (_requestCacheTail != null)
                    {
                        _requestCacheTail._next = null;
                        request._prev = null;
                    }
                    else
                    {
                        _requestCacheHead = null;
                        _requestCacheTail = null;
                    }

                }
            }

            return request;
        }

        private void PushCachedRequest(PooledHttpRequest request)
        {
            Debug.Assert(request._prev == null);
            Debug.Assert(request._next == null);

            request._lastUsedTicks = Environment.TickCount64;

            lock (_sync)
            {
                if (_requestCacheTail is PooledHttpRequest tail)
                {
                    tail._next = request;
                    request._prev = tail;
                    _requestCacheTail = request;
                }
                else
                {
                    _requestCacheHead = request;
                    _requestCacheTail = request;
                }
            }
        }

        /// <inheritdoc/>
        public override async ValueTask PrunePoolsAsync(long curTicks, TimeSpan lifetimeLimit, TimeSpan idleLimit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (lifetimeLimit == Timeout.InfiniteTimeSpan && idleLimit == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            List<ValueTask>? tasks = null;
            List<Exception>? exceptions = null;

            PooledHttp1Connection? connection = PopExpiredHttp1ConnectionFromHead(curTicks, lifetimeLimit, idleLimit);
            while (connection != null)
            {
                PooledHttp1Connection? next = connection._next;
                connection._next = null;
                connection._prev = null;

                try
                {
                    ValueTask task = connection.DisposeAsync(cancellationToken);
                    if (task.IsCompleted) task.GetAwaiter().GetResult();
                    else (tasks ??= new List<ValueTask>()).Add(task);
                }
                catch (Exception ex)
                {
                    (exceptions ??= new List<Exception>()).Add(ex);
                }

                connection = next;
            }

            // TODO: also prune cached requests.

            if (tasks != null)
            {
                foreach (ValueTask task in tasks)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        (exceptions ??= new List<Exception>()).Add(ex);
                    }
                }
            }

            if (exceptions != null)
            {
                throw new AggregateException(exceptions);
            }
        }

        private sealed class PooledHttp1Connection : Http1Connection
        {
            internal PooledHttp1Connection? _prev, _next;

            public PooledHttp1Connection(Connection connection, HttpPrimitiveVersion version) : base(connection, version)
            {
            }
        }

        private sealed class PooledHttpRequest : HttpRequest
        {
            private ValueHttpRequest _request;
            private PooledHttpConnection? _owningConnection;
            internal PooledHttpRequest? _prev, _next;
            internal long _lastUsedTicks;

            protected internal override EndPoint? LocalEndPoint => _request.LocalEndPoint;
            protected internal override EndPoint? RemoteEndPoint => _request.RemoteEndPoint;

            public PooledHttpRequest(ValueHttpRequest request, PooledHttpConnection owningConnection)
            {
                _request = request;
                _owningConnection = owningConnection;
            }

            public void Init(ValueHttpRequest request, PooledHttpConnection owningConnection)
            {
                _request = request;
                _owningConnection = owningConnection;
                Reset();
            }

            protected internal override ValueTask CompleteRequestAsync(int version, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask task)) return task;
                return _request.CompleteRequestAsync(cancellationToken);
            }

            protected internal override void ConfigureRequest(int version, long? contentLength, bool hasTrailingHeaders)
            {
                ThrowIfDisposed(version);
                _request.ConfigureRequest(contentLength, hasTrailingHeaders);
            }

            protected internal override async ValueTask DisposeAsync(int version, CancellationToken cancellationToken)
            {
                if (_owningConnection is PooledHttpConnection owningConnection)
                {
                    ThrowIfDisposed(version);

                    PooledHttp1Connection pooledConnection = (PooledHttp1Connection)((Http1Request)_request.Request).Connection;

                    bool disposeConnection = false;

                    try
                    {
                        await _request.DrainAsync(owningConnection.MaximumDrainBytes, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        disposeConnection = true;
                    }

                    await _request.DisposeAsync(cancellationToken).ConfigureAwait(false);
                    _owningConnection = null;

                    owningConnection.PushCachedRequest(this);

                    if (!disposeConnection && !pooledConnection.IsExpired(Environment.TickCount64, owningConnection.PooledConnectionLifetimeLimit, owningConnection.PooledConnectionIdleLimit))
                    {
                        owningConnection.PushHttp1Connection(pooledConnection);
                    }
                    else
                    {
                        await pooledConnection.DisposeAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            protected internal override ValueTask FlushContentAsync(int version, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask task)) return task;
                return _request.FlushContentAsync(cancellationToken);
            }

            protected internal override ValueTask FlushHeadersAsync(int version, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask task)) return task;
                return _request.FlushHeadersAsync(cancellationToken);
            }

            protected internal override ValueTask<HttpReadType> ReadAsync(int version, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask<HttpReadType> task)) return task;
                return _request.ReadAsync(cancellationToken);
            }

            protected internal override ValueTask<int> ReadContentAsync(int version, Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask<int> task)) return task;
                return _request.ReadContentAsync(buffer, cancellationToken);
            }

            protected internal override ValueTask ReadHeadersAsync(int version, IHttpHeadersSink headersSink, object? state, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask task)) return task;
                return _request.ReadHeadersAsync(headersSink, state, cancellationToken);
            }

            protected internal override void WriteConnectRequest(int version, ReadOnlySpan<byte> authority)
            {
                ThrowIfDisposed(version);
                _request.WriteConnectRequest(authority);
            }

            protected internal override ValueTask WriteContentAsync(int version, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask task)) return task;
                return _request.WriteContentAsync(buffer, cancellationToken);
            }

            protected internal override ValueTask WriteContentAsync(int version, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
            {
                if (IsDisposed(version, out ValueTask task)) return task;
                return _request.WriteContentAsync(buffers, cancellationToken);
            }

            protected internal override void WriteHeader(int version, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
            {
                ThrowIfDisposed(version);
                _request.WriteHeader(name, value);
            }

            protected internal override void WriteHeader(int version, PreparedHeaderSet headers)
            {
                ThrowIfDisposed(version);
                _request.WriteHeader(headers);
            }

            protected internal override void WriteHeader(int version, string name, string value)
            {
                ThrowIfDisposed(version);
                _request.WriteHeader(name, value);
            }

            protected internal override void WriteHeader(int version, string name, IEnumerable<string> values, string separator)
            {
                ThrowIfDisposed(version);
                _request.WriteHeader(name, values, separator);
            }

            protected internal override void WriteTrailingHeader(int version, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
            {
                ThrowIfDisposed(version);
                _request.WriteTrailingHeader(name, value);
            }

            protected internal override void WriteTrailingHeader(int version, string name, string value)
            {
                ThrowIfDisposed(version);
                _request.WriteTrailingHeader(name, value);
            }

            protected internal override void WriteTrailingHeader(int version, string name, IEnumerable<string> values, string separator)
            {
                ThrowIfDisposed(version);
                _request.WriteTrailingHeader(name, values, separator);
            }

            protected internal override void WriteRequest(int version, ReadOnlySpan<byte> method, ReadOnlySpan<byte> scheme, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery)
            {
                ThrowIfDisposed(version);
                _request.WriteRequest(method, scheme, authority, pathAndQuery);
            }

            protected internal override void WriteRequest(int version, HttpMethod method, Uri uri)
            {
                ThrowIfDisposed(version);
                _request.WriteRequest(method, uri);
            }
        }
    }
}
