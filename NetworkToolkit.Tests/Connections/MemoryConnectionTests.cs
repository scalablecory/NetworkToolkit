using NetworkToolkit.Connections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NetworkToolkit.Tests.Connections
{
    public class MemoryConnectionTests : TestsBase
    {
        [Fact]
        public async Task ReadWrite_Success()
        {
            const string ClientTestValue = "ClientString1234";
            const string ServerTestValue = "ServerString5678";

            (Connection clientConnection, Connection serverConnection) = MemoryConnection.Create();

            await using (clientConnection)
            await using (serverConnection)
            {
                await RunClientServer(async () =>
                {
                    using (var writer = new StreamWriter(clientConnection.Stream, leaveOpen: true))
                    {
                        await writer.WriteLineAsync(ClientTestValue);
                    }
                    await clientConnection.CompleteWritesAsync();

                    using (var reader = new StreamReader(clientConnection.Stream))
                    {
                        Assert.Equal(ServerTestValue, await reader.ReadLineAsync());
                        Assert.Null(await reader.ReadLineAsync());
                    }
                },
                async () =>
                {
                    using (var writer = new StreamWriter(serverConnection.Stream, leaveOpen: true))
                    {
                        await writer.WriteLineAsync(ServerTestValue);
                    }
                    await serverConnection.CompleteWritesAsync();

                    using (var reader = new StreamReader(serverConnection.Stream))
                    {
                        Assert.Equal(ClientTestValue, await reader.ReadLineAsync());
                        Assert.Null(await reader.ReadLineAsync());
                    }
                });
            }
        }
    }
}
