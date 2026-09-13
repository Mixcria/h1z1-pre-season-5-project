using System.Numerics;
using System.Threading.Channels;
using Concentus.Structs;

namespace Cranberry.Launcher.Core.Voice;

public readonly record struct VoiceDelivery(ulong Speaker, ulong Listener, ulong Match, uint Sequence,
    long CreatedMs, float Left, float Right, ReadOnlyMemory<byte> Opus, byte[]? Control = null);

public sealed record VoiceHudView(string AccountId, ulong CharacterId, ulong MatchId, ulong[] Speakers);

public sealed class VoicePeer
{
    internal VoicePeer(string account)
    {
        AccountId = account;
        Queue = Channel.CreateBounded<VoiceDelivery>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
            AllowSynchronousContinuations = false,
        }, _ => Interlocked.Increment(ref _dropped));
    }
    public string AccountId { get; }
    internal Channel<VoiceDelivery> Queue { get; }
    public ChannelReader<VoiceDelivery> Outgoing => Queue.Reader;
    internal VoiceParticipant? Position;
    internal bool Deafened, HasSequence;
    internal uint LastSequence;
    internal double Tokens = 10;
    internal long TokenAt;
    internal long StateAt = long.MinValue / 2;
    internal ulong StateCharacter;
    internal bool StateAllowed;
    internal long LastAudioAt = long.MinValue / 2;
    internal readonly Dictionary<ulong, (long At, float Distance)> Speakers = [];
    private long _dropped;
    public long DroppedFrames => Interlocked.Read(ref _dropped);
}

