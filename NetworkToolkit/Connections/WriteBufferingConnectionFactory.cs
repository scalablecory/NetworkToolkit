using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A connection factory that adds write buffering to underlying connections.
    /// </summary>
    public sealed class WriteBufferingConnectionFactory : FilteringConnectionFactory
    {
        /// <summary>
        /// Instantiates a new <see cref="WriteBufferingConnectionFactory"/>.
        /// </summary>
        /// <param name="baseFactory">The underlying factory that will have write buffering added to its connections.</param>
        public WriteBufferingConnectionFactory(ConnectionFactory baseFactory) : base(baseFactory)
        {
        }

        /// <inheritdoc/>
        public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            Connection con = await BaseFactory.ConnectAsync(endPoint, options, cancellationToken).ConfigureAwait(false);
            return new FilteringConnection(con, new WriteBufferingStream(con.Stream));
        }

        /// <inheritdoc/>
        public override async ValueTask<ConnectionListener> ListenAsync(EndPoint? endPoint = null, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            ConnectionListener listener = await BaseFactory.ListenAsync(endPoint, options, cancellationToken).ConfigureAwait(false);
            return new WriteBufferingConnectionListener(listener);
        }

        private sealed class WriteBufferingConnectionListener : FilteringConnectionListener
        {
            public WriteBufferingConnectionListener(ConnectionListener baseListener) : base(baseListener)
            {
            }

            public override async ValueTask<Connection?> AcceptConnectionAsync(IConnectionProperties? options = null, CancellationToken cancellationToken = default)
            {
                Connection? con = await BaseListener.AcceptConnectionAsync(options, cancellationToken).ConfigureAwait(false);
                if (con == null) return con;

                return new FilteringConnection(con, new WriteBufferingStream(con.Stream));
            }
        }
    }
}
