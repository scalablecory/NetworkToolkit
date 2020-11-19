using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    internal sealed class Http1Request : HttpRequest
    {
        private Http1Connection _connection;
        private HttpPrimitiveVersion _version;

        public Http1Connection Connection => _connection;

        protected internal override EndPoint? LocalEndPoint => _connection._connection.LocalEndPoint;
        protected internal override EndPoint? RemoteEndPoint => _connection._connection.RemoteEndPoint;

        public Http1Request(Http1Connection connection, HttpPrimitiveVersion version)
        {
            _connection = connection;
            _version = version;
        }

        public void Init(Http1Connection connection, HttpPrimitiveVersion version)
        {
            Reset();
            _connection = connection;
            _version = version;
        }

        protected internal override ValueTask DisposeAsync(int version, CancellationToken cancellationToken)
        {
            Http1Connection connection = _connection;

            if (connection != null)
            {
                if (IsDisposed(version, out ValueTask task)) return task;

                _connection = null!;
                _version = null!;
                Volatile.Write(ref connection._request, this);
            }

            return default;
        }

        internal void SetCurrentReadType(HttpReadType readType)
        {
            ReadType = readType;
        }

        internal void SetCurrentResponseLine(Version version, HttpStatusCode statusCode)
        {
            Version = version;
            StatusCode = statusCode;
        }

        protected internal override void ConfigureRequest(int version, long? contentLength, bool hasTrailingHeaders)
        {
            ThrowIfDisposed(version);
            _connection.ConfigureRequest(contentLength, hasTrailingHeaders);
        }

        protected internal override void WriteConnectRequest(int version, ReadOnlySpan<byte> authority)
        {
            ThrowIfDisposed(version);
            _connection.WriteConnectRequest(authority, _version);
        }

        protected internal override void WriteRequest(int version, ReadOnlySpan<byte> method, ReadOnlySpan<byte> scheme, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery)
        {
            ThrowIfDisposed(version);
            _connection.WriteRequest(method, scheme, authority, pathAndQuery, _version);
        }

        protected internal override void WriteHeader(int version, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            ThrowIfDisposed(version);
            _connection.WriteHeader(name, value);
        }

        protected internal override void WriteHeader(int version, PreparedHeaderSet headers)
        {
            ThrowIfDisposed(version);
            _connection.WriteHeader(headers);
        }

        protected internal override void WriteTrailingHeader(int version, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            ThrowIfDisposed(version);
            _connection.WriteTrailingHeader(name, value);
        }

        protected internal override ValueTask FlushHeadersAsync(int version, CancellationToken cancellationToken)
        {
            if (IsDisposed(version, out ValueTask task)) return task;
            return _connection.FlushHeadersAsync(cancellationToken);
        }

        protected internal override ValueTask WriteContentAsync(int version, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask task)) return task;
            return _connection.WriteContentAsync(buffer, cancellationToken);
        }

        protected internal override ValueTask WriteContentAsync(int version, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask task)) return task;
            return _connection.WriteContentAsync(buffers, cancellationToken);
        }

        protected internal override ValueTask FlushContentAsync(int version, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask task)) return task;
            return _connection.FlushContentAsync(cancellationToken);
        }

        protected internal override ValueTask CompleteRequestAsync(int version, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask task)) return task;
            return _connection.CompleteRequestAsync(cancellationToken);
        }

        protected internal override ValueTask<HttpReadType> ReadAsync(int version, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask<HttpReadType> task)) return task;
            return _connection.ReadAsync(this, cancellationToken);
        }

        protected internal override ValueTask ReadHeadersAsync(int version, IHttpHeadersSink headersSink, object? state, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask task)) return task;
            return _connection.ReadHeadersAsync(this, headersSink, state, cancellationToken);
        }

        protected internal override ValueTask<int> ReadContentAsync(int version, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (IsDisposed(version, out ValueTask<int> task)) return task;
            return _connection.ReadContentAsync(buffer, cancellationToken);
        }
    }
}