/// <summary>Authoritative proximity routing. Positions arrive only from the game listener.</summary>
public sealed class VoiceRouter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VoicePeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, List<VoicePeer>> _matches = [];
    private readonly Dictionary<ulong, VoicePeer> _characters = [];
    private long _snapshotMs = long.MinValue / 2;
    public float RangeMetres { get; }
    public int MaxSpeakers { get; }
    public const int MaxSnapshotAgeMs = 500, MaxQueuedAgeMs = 120;
    public const int SpeakerVisibleMs = 300;
    public int ConnectionCount { get { lock (_gate) return _peers.Count; } }
    public void Close()
    {
        lock (_gate)
        {
            foreach (var peer in _peers.Values) { peer.Position = null; peer.Queue.Writer.TryComplete(); }
            _peers.Clear(); _matches.Clear(); _characters.Clear();
        }
    }

    public VoiceRouter(float rangeMetres = 75, int maxSpeakers = 16)
    {
        if (!float.IsFinite(rangeMetres) || rangeMetres is < 5 or > 300) throw new ArgumentOutOfRangeException(nameof(rangeMetres));
        if (maxSpeakers is < 1 or > 150) throw new ArgumentOutOfRangeException(nameof(maxSpeakers));
        RangeMetres = rangeMetres; MaxSpeakers = maxSpeakers;
    }

    public VoicePeer Connect(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        lock (_gate)
        {
            if (_peers.Count >= 8192 || _peers.ContainsKey(account)) throw new InvalidOperationException("Voice is already connected or full.");
            var peer = new VoicePeer(account); _peers.Add(account, peer); return peer;
        }
    }
    public void Disconnect(VoicePeer peer)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer.AccountId, out var current) || current != peer) return;
            _peers.Remove(peer.AccountId);
            if (peer.Position is { } position) _characters.Remove(position.CharacterId);
            peer.Position = null; peer.Queue.Writer.TryComplete();
            foreach (var match in _matches.Values) match.Remove(peer);
        }
    }

    public void UpdateWorld(IReadOnlyList<VoiceParticipant> participants, long now)
    {
        lock (_gate)
        {
            var accounts = new Dictionary<string, VoiceParticipant>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            var characters = new HashSet<ulong>();
            var ambiguousCharacters = new HashSet<ulong>();
            foreach (var participant in participants)
            {
                if (!accounts.TryAdd(participant.AccountId, participant)) ambiguous.Add(participant.AccountId);
                if (!characters.Add(participant.CharacterId)) ambiguousCharacters.Add(participant.CharacterId);
            }
            _matches.Clear(); _characters.Clear();
            foreach (var peer in _peers.Values)
            {
                accounts.TryGetValue(peer.AccountId, out var position);
                if (ambiguous.Contains(peer.AccountId) || position is { MatchId: 0 } || position is { CharacterId: 0 }
                    || position is not null && ambiguousCharacters.Contains(position.CharacterId)
                    || position is not null && (!Finite(position.Position) || !float.IsFinite(position.Heading))) position = null;
                if (peer.Position?.MatchId != position?.MatchId || peer.Position?.CharacterId != position?.CharacterId)
                {
                    peer.Speakers.Clear();
                    peer.LastAudioAt = long.MinValue / 2;
                }
                peer.Position = position;
                if (position is not null)
                {
                    if (!_matches.TryGetValue(position.MatchId, out var members)) _matches.Add(position.MatchId, members = []);
                    members.Add(peer);
                    _characters.Add(position.CharacterId, peer);
                }
                ulong character = position?.CharacterId ?? 0;
                bool allowed = position is not null && !peer.Deafened;
                if (peer.StateCharacter != character || peer.StateAllowed != allowed || now - peer.StateAt >= (allowed ? 200 : 1000))
                {
                    peer.StateCharacter = character; peer.StateAllowed = allowed; peer.StateAt = now;
                    peer.Queue.Writer.TryWrite(new(0, 0, 0, 0, now, 0, 0, default, VoiceWire.EncodeState(character, allowed)));
                }
            }
            _snapshotMs = now;
        }
    }

    public bool Receive(VoicePeer source, ReadOnlySpan<byte> packet, long now)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(source.AccountId, out var current) || current != source) return false;
            source.Tokens = Math.Min(10, source.Tokens + Math.Max(0, now - source.TokenAt) * .06);
            source.TokenAt = now;
            if (source.Tokens < 1) return false;
            source.Tokens--;
            if (VoiceWire.Is(packet, VoiceWire.Deafen) && packet.Length == 5 && packet[4] <= 1)
            { source.Deafened = packet[4] != 0; source.Speakers.Clear(); source.LastAudioAt = long.MinValue / 2; return true; }
            if (!VoiceWire.TryUpload(packet, out uint sequence)) return false;
            if (source.HasSequence && unchecked((int)(sequence - source.LastSequence)) <= 0) return false;
            source.HasSequence = true; source.LastSequence = sequence;
            if (now - _snapshotMs > MaxSnapshotAgeMs || source.Position is not { } speaker || source.Deafened) return true;
            ReadOnlySpan<byte> opus = packet[8..];
            // Validate frame duration/channels before relaying. Full decoding remains on the
            // audio client, so the game host performs no codec work for 150 simultaneous talkers.
            try
            {
                if ((opus[0] & 4) != 0 || OpusPacketInfo.GetNumSamples(opus, VoiceWire.SampleRate) != VoiceWire.FrameSamples) return false;
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or Concentus.OpusException) { return false; }
            source.LastAudioAt = now;
            if (!_matches.TryGetValue(speaker.MatchId, out var listeners)) return true;
            byte[]? shared = null;
            foreach (VoicePeer target in listeners)
            {
                if (target.Deafened || target.Position is not { } listener
                    || !VoiceSpatial.TryGains(speaker, listener, RangeMetres, out float left, out float right)) continue;
                float distance = Vector3.DistanceSquared(speaker.Position, listener.Position);
                if (!AdmitSpeaker(target, speaker.CharacterId, distance, now)) continue;
                shared ??= opus.ToArray();
                target.Queue.Writer.TryWrite(new(speaker.CharacterId, listener.CharacterId, speaker.MatchId,
                    sequence, now, left, right, shared));
            }
            return true;
        }
    }

    private bool AdmitSpeaker(VoicePeer listener, ulong speaker, float distance, long now)
    {
        if (!listener.Speakers.ContainsKey(speaker) && listener.Speakers.Count >= MaxSpeakers)
        {
            ulong remove = 0; float farthest = distance;
            foreach (var entry in listener.Speakers)
            {
                if (now - entry.Value.At > 250) { remove = entry.Key; break; }
                if (entry.Value.Distance > farthest) { farthest = entry.Value.Distance; remove = entry.Key; }
            }
            if (remove == 0) return false;
            listener.Speakers.Remove(remove);
        }
        listener.Speakers[speaker] = (now, distance);
        return true;
    }

    /// <summary>Activity from accepted microphone frames, scoped to the same listeners as the audio.</summary>
    public IReadOnlyList<VoiceHudView> HudViews(long now)
    {
        lock (_gate)
        {
            if (now - _snapshotMs > MaxSnapshotAgeMs) return [];
            List<VoiceHudView> views = [];
            foreach (var peer in _peers.Values)
            {
                if (peer.Position is not { } listener || peer.Deafened) continue;
                List<ulong> speakers = [];
                // Local speech has no audio loopback, but still needs its own name/icon.
                if (now - peer.LastAudioAt <= SpeakerVisibleMs) speakers.Add(listener.CharacterId);
                foreach (var entry in peer.Speakers.OrderBy(p => p.Value.Distance).ThenBy(p => p.Key))
                {
                    if (speakers.Count == 10) break; // The August HUD has ten native voice rows.
                    if (now - entry.Value.At > SpeakerVisibleMs
                        || !_characters.TryGetValue(entry.Key, out var source) || source.Deafened
                        || now - source.LastAudioAt > SpeakerVisibleMs
                        || source.Position is not { } speaker
                        || !VoiceSpatial.TryGains(speaker, listener, RangeMetres, out _, out _)) continue;
                    speakers.Add(speaker.CharacterId);
                }
                views.Add(new(peer.AccountId, listener.CharacterId, listener.MatchId, speakers.ToArray()));
            }
            return views;
        }
    }

    public bool CanDeliver(VoicePeer listener, in VoiceDelivery delivery, long now)
    {
        lock (_gate)
        {
            if (delivery.Control is not null) return now - delivery.CreatedMs <= MaxSnapshotAgeMs;
            if (now - _snapshotMs > MaxSnapshotAgeMs || now - delivery.CreatedMs > MaxQueuedAgeMs
                || listener.Deafened || listener.Position is not { } target || target.CharacterId != delivery.Listener
                || target.MatchId != delivery.Match || !listener.Speakers.ContainsKey(delivery.Speaker)) return false;
            return _characters.TryGetValue(delivery.Speaker, out var member) && !member.Deafened
                && member.Position is { } source && VoiceSpatial.TryGains(source, target, RangeMetres, out _, out _);
        }
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
