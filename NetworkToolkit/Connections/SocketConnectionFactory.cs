using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A connection factory using sockets.
    /// </summary>
    public class SocketConnectionFactory : ConnectionFactory
    {
        /// <summary>
        /// A property key to retrieve the underlying <see cref="Socket"/> of the connection.
        /// </summary>
        public static ConnectionPropertyKey<Socket> SocketPropertyKey => new ();

        private readonly AddressFamily _addressFamily;
        private readonly SocketType _socketType;
        private readonly ProtocolType _protocolType;
        private readonly bool _corking, _fastOpen;

        /// <summary>
        /// Enables TCP_CORK, if available.
        /// This option is only valid if the <see cref="ProtocolType"/> of <see cref="SocketConnectionFactory"/> is TCP.
        /// </summary>
        public bool EnableCorking
        {
            get => _corking;
            init
            {
                if (value == true && _protocolType != ProtocolType.Tcp)
                {
                    throw new Exception($"{nameof(EnableCorking)} may only be enabled for a {nameof(ProtocolType)} of {nameof(ProtocolType.Tcp)}.");
                }
                _corking = value;
            }
        }

        /// <summary>
        /// Enables TCP_FASTOPEN, if available.
        /// </summary>
        public bool EnableFastOpen
        {
            get => _fastOpen;
            init
            {
                if (value == true && _protocolType != ProtocolType.Tcp)
                {
                    throw new Exception($"{nameof(EnableCorking)} may only be enabled for a {nameof(ProtocolType)} of {nameof(ProtocolType.Tcp)}.");
                }
                _fastOpen = value;
            }
        }

        /// <summary>
        /// Instantiates a new <see cref="SocketConnectionFactory"/>.
        /// </summary>
        /// <param name="socketType">The <see cref="SocketType"/> to use when creating sockets.</param>
        /// <param name="protocolType">The <see cref="ProtocolType"/> to use when creating sockets.</param>
        /// <remarks>
        /// If TCP values are specified and the OS supports it, sockets opened by this <see cref="SocketConnectionFactory"/> will be dual-mode IPv6.
        /// Socket options can be customized by overriding the <see cref="CreateSocket(AddressFamily, SocketType, ProtocolType)"/> method.
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

        /// <summary>
        /// Creates a <see cref="Socket"/> used by the connection factory.
        /// </summary>
        /// <param name="addressFamily">The <see cref="AddressFamily"/> of the <see cref="Socket"/> to create.</param>
        /// <param name="socketType">The <see cref="SocketType"/> of the <see cref="Socket"/> to create.</param>
        /// <param name="protocolType">The <see cref="ProtocolType"/> of the <see cref="Socket"/> to create.</param>
        /// <returns>A new <see cref="Socket"/>.</returns>
        protected virtual Socket CreateSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
        {
            var sock = new Socket(addressFamily, socketType, protocolType);

            try
            {
                if (addressFamily == AddressFamily.InterNetworkV6)
                {
                    sock.DualMode = true;
                }

                if (protocolType == ProtocolType.Tcp)
                {
                    sock.NoDelay = true;

                    if (TcpFastOpenStream.IsSupported && EnableFastOpen)
                    {
                        int enabled = 1;
                        sock.SetRawSocketOption(TcpFastOpenStream.IPPROTO_TCP, TcpFastOpenStream.TCP_FASTOPEN, MemoryMarshal.Cast<int, byte>(MemoryMarshal.CreateSpan(ref enabled, 1)));
                    }
                }

                return sock;
            }
            catch
            {
                sock.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a <see cref="NetworkStream"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> to create a <see cref="NetworkStream"/> over.</param>
        /// <returns>A new <see cref="NetworkStream"/>. This stream must take ownership over <paramref name="socket"/>.</returns>
        /// <remarks>The default implementation returns a <see cref="GatheringNetworkStream"/>, offering better performance for supporting usage.</remarks>
        protected internal virtual NetworkStream CreateStream(Socket socket)
        {
#pragma warning disable CA1416 // Validate platform compatibility
            if (CorkingNetworkStream.IsSupported && _protocolType == ProtocolType.Tcp && EnableCorking)
            {
                return new CorkingNetworkStream(socket, ownsSocket: true);
            }
#pragma warning restore CA1416 // Validate platform compatibility

            return new GatheringNetworkStream(socket, ownsSocket: true);
        }

        /// <inheritdoc/>
        public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            if (endPoint == null) throw new ArgumentNullException(nameof(endPoint));

            Socket sock = CreateSocket(_addressFamily, _socketType, _protocolType);

            if (TcpFastOpenStream.IsSupported && EnableFastOpen)
            {
                return new SocketConnection(sock, new TcpFastOpenStream(this, sock, endPoint));
            }

            try
            {
                await sock.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                return new SocketConnection(sock, CreateStream(sock));
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

            Socket sock = CreateSocket(_addressFamily, _socketType, _protocolType);

            try
            {
                sock.Bind(endPoint!); // relying on Bind() checking args.
                sock.Listen();
                return new ValueTask<ConnectionListener>(new SocketListener(this, sock));
            }
            catch(Exception ex)
            {
                sock.Dispose();
                return ValueTask.FromException<ConnectionListener>(ex);
            }
        }

        private sealed class SocketListener : ConnectionListener
        {
            private readonly SocketConnectionFactory _connectionFactory;
            private readonly Socket _listener;
            private readonly EventArgs _args = new EventArgs();

            public override EndPoint? EndPoint => _listener.LocalEndPoint;

            public SocketListener(SocketConnectionFactory connectionFactory, Socket listener)
            {
                _connectionFactory = connectionFactory;
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

                        try
                        {
                            return new SocketConnection(socket, _connectionFactory.CreateStream(socket));
                        }
                        catch
                        {
                            socket.Dispose();
                        }
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

            private Socket Socket { get; }

            public SocketConnection(Socket socket, Stream stream) : base(stream)
            {
                Socket = socket;
            }

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

            public override async ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
            {
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                Socket.Shutdown(SocketShutdown.Send);
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
