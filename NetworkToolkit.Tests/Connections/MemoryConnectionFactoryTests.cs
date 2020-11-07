using NetworkToolkit.Connections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NetworkToolkit.Tests.Connections
{
    public class MemoryConnectionFactoryTests : TestsBase
    {
        [Theory]
        [InlineData(false), InlineData(true)]
        public async Task Connect_Success(bool clientFirst)
        {
            await using ConnectionFactory factory = new MemoryConnectionFactory();
            await using ConnectionListener listener = await factory.ListenAsync();

            using var semaphore = new SemaphoreSlim(0);

            await RunClientServer(async () =>
            {
                if (!clientFirst)
                {
                    bool success = await semaphore.WaitAsync(10_000);
                    Assert.True(success);
                }

                ValueTask<Connection> task = factory.ConnectAsync(listener.EndPoint!);
                if (clientFirst) semaphore.Release();

                await using Connection connection = await task;
            },
            async () =>
            {
                if (clientFirst)
                {
                    bool success = await semaphore.WaitAsync(10_000);
                    Assert.True(success);
                }

                ValueTask<Connection?> task = listener.AcceptConnectionAsync();
                if (!clientFirst) semaphore.Release();

                await using Connection? connection = await task;
                Assert.NotNull(connection);
            });
        }

        [Fact]
        public async Task Listener_DisposeCancelsConnect_Success()
        {
            await using ConnectionFactory factory = new MemoryConnectionFactory();
            await using ConnectionListener listener = await factory.ListenAsync();

            ValueTask<Connection> connectTask = factory.ConnectAsync(listener.EndPoint!);

            await listener.DisposeAsync();

            SocketException ex = await Assert.ThrowsAsync<SocketException>(async () =>
            {
                await using Connection connection = await connectTask;
            }).ConfigureAwait(false);

            Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
        }

        [Fact]
        public async Task Listener_DisposeCancelsAccept_Success()
        {
            await using ConnectionFactory factory = new MemoryConnectionFactory();
            await using ConnectionListener listener = await factory.ListenAsync();

            ValueTask<Connection?> acceptTask = listener.AcceptConnectionAsync();

            await listener.DisposeAsync();

            Connection? connection = await acceptTask;
            Assert.Null(connection);
        }
    }
}
