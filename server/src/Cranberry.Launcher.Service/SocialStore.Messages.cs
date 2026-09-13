using System.Text.Json;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Service;

public sealed partial class SocialStore
{
    private sealed record ReadMark(string Actor, string Friend, long Through);
    private sealed record MessageData(int Version, long NextId, List<DirectMessage> Messages, List<ReadMark> Reads);
    private MessageData _chat = new(1, 1, [], []);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _messageRates = [];
    private string MessagePath => Path.Combine(Path.GetDirectoryName(_path)!, "messages.json");

    private void LoadMessages()
    {
        if (!File.Exists(MessagePath)) return;
        _chat = JsonSerializer.Deserialize<MessageData>(File.ReadAllText(MessagePath))
            ?? throw new InvalidDataException("Invalid message history.");
        if (_chat.Version != 1 || _chat.Messages is null || _chat.Reads is null || _chat.NextId < 1
            || _chat.Messages.Any(m => m.Id >= _chat.NextId)) throw new InvalidDataException("Invalid message history.");
    }

    public AvatarView SetAvatar(string actor, string pixels)
    {
        pixels = AvatarPixels.Validate(pixels);
        var user = Find(actor);
        if (pixels != user.Avatar)
            Commit(_data with { Users = _data.Users.Select(u => u.Id == actor ? u with { Avatar = pixels } : u).ToList() });
        return new(AvatarPixels.Version(pixels), pixels);
    }

    public AvatarView Avatar(string actor, string target)
    {
        if (actor != target) RequireFriend(actor, target);
        string pixels = Find(target).Avatar;
        return new(AvatarPixels.Version(pixels), pixels);
    }

    private void RequireFriend(string actor, string target)
    {
        if (string.IsNullOrEmpty(target) || actor == target || !Friends(actor, target))
            throw new InvalidOperationException("Select an accepted friend to send or read messages.");
    }

    private static bool Between(DirectMessage m, string a, string b) => m.From == a && m.To == b || m.From == b && m.To == a;

    public Conversation History(string actor, string target)
    {
        RequireFriend(actor, target);
        return new(target, _chat.Messages.Where(m => Between(m, actor, target)).TakeLast(100).ToArray(),
            _chat.Reads.FirstOrDefault(r => r.Actor == actor && r.Friend == target)?.Through ?? 0);
    }

    public MessageUnread[] Unread(string actor) => InGameDirectory.Friends(actor).Select(friend =>
    {
        long read = _chat.Reads.FirstOrDefault(r => r.Actor == actor && r.Friend == friend)?.Through ?? 0;
        return new MessageUnread(friend, _chat.Messages.Count(m => m.To == actor && m.From == friend && m.Id > read));
    }).Where(r => r.Count > 0).ToArray();

    public DirectMessage SendMessage(string actor, MessageRequest request)
    {
        RequireFriend(actor, request.Target);
        if (!Guid.TryParseExact(request.ClientId, "N", out _)) throw new InvalidOperationException("Invalid message ID. Try sending again.");
        string body = request.Text?.Trim() ?? "";
        if (body.Length is < 1 or > 1000 || body.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            throw new InvalidOperationException("Messages must contain 1–1000 characters, without control characters.");
        var previous = _chat.Messages.FirstOrDefault(m => m.From == actor && m.ClientId == request.ClientId);
        if (previous is not null)
        {
            if (previous.To != request.Target || previous.Text != body) throw new InvalidOperationException("This message ID was already used.");
            return previous;
        }
        if (!_messageRates.TryGetValue(actor, out var sent)) _messageRates[actor] = sent = new();
        while (sent.TryPeek(out var when) && when <= _clock().AddMinutes(-1)) sent.Dequeue();
        if (sent.Count >= 30) throw new InvalidOperationException("Messages are being sent too quickly. Wait a moment.");
        var message = new DirectMessage(_chat.NextId, request.ClientId, actor, request.Target, body, _clock());
        var conversation = _chat.Messages.Where(m => Between(m, actor, request.Target)).TakeLast(199).Append(message);
        var messages = _chat.Messages.Where(m => !Between(m, actor, request.Target)).Concat(conversation).OrderBy(m => m.Id).ToList();
        CommitMessages(_chat with { NextId = checked(message.Id + 1), Messages = messages });
        sent.Enqueue(_clock());
        return message;
    }

    public void MarkRead(string actor, string friend, long through)
    {
        RequireFriend(actor, friend);
        long latest = _chat.Messages.Where(m => m.From == friend && m.To == actor).Select(m => m.Id).DefaultIfEmpty().Max();
        through = Math.Clamp(through, 0, latest);
        long previous = _chat.Reads.FirstOrDefault(r => r.Actor == actor && r.Friend == friend)?.Through ?? 0;
        if (through <= previous) return;
        CommitMessages(_chat with { Reads = [.. _chat.Reads.Where(r => r.Actor != actor || r.Friend != friend), new(actor, friend, through)] });
    }

    private void CommitMessages(MessageData next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MessagePath)!);
        string temporary = MessagePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
        File.Move(temporary, MessagePath, true);
        _chat = next;
    }
}
