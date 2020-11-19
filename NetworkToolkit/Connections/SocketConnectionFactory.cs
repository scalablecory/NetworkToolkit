using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A connection factory using sockets.
    /// </summary>
    public sealed class SocketConnectionFactory : ConnectionFactory
    {
        /// <summary>
        /// A property key to retrieve the underlying <see cref="Socket"/> of the connection.
        /// </summary>
        public static ConnectionPropertyKey<Socket> SocketPropertyKey => new ();

        private AddressFamily _addressFamily;
        private SocketType _socketType;
        private ProtocolType _protocolType;

        /// <summary>
        /// Instantiates a new <see cref="SocketConnectionFactory"/>.
        /// </summary>
        /// <param name="socketType">The <see cref="SocketType"/> to use when creating sockets.</param>
        /// <param name="protocolType">The <see cref="ProtocolType"/> to use when creating sockets.</param>
        /// <remarks>
        /// If TCP values are specified and the OS supports it, sockets opened by this <see cref="SocketConnectionFactory"/> will be dual-mode IPv6.
        /// </remarks>
        public SocketConnectionFactory(SocketType socketType = SocketType.Stream, ProtocolType protocolType = ProtocolType.Tcp)
            : this(AddressFamily.Unspecified, socketType, protocolType)
        {
        }

        /// <summary>
        /// Instantiates a new <see cref="SocketConnectionFactory"/>.
        /// </summary>
        /// <param name="addressFamily">The <see cref="AddressFamily"/> to use when creating sockets.</param>
        /// <param name="socketType">The <see cref="SocketType"/> to use when creating sockets.</param>
        /// <param name="protocolType">The <see cref="ProtocolType"/> to use when creating sockets.</param>
        public SocketConnectionFactory(AddressFamily addressFamily, SocketType socketType = SocketType.Stream, ProtocolType protocolType = ProtocolType.Tcp)
        {
            _addressFamily =
                _addressFamily != AddressFamily.Unspecified ? addressFamily :
                Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 :
                AddressFamily.InterNetwork;
            _socketType = socketType;
            _protocolType = protocolType;
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            return default;
        }

        private Socket CreateSocket()
        {
            var sock = new Socket(_addressFamily, _socketType, _protocolType);

            if (_addressFamily == AddressFamily.InterNetworkV6)
            {
                sock.DualMode = true;
            }

            if (_protocolType == ProtocolType.Tcp)
            {
                sock.NoDelay = true;
            }

            return sock;
        }

        /// <inheritdoc/>
        public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            if (endPoint == null) throw new ArgumentNullException(nameof(endPoint));

            Socket sock = CreateSocket();

            try
            {
                await sock.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                return new SocketConnection(sock);
            }
            catch
            {
                sock.Dispose();
                throw;
            }
        }

        /// <inheritdoc/>
        public override ValueTask<ConnectionListener> ListenAsync(EndPoint? endPoint = null, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<ConnectionListener>(cancellationToken);

            if (endPoint == null)
            {
                IPAddress address;

                if (_addressFamily == AddressFamily.InterNetworkV6)
                {
                    address = IPAddress.IPv6Any;
                }
                else if (_addressFamily == AddressFamily.InterNetwork)
                {
                    address = IPAddress.Any;
                }
                else
                {
                    return ValueTask.FromException<ConnectionListener>(ExceptionDispatchInfo.SetCurrentStackTrace(new ArgumentNullException(nameof(endPoint))));
                }

                endPoint = new IPEndPoint(address, 0);
            }

            Socket sock = CreateSocket();

            try
            {
                sock.Bind(endPoint!); // relying on Bind() checking args.
                sock.Listen();
                return new ValueTask<ConnectionListener>(new SocketListener(sock));
            }
            catch(Exception ex)
            {
                sock.Dispose();
                return ValueTask.FromException<ConnectionListener>(ex);
            }
        }

        private sealed class SocketListener : ConnectionListener
        {
            private readonly Socket _listener;
            private readonly EventArgs _args = new EventArgs();

            public override EndPoint? EndPoint => _listener.LocalEndPoint;

            public SocketListener(Socket listener)
            {
                _listener = listener;
            }

            protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
            {
                try
                {
                    _listener.Dispose();
                    _args.Dispose();
                    return default;
                }
                catch (Exception ex)
                {
                    return ValueTask.FromException(ex);
                }
            }

            public override async ValueTask<Connection?> AcceptConnectionAsync(IConnectionProperties? options = null, CancellationToken cancellationToken = default)
            {
                CancellationTokenRegistration tokenRegistration = default;

                try
                {
                    Debug.Assert(_args.AcceptSocket == null);
                    _args.AcceptSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    _args.Reset();

                    if (_listener.AcceptAsync(_args))
                    {
                        tokenRegistration = cancellationToken.UnsafeRegister(static o => ((Socket)o!).Dispose(), _listener);
                    }
                    else
                    {
                        _args.SetResult();
                    }

                    await _args.Task.ConfigureAwait(false);

                    if (_args.SocketError == SocketError.Success)
                    {
                        Socket socket = _args.AcceptSocket;
                        _args.AcceptSocket = null;
                        return new SocketConnection(socket);
                    }

                    if (_args.SocketError == SocketError.OperationAborted)
                    {
                        return null;
                    }

                    var sockEx = new SocketException((int)_args.SocketError);

                    throw _args.SocketError == SocketError.OperationAborted && cancellationToken.IsCancellationRequested
                        ? (Exception)new OperationCanceledException("The connect has been canceled.", sockEx, cancellationToken)
                        : sockEx;
                }
                finally
                {
                    await tokenRegistration.DisposeAsync().ConfigureAwait(false);

                    if (_args.AcceptSocket is Socket acceptSocket)
                    {
                        acceptSocket.Dispose();
                        _args.AcceptSocket = null;
                    }
                }
            }
        }

        private sealed class SocketConnection : Connection
        {
            public override EndPoint? LocalEndPoint => Socket.LocalEndPoint;
            public override EndPoint? RemoteEndPoint => Socket.RemoteEndPoint;

            private Socket Socket => ((NetworkStream)Stream).Socket;

            public SocketConnection(Socket socket) : base(CreateStream(socket))
            {
            }

            private static NetworkStream CreateStream(Socket socket) =>
                GatheringNetworkStream.IsSupported
                    ? new GatheringNetworkStream(socket)
                    : new NetworkStream(socket);

            protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
                => default;

            public override bool TryGetProperty(Type type, out object? value)
            {
                if (type == typeof(Socket))
                {
                    value = Socket;
                    return true;
                }

                if (type == null)
                {
                    throw new ArgumentNullException(nameof(type));
                }

                value = null;
                return false;
            }

            public override ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);

                try
                {
                    ((NetworkStream)Stream).Socket.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex)
                {
                    return ValueTask.FromException(ex);
                }

                return default;
            }
        }

        private sealed class EventArgs : SocketTaskEventArgs<int>
        {
            public void SetResult() => base.SetResult(0);
            protected override void OnCompleted(SocketAsyncEventArgs e) =>
                SetResult(0);
        }
    }
}
