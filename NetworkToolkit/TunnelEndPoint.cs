using System;
using System.Net;

namespace NetworkToolkit
{
    internal sealed class TunnelEndPoint : EndPoint
    {
        public EndPoint? LocalEndPoint { get; }
        public EndPoint? RemoteEndPoint { get; }

        public TunnelEndPoint(EndPoint? localEndPoint, EndPoint? remoteEndPoint)
        {
            LocalEndPoint = localEndPoint;
            RemoteEndPoint = remoteEndPoint;
        }

        public override string ToString() =>
            $"{{ {LocalEndPoint?.ToString() ?? "unknown"} -> {RemoteEndPoint?.ToString() ?? "unknown"} }}";

        public override bool Equals(object? obj) =>
            obj is TunnelEndPoint ep &&
            (ep.LocalEndPoint != null) == (LocalEndPoint != null) &&
            (ep.RemoteEndPoint != null) == (RemoteEndPoint != null) &&
            ep.LocalEndPoint?.Equals(LocalEndPoint) != false &&
            ep.RemoteEndPoint?.Equals(RemoteEndPoint) != false;

        public override int GetHashCode() =>
            HashCode.Combine(LocalEndPoint, RemoteEndPoint);
    }
}
