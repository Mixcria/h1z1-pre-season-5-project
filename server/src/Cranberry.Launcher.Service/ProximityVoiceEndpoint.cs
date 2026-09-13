using System.Net.WebSockets;
using Cranberry.Launcher.Core.Voice;

namespace Cranberry.Launcher.Service;

public static class ProximityVoiceEndpoint
{
    public static async Task Run(WebSocket socket, VoiceRouter router, VoicePeer peer, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Receive()
        {
            byte[] buffer = new byte[VoiceWire.MaxMessageBytes];
            int bad = 0;
            while (!stop.IsCancellationRequested)
            {
                int length = 0, fragments = 0; ValueWebSocketReceiveResult part;
                do
                {
                    if (length == buffer.Length || ++fragments > 16) throw new InvalidDataException("Voice frame too large or fragmented.");
                    part = await socket.ReceiveAsync(buffer.AsMemory(length), stop.Token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Invalid voice frame.");
                    length += part.Count;
                } while (!part.EndOfMessage);
                if (!router.Receive(peer, buffer.AsSpan(0, length), Environment.TickCount64))
                {
                    if (++bad >= 30) throw new InvalidDataException("Invalid or excessive voice traffic.");
                }
                else bad = Math.Max(0, bad - 1);
            }
        }
        async Task Send()
        {
            await foreach (var delivery in peer.Outgoing.ReadAllAsync(stop.Token))
            {
                if (!router.CanDeliver(peer, delivery, Environment.TickCount64)) continue;
                byte[] bytes = delivery.Control ?? VoiceWire.EncodeAudio(delivery.Speaker, delivery.Sequence,
                    delivery.CreatedMs, delivery.Left, delivery.Right, delivery.Opus.Span);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(250); // One stalled voice receiver cannot queue seconds of speech.
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
            }
        }
        Task[] pumps = [Receive(), Send()];
        try { await await Task.WhenAny(pumps); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException) { }
        finally
        {
            stop.Cancel(); socket.Abort(); router.Disconnect(peer);
            try { await Task.WhenAll(pumps); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException) { }
        }
    }
}
