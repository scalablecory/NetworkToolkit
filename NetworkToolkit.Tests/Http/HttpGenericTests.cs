using NetworkToolkit.Http.Primitives;
using NetworkToolkit.Tests.Servers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace NetworkToolkit.Tests
{
    public abstract class HttpGenericTests : TestsBase
    {
        internal abstract Task RunSingleStreamTest(Func<ValueHttpRequest, Uri, Task> clientFunc, Func<HttpTestStream, Task> serverFunc, int? millisecondsTimeout = null);

        [Theory]
        [MemberData(nameof(HeadersData))]
        public async Task HeadersSend_Success(TestHeadersSink requestHeaders)
        {
            await RunSingleStreamTest(
                async (clientRequest, serverUri) =>
                {
                    clientRequest.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    clientRequest.WriteRequest(HttpMethod.Get, serverUri);
                    clientRequest.WriteHeaders(requestHeaders);
                    await clientRequest.CompleteRequestAsync();
                },
                async serverStream =>
                {
                    HttpTestFullRequest request = await serverStream.ReceiveAndSendAsync();
                    Assert.True(request.Headers.Contains(requestHeaders));
                });
        }

        [Theory]
        [MemberData(nameof(HeadersData))]
        public async Task HeadersReceived_Success(TestHeadersSink expectedResponseHeaders)
        {
            await RunSingleStreamTest(
                async (clientRequest, serverUri) =>
                {
                    clientRequest.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    clientRequest.WriteRequest(HttpMethod.Get, serverUri);
                    await clientRequest.CompleteRequestAsync();

                    TestHeadersSink actualResponseHeaders = await clientRequest.ReadAllHeadersAsync();
                    Assert.True(actualResponseHeaders.Contains(expectedResponseHeaders));
                },
                async serverStream =>
                {
                    await serverStream.ReceiveAndSendAsync(headers: expectedResponseHeaders);
                });
        }

        public static TheoryData<TestHeadersSink> HeadersData() => new TheoryData<TestHeadersSink>
        {
            new TestHeadersSink
            {
            },
            new TestHeadersSink
            {
                { "foo", "1234" }
            },
            new TestHeadersSink
            {
                { "foo", "5678" },
                { "bar", "9012" }
            },
            new TestHeadersSink
            {
                { "foo", "3456" },
                { "bar", "7890" },
                { "quz", "1234" }
            }
        };

        [Theory]
        [MemberData(nameof(ChunkedData))]
        public async Task Send_ChunkedRequest_Success(int testIdx, TestHeadersSink requestHeaders, List<string> requestContent, TestHeadersSink requestTrailingHeaders)
        {
            _ = testIdx; // only used to assist debugging.

            await RunSingleStreamTest(
                async (client, serverUri) =>
                {
                    long contentLength = requestContent.Sum(x => (long)x.Length);
                    client.ConfigureRequest(contentLength, hasTrailingHeaders: true);
                    client.WriteRequest(HttpMethod.Post, serverUri);
                    client.WriteHeaders(requestHeaders);

                    foreach (string content in requestContent)
                    {
                        await client.WriteContentAsync(content);
                    }

                    client.WriteTrailingHeaders(requestTrailingHeaders);

                    await client.CompleteRequestAsync();
                },
                async server =>
                {
                    HttpTestFullRequest request = await server.ReceiveAndSendAsync();

                    if (request.Version.Major == 1)
                    {
                        Assert.Equal("chunked", request.Headers.GetSingleValue("transfer-encoding"));
                    }

                    Assert.True(request.Headers.Contains(requestHeaders));
                    Assert.Equal(string.Join("", requestContent), request.Content);
                    Assert.True(request.TrailingHeaders.Contains(requestTrailingHeaders));
                });
        }

        [Theory]
        [MemberData(nameof(ChunkedData))]
        public async Task Receive_ChunkedResponse_Success(int testIdx, TestHeadersSink responseHeaders, List<string> responseContent, TestHeadersSink responseTrailingHeaders)
        {
            _ = testIdx; // only used to assist debugging.

            await RunSingleStreamTest(
                async (client, serverUri) =>
                {
                    client.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    client.WriteRequest(HttpMethod.Get, serverUri);
                    client.WriteHeader("TE", "trailers");
                    await client.CompleteRequestAsync();

                    Assert.True(await client.ReadToResponseAsync());
                    Version version = client.Version!;

                    TestHeadersSink headers = await client.ReadAllHeadersAsync();

                    if (version.Major == 1)
                    {
                        Assert.Equal("chunked", headers.GetSingleValue("transfer-encoding"));
                    }

                    Assert.True(headers.Contains(responseHeaders));

                    string content = await client.ReadAllContentAsStringAsync();
                    Assert.Equal(string.Join("", responseContent), content);

                    TestHeadersSink trailers = await client.ReadAllTrailingHeadersAsync();
                    Assert.True(trailers.Contains(responseTrailingHeaders));
                },
                async server =>
                {
                    await server.ReceiveAndSendChunkedAsync(headers: responseHeaders, content: responseContent, trailingHeaders: responseTrailingHeaders);
                });
        }

        public static IEnumerable<object[]> ChunkedData()
        {
            int testIdx = 0;

            foreach (object[] headersTheoryData in HeadersData())
            {
                TestHeadersSink headers = (TestHeadersSink)headersTheoryData[0];

                foreach (List<string> contents in GetContents())
                {
                    foreach (object[] trailersTheoryData in HeadersData())
                    {
                        TestHeadersSink trailers = (TestHeadersSink)trailersTheoryData[0];
                        var trailersToSend = new TestHeadersSink();
                        foreach (var kvp in trailers)
                        {
                            trailersToSend.Add(kvp.Key + "-trailer", kvp.Value);
                        }

                        ++testIdx;

                        yield return new object[] { testIdx, headers, contents, trailersToSend };
                    }
                }
            }

            static IEnumerable<List<string>> GetContents() =>
                new[]
                {
                    new List<string> { },
                    new List<string> { "foo" },
                    new List<string> { "foo", "barbar" },
                    new List<string> { "foo", "barbar", "bazbazbaz" }
                };
        }
    }
}
