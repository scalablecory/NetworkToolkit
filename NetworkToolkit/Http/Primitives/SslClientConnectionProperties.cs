using NetworkToolkit.Connections;
using System;
using System.Net.Security;

namespace NetworkToolkit.Http.Primitives
{
    internal sealed class SslClientConnectionProperties : SslClientAuthenticationOptions, IConnectionProperties
    {
        public bool TryGetProperty(Type type, out object? value)
        {
            if (type == typeof(SslClientAuthenticationOptions))
            {
                value = this;
                return true;
            }

            value = null;
            return false;
        }
    }
}
