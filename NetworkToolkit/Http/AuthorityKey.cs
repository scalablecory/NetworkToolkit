using System;

namespace NetworkToolkit.Http
{
    internal readonly struct AuthorityKey : IEquatable<AuthorityKey>
    {
        public string IdnHost { get; }
        public int Port { get; }

        public AuthorityKey(string idnHost, int port)
        {
            IdnHost = idnHost;
            Port = port;
        }

        public bool Equals(AuthorityKey other) =>
            Port == other.Port
            && string.Equals(IdnHost, other.IdnHost, StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is AuthorityKey key && Equals(key);

        public override int GetHashCode() =>
            HashCode.Combine(IdnHost, Port);
    }
}
