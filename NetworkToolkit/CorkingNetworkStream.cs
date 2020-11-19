using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// Applies <c>TCP_CORK</c> to <see cref="Socket"/>.
    /// The application must be running on Linux to support this.
    /// Writes to this <see cref="Stream"/> will not immediately be sent, instead waiting for one of:
    /// <list type="bullet">
    /// <item><description><see cref="Flush"/> or <see cref="FlushAsync(CancellationToken)"/> to be called.</description></item>
    /// <item><description>A full packet worth of data to be written.</description></item>
    /// <item><description>200ms to pass.</description></item>
    /// </list>
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class CorkingNetworkStream : GatheringNetworkStream
    {
        const int IPPROTO_TCP = 6; // from netinet/in.h
        const int TCP_CORK = 3; // from linux/tcp.h

        /// <summary>
        /// If true, <see cref="CorkingNetworkStream"/> is supported on your platform.
        /// </summary>
        /// <remarks>
        /// <see cref="CorkingNetworkStream"/> requires the <c>TCP_CORK</c> option on Linux.
        /// </remarks>
        public static bool IsSupported { get; } = OperatingSystem.IsLinux();

        /// <summary>
        /// Instantiates a new <see cref="CorkingNetworkStream"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> this stream will operate over.</param>
        /// <param name="ownsSocket">If true, the <paramref name="socket"/> will be disposed of along with this stream.</param>
        public CorkingNetworkStream(Socket socket, bool ownsSocket) : this(socket, FileAccess.ReadWrite, ownsSocket)
        {
        }

        /// <summary>
        /// Instantiates a new <see cref="CorkingNetworkStream"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> this stream will operate over.</param>
        /// <param name="access">The access permissions given to this stream.</param>
        /// <param name="ownsSocket">If true, the <paramref name="socket"/> will be disposed of along with this stream.</param>
        public CorkingNetworkStream(Socket socket, FileAccess access, bool ownsSocket) : base(socket, access, ownsSocket)
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException($"{nameof(CorkingNetworkStream)} requires Linux.");
            }

            if (socket.ProtocolType != ProtocolType.Tcp)
            {
                throw new IOException($"{nameof(CorkingNetworkStream)} requires a {nameof(ProtocolType)} of {nameof(ProtocolType.Tcp)}");
            }

            SetCork(1);
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            SetCork(0);
            SetCork(1);
        }

        /// <inheritdoc/>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

            try
            {
                SetCork(0);
                SetCork(1);
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }

            return Task.CompletedTask;
        }

        private void SetCork(int enabled)
        {
            Debug.Assert(enabled is 0 or 1);
            Socket.SetRawSocketOption(IPPROTO_TCP, TCP_CORK, MemoryMarshal.Cast<int, byte>(MemoryMarshal.CreateReadOnlySpan(ref enabled, 1)));
        }
    }
}
