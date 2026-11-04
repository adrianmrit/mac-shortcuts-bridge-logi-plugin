using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
public sealed class ActionIconHostTests
{
    private string portFile = null!;

    [SetUp]
    public void SetUp() => portFile = Path.GetTempFileName();

    [TearDown]
    public void TearDown() => File.Delete(portFile);

    [Test]
    public void ReadEndpoint_ServiceRestarts_UsesNewPublishedPort()
    {
        File.WriteAllText(portFile, "56086");
        Assert.That(ActionIconHost.ReadEndpoint(portFile).Port, Is.EqualTo(56086));
        File.WriteAllText(portFile, "62902\n");
        var endpoint = ActionIconHost.ReadEndpoint(portFile);
        Assert.Multiple(() =>
        {
            Assert.That(endpoint.Port, Is.EqualTo(62902));
            Assert.That(endpoint.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(endpoint.AbsolutePath, Is.EqualTo("/configui3"));
        });
    }

    [TestCase("")]
    [TestCase("0")]
    [TestCase("65536")]
    [TestCase("-1")]
    [TestCase("localhost:56086")]
    [TestCase("12345678901234567")]
    public void ReadEndpoint_InvalidPort_ReportsFailure(string value)
    {
        File.WriteAllText(portFile, value);
        Assert.Throws<IOException>(() => ActionIconHost.ReadEndpoint(portFile));
    }

    [Test]
    public void ReadEndpoint_MissingPort_DoesNotUseStaleFallback()
    {
        File.Delete(portFile);
        Assert.Throws<FileNotFoundException>(() => ActionIconHost.ReadEndpoint(portFile));
    }

    [Test]
    public async Task GetAsync_ConcurrentRequests_UsesDistinctRequestIds()
    {
        using var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        File.WriteAllText(portFile, port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var host = new ActionIconHost(portFile);
        var requestIds = ReadAndReplyToTwoRequestsAsync(listener);
        var binding = new ShortcutIconBinding("device", "application", "profile", "action",
            "icon", "shortcut", "profile-file");

        await Task.WhenAll(
            host.GetAsync(binding, false, CancellationToken.None),
            host.GetAsync(binding, false, CancellationToken.None));

        Assert.That(await requestIds, Is.Unique);
    }

    private static async Task<int[]> ReadAndReplyToTwoRequestsAsync(HttpListener listener) =>
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            using var socket = accepted.WebSocket;
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);

            var request = JsonNode.Parse(message.ToArray())!.AsObject();
            var id = request["id"]!.GetValue<int>();
            var response = new JsonObject
            {
                ["id"] = id,
                ["name"] = "GetActionIcon",
                ["failed"] = false,
                ["data"] = new JsonObject { ["actionIcon"] = new JsonObject() }
            };
            var payload = Encoding.UTF8.GetBytes(response.ToJsonString());
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            return id;
        }));
}
