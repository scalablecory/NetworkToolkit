using NetworkToolkit.Connections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NetworkToolkit.Tests.Connections
{
    public class SslConnectionFactoryTests : TestsBase
    {
        [Fact]
        public async Task Connect_SelfSigned_Success()
        {
            var protocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("test") };

            var connectProperties = new ConnectionProperties();
            connectProperties.Add(SslConnectionFactory.SslClientAuthenticationOptionsPropertyKey, new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = delegate { return true; }
            });

            var listenProperties = new ConnectionProperties();
            listenProperties.Add(SslConnectionFactory.SslServerAuthenticationOptionsPropertyKey, new SslServerAuthenticationOptions
            {
                ApplicationProtocols = protocols,
                ServerCertificate = TestCertificates.GetSelfSigned13ServerCertificate()
            });

            byte[] sendBuffer = Encoding.ASCII.GetBytes("Testing 123");

            await using ConnectionFactory factory = new SslConnectionFactory(new MemoryConnectionFactory());
            await using ConnectionListener listener = await factory.ListenAsync(options: listenProperties);

            await RunClientServer(
                async () =>
                {
                    await using Connection connection = await factory.ConnectAsync(listener.EndPoint!, connectProperties);
                    await connection.Stream.WriteAsync(sendBuffer);
                },
                async () =>
                {
                    await using Connection? connection = await listener.AcceptConnectionAsync();
                    Assert.NotNull(connection);
                    Debug.Assert(connection != null);

                    byte[] buffer = new byte[sendBuffer.Length + 1];
                    int readLen = await connection.Stream.ReadAsync(buffer);
                    Assert.Equal(sendBuffer, buffer[..readLen]);

                    readLen = await connection.Stream.ReadAsync(buffer);
                    Assert.Equal(0, readLen);
                }).ConfigureAwait(false);
        }
    }
}
