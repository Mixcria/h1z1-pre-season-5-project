using System.Net;
using System.Net.Http.Json;
using Cranberry.Launcher.Core;
using Cranberry.NetworkBots;

namespace Cranberry.Harness.Tests;

public sealed class NetworkBotFixtureReadinessTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request, ++Calls);
    }

    [Fact]
    public async Task NativeProtocolUsesIssuedTicketWithoutAnyHelperReadyCall()
    {
        var issued = new GameLaunch("authored-fixture-ticket", "127.0.0.1:1", "fixture");
        using var handler = new Handler(async (request, call) =>
        {
            Assert.Equal(1, call);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/launch", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<LaunchRequest>();
            Assert.Equal(32123, body!.GatewayPort);
            Assert.Equal(0, body.DoorSwingProtocol);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(issued) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://127.0.0.1:1234/") };
        Assert.Equal(issued, await TlsFixture.LaunchProtocolFixture(http, 32123, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("http://127.0.0.1/")]
    public async Task NonFixtureEndpointNeverReceivesFixtureLaunch(string address)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP call"));
        using var http = new HttpClient(handler) { BaseAddress = new(address) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => TlsFixture.LaunchProtocolFixture(http, 32123, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RejectedLaunchCannotReturnUsableLaunch()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var http = new HttpClient(handler) { BaseAddress = new("https://127.0.0.1/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsFixture.LaunchProtocolFixture(http, 32123, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyTicketCannotReturnUsableLaunch(string ticket)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new GameLaunch(ticket, "127.0.0.1:1", "fixture")) }));
        using var http = new HttpClient(handler) { BaseAddress = new("https://127.0.0.1/") };
        await Assert.ThrowsAsync<InvalidDataException>(() => TlsFixture.LaunchProtocolFixture(http, 32123, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }
}
