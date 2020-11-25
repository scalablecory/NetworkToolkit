using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NetworkToolkit.Http.Primitives
{
    internal sealed class Http1Request : HttpRequest, IIntrusiveLinkedListNode<Http1Request>
    {
        private readonly ResettableValueTaskSource<ValueHttpRequest?> _writeWaitTaskSource = new();
        private readonly ResettableValueTaskSource<int> _readWaitTaskSource = new();
        private volatile bool _waitForRead = false;

        private Http1Connection _connection;

        private IntrusiveLinkedNodeHeader<Http1Request> _listHeader;
        public ref IntrusiveLinkedNodeHeader<Http1Request> ListHeader => ref _listHeader;

        public Http1Connection Connection => _connection;

        protected internal override EndPoint? LocalEndPoint => _connection._connection.LocalEndPoint;
        protected internal override EndPoint? RemoteEndPoint => _connection._connection.RemoteEndPoint;

        public Http1Request(Http1Connection connection, bool waitForRead)
        {
            _connection = connection;
            _waitForRead = waitForRead;
        }

        public void Init(Http1Connection connection, bool waitForRead)
        {
            _connection = connection;
            _waitForRead = waitForRead;
            Reset();
            _writeWaitTaskSource.Reset();
            _readWaitTaskSource.Reset();
        }

        protected internal override async ValueTask DisposeAsync(int version, CancellationToken cancellationToken)
        {
            ThrowIfDisposed(version);

            if (_connection is not Http1Connection connection)
            {
                return;
            }

            if (ReadType != HttpReadType.EndOfStream)
            {
                // drain the connection to prepare for next request.

                try
                {
                    while (await ReadAsync(version, cancellationToken).ConfigureAwait(false) != HttpReadType.EndOfStream)
                    {
                        // TODO: test against maximum drain size.
                    }
                }
                catch (OperationCanceledException)
                {
                    // do not throw OCE in DisposeAsync; instead treat as an abortive dispose rather than graceful. (i.e. tear down connection without draining)
                    _connection.DrainFailed();
                }
            }

            _connection = null!;
            connection.ReleaseNextReader(this);
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
            _connection.WriteConnectRequest(authority);
        }

        protected internal override void WriteRequest(int version, ReadOnlySpan<byte> method, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery)
        {
            ThrowIfDisposed(version);
            _connection.WriteRequest(method, authority, pathAndQuery);
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

            if (!_waitForRead)
            {
                return _connection.ReadAsync(this, cancellationToken);
            }
            else
            {
                return WaitThenReadAsync(cancellationToken);
            }
        }

        private async ValueTask<HttpReadType> WaitThenReadAsync(CancellationToken cancellationToken)
        {
            // TODO: register cancellation token.
            await _readWaitTaskSource.UntypedTask.ConfigureAwait(false);
            return await _connection.ReadAsync(this, cancellationToken).ConfigureAwait(false);
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

        public ValueTask<ValueHttpRequest?> GetWriteWaitTask(CancellationToken cancellationToken)
        {
            // TODO: register cancellationToken.
            return _writeWaitTaskSource.Task;
        }

        public void ReleaseWriteWait() =>
            _writeWaitTaskSource.SetResult(GetValueRequest());

        public void ReleaseReadWait()
        {
            _waitForRead = false;
            _readWaitTaskSource.SetResult(0);
        }

        public void FailReadWait(Exception ex)
        {
            _waitForRead = false;
            _readWaitTaskSource.SetException(ex);
        }
    }
}
