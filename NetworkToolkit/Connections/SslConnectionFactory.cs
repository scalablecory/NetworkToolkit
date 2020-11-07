using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A connection factory using SSL.
    /// </summary>
    public sealed class SslConnectionFactory : ConnectionFactory
    {
        private readonly ConnectionFactory _baseFactory;
        
        /// <summary>
        /// A connection property used to pass a <see cref="SslClientAuthenticationOptions"/> to <see cref="ConnectAsync(EndPoint, IConnectionProperties?, CancellationToken)"/>.
        /// </summary>
        public static ConnectionPropertyKey<SslClientAuthenticationOptions> SslClientAuthenticationOptionsPropertyKey => new();

        /// <summary>
        /// A connection property used to pass a <see cref="SslServerAuthenticationOptions"/> to <see cref="ConnectionListener.AcceptConnectionAsync(IConnectionProperties?, CancellationToken)"/>.
        /// </summary>
        public static ConnectionPropertyKey<SslServerAuthenticationOptions> SslServerAuthenticationOptionsPropertyKey => new();
        
        /// <summary>
        /// A connection property that returns the underlying <see cref="SslStream"/> of an established <see cref="Connection"/>.
        /// </summary>
        public static ConnectionPropertyKey<SslStream> SslStreamPropertyKey => new();

        /// <summary>
        /// Instantiates a new <see cref="SslConnectionFactory"/>.
        /// </summary>
        /// <param name="baseFactory">The base factory for the <see cref="SslConnectionFactory"/>.</param>
        public SslConnectionFactory(ConnectionFactory baseFactory)
        {
            _baseFactory = baseFactory;
        }

        /// <inheritdoc/>
        public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            SslClientAuthenticationOptions sslOptions = options.GetProperty(SslClientAuthenticationOptionsPropertyKey);

            Connection baseConnection = await _baseFactory.ConnectAsync(endPoint, options, cancellationToken).ConfigureAwait(false);
            SslStream? stream = null;
            
            try
            {
                stream = new SslStream(baseConnection.Stream, leaveInnerStreamOpen: false);

                await stream.AuthenticateAsClientAsync(sslOptions, cancellationToken).ConfigureAwait(false);
                return new SslConnection(baseConnection, stream);
            }
            catch
            {
                if (stream != null) await stream.DisposeAsync().ConfigureAwait(false);
                await baseConnection.DisposeAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc/>
        public override async ValueTask<ConnectionListener> ListenAsync(EndPoint? endPoint = null, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            ConnectionListener baseListener = await _baseFactory.ListenAsync(endPoint, options, cancellationToken).ConfigureAwait(false);
            return new SslListener(baseListener);
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            return _baseFactory.DisposeAsync(cancellationToken);
        }

        private sealed class SslListener : FilteringConnectionListener
        {

            public SslListener(ConnectionListener baseListener) : base(baseListener)
            {
            }

            public override async ValueTask<Connection?> AcceptConnectionAsync(IConnectionProperties? options = null, CancellationToken cancellationToken = default)
            {
                if (options == null) throw new ArgumentNullException(nameof(options));
                SslServerAuthenticationOptions sslOptions = options.GetProperty(SslServerAuthenticationOptionsPropertyKey);

                Connection? baseConnection = await BaseListener.AcceptConnectionAsync(options, cancellationToken).ConfigureAwait(false);

                if (baseConnection == null)
                {
                    return null;
                }

                SslStream? stream = null;

                try
                {
                    stream = new SslStream(baseConnection.Stream, leaveInnerStreamOpen: false);
                    await stream.AuthenticateAsServerAsync(sslOptions, cancellationToken).ConfigureAwait(false);

                    return new SslConnection(baseConnection, stream);
                }
                catch
                {
                    if (stream != null) await stream.DisposeAsync().ConfigureAwait(false);
                    await baseConnection.DisposeAsync(cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
        }

        private sealed class SslConnection : FilteringConnection
        {
            public SslConnection(Connection baseConnection, SslStream stream) : base(baseConnection, stream)
            {
            }

            protected override async ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
                await base.DisposeAsyncCore(cancellationToken).ConfigureAwait(false);
            }

            public override bool TryGetProperty(Type type, out object? value)
            {
                if (type == typeof(SslStream))
                {
                    value = (SslStream)Stream;
                    return true;
                }

                return BaseConnection.TryGetProperty(type, out value);
            }
        }
    }
}
