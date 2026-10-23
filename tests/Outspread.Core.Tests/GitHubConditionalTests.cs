using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Outspread.Core.Tests;

/// <summary>
/// The eight-second poll is only affordable because a conditional request that answers 304 costs
/// no rate limit (measured 2026-10-23 on the live API: a full tree call moved <c>remaining</c>
/// 4980→4979, the same call with <c>If-None-Match</c> answered 304 and left it at 4979).
/// </summary>
/// <remarks>
/// A handler, not a live host: the promise being tested is that this class *sends* the validator
/// on the second poll and returns the first answer on a 304. Both halves are visible in the
/// request and the branch taken, and neither needs the network to be honest about.
/// </remarks>
public class GitHubConditionalTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public const string Etag = "\"tree-v1\"";

        public int Calls;
        public int ConditionalCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Calls++;

            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == Etag))
            {
                ConditionalCalls++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            const string json = """
                {"sha":"tree-sha-1","tree":[{"path":"boards/a.canvas","type":"blob","sha":"blob-a","size":10}],"truncated":false}
                """;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            response.Headers.ETag = new EntityTagHeaderValue(Etag);

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_second_tree_call_sends_the_validator_and_reuses_the_304_answer()
    {
        var handler = new FakeHandler();
        using var client = new HttpClient(handler);
        using var transport = new GitHubContents(client, "o", "r", "main");

        var first = await transport.ReadTreeAsync(CancellationToken.None);
        var second = await transport.ReadTreeAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("tree-sha-1", first!.TreeToken);

        var entry = Assert.Single(first.Entries);
        Assert.Equal("boards/a.canvas", entry.Path);
        Assert.Equal("blob-a", entry.Token);
        Assert.Equal(10, entry.Bytes);

        // A 304 must hand back the same tree, not null and not an exception about "not answering".
        Assert.Equal(first.TreeToken, second!.TreeToken);
        Assert.Equal(first.Entries.Count, second.Entries.Count);

        Assert.Equal(1, handler.ConditionalCalls);
        Assert.Equal(2, handler.Calls);
    }
}
