using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Dolophobia.Net;

/// <summary>
/// Outbound WebSocket to the relay, so the host never needs to open a port.
/// Runs on background tasks; the game thread talks to it only through <see cref="Inbox"/> and the Send methods.
/// </summary>
internal sealed class RelayClient : IDisposable
{
    public readonly ConcurrentQueue<JsonObject> Inbox = new();
    public volatile bool Connected;

    readonly Uri _url;
    readonly string _secret, _hostKey;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<(WebSocketMessageType type, byte[] data)> _outbox =
        Channel.CreateBounded<(WebSocketMessageType, byte[])>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // stale snapshots/frames are worthless anyway
            SingleReader = true,
        });

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public RelayClient(string url, string secret, string hostKey)
    {
        _url = new Uri(url);
        _secret = secret;
        _hostKey = hostKey;
    }

    public void Start() => Task.Run(() => RunAsync(_cts.Token));

    public void Send(object message)
    {
        if (!Connected) return;
        _outbox.Writer.TryWrite((WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(message, Json)));
    }

    public void SendBinary(byte[] data)
    {
        if (!Connected) return;
        _outbox.Writer.TryWrite((WebSocketMessageType.Binary, data));
    }

    async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            try
            {
                await ws.ConnectAsync(_url, ct);
                var hello = JsonSerializer.SerializeToUtf8Bytes(new { t = "host.hello", hostKey = _hostKey, secret = _secret, version = Plugin.Version });
                await ws.SendAsync(new ArraySegment<byte>(hello), WebSocketMessageType.Text, true, ct);
                Connected = true;
                backoff = TimeSpan.FromSeconds(1);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sender = SendLoopAsync(ws, linked.Token);
                await ReceiveLoopAsync(ws, ct);
                linked.Cancel();
                await sender.ContinueWith(_ => { });
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Plugin.Logger.LogWarning($"[Relay] {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (Connected) Inbox.Enqueue(new JsonObject { ["t"] = "relay.down" });
                Connected = false;
            }

            if (ct.IsCancellationRequested) break;
            Plugin.Logger.LogInfo($"[Relay] Reconnecting in {backoff.TotalSeconds:0}s");
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // Drop anything queued for a previous connection.
        while (_outbox.Reader.TryRead(out _)) { }
        await foreach (var (type, data) in _outbox.Reader.ReadAllAsync(ct))
            await ws.SendAsync(new ArraySegment<byte>(data), type, true, ct);
    }

    async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Plugin.Logger.LogWarning($"[Relay] Closed by relay: {result.CloseStatusDescription}");
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text &&
                JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)) is JsonObject obj)
                Inbox.Enqueue(obj);
            message.SetLength(0);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _outbox.Writer.TryComplete();
    }
}
