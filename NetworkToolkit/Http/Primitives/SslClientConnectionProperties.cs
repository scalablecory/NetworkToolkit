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
    internal sealed class SslClientConnectionProperties : IConnectionProperties
    {
        public SslClientAuthenticationOptions SslOptions { get; }

        public SslClientConnectionProperties(SslClientAuthenticationOptions sslOptions)
        {
            SslOptions = sslOptions;
        }

        public bool TryGetProperty(Type type, out object? value)
        {
            if (type == typeof(SslClientAuthenticationOptions))
            {
                value = SslOptions;
                return true;
            }

            value = null;
            return false;
        }
    }
}
