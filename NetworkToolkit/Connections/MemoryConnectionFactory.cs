using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A factory of in-memory connections.
    /// </summary>
    /// <remarks>
    /// Once a <see cref="ConnectionListener"/> has been opened via <see cref="ListenAsync(EndPoint?, IConnectionProperties?, CancellationToken)"/>,
    /// calls to <see cref="ConnectAsync(EndPoint, IConnectionProperties?, CancellationToken)"/> should use the <see cref="EndPoint"/> returned by <see cref="ConnectionListener.EndPoint"/>.
    /// </remarks>
    public sealed class MemoryConnectionFactory : ConnectionFactory
    {
        private readonly ConcurrentDictionary<EndPoint, Channel<TaskCompletionSource<Connection>>> _incomingConnection = new ();

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            return default;
        }

        /// <inheritdoc/>
        public override ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            if (endPoint == null) return ValueTask.FromException<Connection>(ExceptionDispatchInfo.SetCurrentStackTrace(new ArgumentNullException(nameof(endPoint))));
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromException<Connection>(ExceptionDispatchInfo.SetCurrentStackTrace(new SocketException((int)SocketError.OperationAborted)));

            if (_incomingConnection.TryGetValue(endPoint, out Channel<TaskCompletionSource<Connection>>? channel))
            {
                var tcs = new TaskCompletionSource<Connection>();
                if (channel.Writer.TryWrite(tcs))
                {
                    return new ValueTask<Connection>(tcs.Task);
                }
            }

            return ValueTask.FromException<Connection>(ExceptionDispatchInfo.SetCurrentStackTrace(new SocketException((int)SocketError.ConnectionRefused)));
        }

        /// <inheritdoc/>
        public override ValueTask<ConnectionListener> ListenAsync(EndPoint? endPoint = null, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromException<ConnectionListener>(ExceptionDispatchInfo.SetCurrentStackTrace(new SocketException((int)SocketError.OperationAborted)));
            }

            endPoint ??= new SentinelEndPoint();

            Channel<TaskCompletionSource<Connection>> channel = Channel.CreateUnbounded<TaskCompletionSource<Connection>>();

            if (!_incomingConnection.TryAdd(endPoint, channel))
            {
                return ValueTask.FromException<ConnectionListener>(ExceptionDispatchInfo.SetCurrentStackTrace(new SocketException((int)SocketError.AddressAlreadyInUse)));
            }

            return new ValueTask<ConnectionListener>(new Listener(channel, _incomingConnection, endPoint));
        }

        private sealed class SentinelEndPoint : EndPoint
        {
            public override AddressFamily AddressFamily => AddressFamily.Unspecified;
        }

        private sealed class Listener : ConnectionListener
        {
            private readonly Channel<TaskCompletionSource<Connection>> _channel;
            private readonly ConcurrentDictionary<EndPoint, Channel<TaskCompletionSource<Connection>>> _incomingConnection;
            private readonly EndPoint _endPoint;

            public override EndPoint? EndPoint => _endPoint;

            public Listener(Channel<TaskCompletionSource<Connection>> channel, ConcurrentDictionary<EndPoint, Channel<TaskCompletionSource<Connection>>> incomingConnection, EndPoint endPoint)
            {
                _channel = channel;
                _incomingConnection = incomingConnection;
                _endPoint = endPoint;
            }

            protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
            {
                bool removed = _incomingConnection.TryRemove(_endPoint, out Channel<TaskCompletionSource<Connection>>? channel);
                Debug.Assert(removed);
                Debug.Assert(channel == _channel);

                channel.Writer.TryComplete();

                while (channel.Reader.TryRead(out TaskCompletionSource<Connection>? tcs))
                {
                    tcs.SetException(new SocketException((int)SocketError.ConnectionRefused));
                }

                return default;
            }

            public override async ValueTask<Connection?> AcceptConnectionAsync(IConnectionProperties? options = null, CancellationToken cancellationToken = default)
            {
                TaskCompletionSource<Connection> tcs;

                try
                {
                    tcs = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }

                (Connection clientConnection, Connection serverConnection) = MemoryConnection.Create(new SentinelEndPoint(), _endPoint);

                tcs.SetResult(clientConnection);
                return serverConnection;
            }
        }
    }
}
