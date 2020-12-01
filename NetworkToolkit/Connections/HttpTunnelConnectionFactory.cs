using NetworkToolkit.Http.Primitives;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A filtering connection factory implementing an HTTP tunnel.
    /// </summary>
    public sealed class HttpTunnelConnectionFactory : ConnectionFactory
    {
        private HttpConnection _httpConnection;
        private readonly HttpPrimitiveVersion _httpVersion;
        private readonly HttpVersionPolicy _httpVersionPolicy;
        private readonly bool _ownsConnection;

        /// <summary>
        /// Instantiates a new <see cref="HttpTunnelConnectionFactory"/>.
        /// </summary>
        /// <param name="httpConnection">The <see cref="HttpConnection"/> to open a tunnel over.</param>
        /// <param name="httpVersion">The HTTP version to establish the connect tunnel over.</param>
        /// <param name="httpVersionPolicy">A policy controlling what HTTP version will be used for the connect tunnel.</param>
        /// <param name="ownsConnection">If true, the <paramref name="httpConnection"/> will be disposed when the <see cref=" HttpTunnelConnectionFactory"/> tunnel is disposed.</param>
        public HttpTunnelConnectionFactory(HttpConnection httpConnection, HttpPrimitiveVersion httpVersion, HttpVersionPolicy httpVersionPolicy, bool ownsConnection = false)
        {
            _httpConnection = httpConnection ?? throw new ArgumentNullException(nameof(httpConnection));
            _httpVersion = httpVersion ?? throw new ArgumentNullException(nameof(httpVersion));
            _httpVersionPolicy = httpVersionPolicy;
            _ownsConnection = ownsConnection;
        }

        /// <inheritdoc/>
        public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            string authority = endPoint switch
            {
                DnsEndPoint dns => Tools.EscapeIdnHost(dns.Host) + ":" + dns.Port.ToString(CultureInfo.InvariantCulture),
                IPEndPoint ip4 when ip4.AddressFamily == AddressFamily.InterNetwork => ip4.Address.ToString() + ":" + ip4.Port.ToString(CultureInfo.InvariantCulture),
                IPEndPoint ip6 when ip6.AddressFamily == AddressFamily.InterNetworkV6 => "[" + ip6.Address.ToString() + "]:" + ip6.Port.ToString(CultureInfo.InvariantCulture),
                null => throw new ArgumentNullException(nameof(endPoint)),
                _ => throw new ArgumentException($"{nameof(EndPoint)} is of an unsupported type. Must be one of {nameof(DnsEndPoint)} or {nameof(IPEndPoint)}", nameof(endPoint))
            };

            byte[] authorityBytes = Encoding.ASCII.GetBytes(authority);

            ValueHttpRequest request = (await _httpConnection.CreateNewRequestAsync(_httpVersion, _httpVersionPolicy, cancellationToken).ConfigureAwait(false))
                ?? throw new Exception($"{nameof(HttpConnection)} in use by {nameof(HttpTunnelConnectionFactory)} has been closed by peer.");

            try
            {
                request.ConfigureRequest(contentLength: null, hasTrailingHeaders: false);
                request.WriteConnectRequest(authorityBytes);
                await request.FlushHeadersAsync(cancellationToken).ConfigureAwait(false);

                bool hasResponse = await request.ReadToFinalResponseAsync(cancellationToken).ConfigureAwait(false);
                Debug.Assert(hasResponse);

                if ((int)request.StatusCode > 299)
                {
                    throw new Exception($"Connect to HTTP tunnel failed; received status code {request.StatusCode}.");
                }

                var localEndPoint = new TunnelEndPoint(request.LocalEndPoint, request.RemoteEndPoint);
                var stream = new HttpContentStream(request, ownsRequest: true);
                return new HttpTunnelConnection(localEndPoint, endPoint, stream);
            }
            catch
            {
                await request.DisposeAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc/>
        public override ValueTask<ConnectionListener> ListenAsync(EndPoint? endPoint, IConnectionProperties? options = null, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<ConnectionListener>(ExceptionDispatchInfo.SetCurrentStackTrace(new NotSupportedException($"{nameof(HttpTunnelConnectionFactory)} does not support listening.")));
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
        {
            if (_ownsConnection)
            {
                return _httpConnection.DisposeAsync(cancellationToken);
            }

            return default;
        }

        private sealed class HttpTunnelConnection : Connection
        {
            public HttpTunnelConnection(EndPoint localEndPoint, EndPoint remoteEndPoint, HttpContentStream stream) : base(stream)
            {
                LocalEndPoint = localEndPoint;
                RemoteEndPoint = remoteEndPoint;
            }

            public override EndPoint? LocalEndPoint { get; }

            public override EndPoint? RemoteEndPoint { get; }

            protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
            {
                return default;
            }
        }
    }
}
