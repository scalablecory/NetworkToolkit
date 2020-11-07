using NetworkToolkit.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    internal sealed class DnsEndPointWithProperties : DnsEndPoint, IConnectionProperties
    {
        public SslClientAuthenticationOptions? SslOptions { get; }

        public DnsEndPointWithProperties(string host, int port, SslClientAuthenticationOptions? sslOptions)
            : base(host, port)
        {
            SslOptions = sslOptions;
        }

        public bool TryGetProperty(Type type, out object? value)
        {
            if (type == typeof(SslClientAuthenticationOptions) && SslOptions is SslClientAuthenticationOptions nonNullOptions)
            {
                value = SslOptions;
                return true;
            }

            value = null;
            return false;
        }
    }
}
