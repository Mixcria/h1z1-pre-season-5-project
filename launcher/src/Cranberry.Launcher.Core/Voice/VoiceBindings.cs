using System.Xml.Linq;

namespace Cranberry.Launcher.Core.Voice;

public sealed record VoiceBindings(int[][] Talk, int[][] Deafen, int[][] Louder, int[][] Quieter, string TalkLabel)
{
    public static VoiceBindings Load(string path, string? talkOverride = null)
    {
        XDocument? document = File.Exists(path) ? XDocument.Load(path) : null;
        string[] Triggers(string action, params string[] fallback)
        {
            // User profiles can contain only overrides. An absent action inherits the
            // default, while an explicitly empty action deliberately unbinds it.
            var actions = document?.Descendants("Action").Where(a => (string?)a.Attribute("name") == action).ToArray();
            return actions is null || actions.Length == 0 ? fallback
                : actions.SelectMany(a => a.Elements("Trigger")).Select(e => e.Value.Trim()).ToArray();
        }
        string[] talk = string.IsNullOrWhiteSpace(talkOverride) ? Triggers("VoiceChatProximity", "KP_4", "Mouse_2") : [talkOverride];
        int[][] Keys(string[] triggers) => triggers.Select(Parse).Where(k => k.Length > 0).ToArray();
        return new(Keys(talk), Keys(Triggers("ToggleMuteVoice", "Alt+M")), Keys(Triggers("VoiceVolumeUp", "KP_Add")),
            Keys(Triggers("VoiceVolumeDown", "KP_Subtract")), string.Join(" / ", talk));
    }
    public static int[] Parse(string binding)
    {
        List<int> keys = [];
        foreach (string part in binding.Split('+', StringSplitOptions.TrimEntries))
        {
            string key = part.ToUpperInvariant();
            int value = key switch
            {
                "SHIFT" => 0x10, "CONTROL" or "CTRL" => 0x11, "ALT" => 0x12,
                "SHIFT_LEFT" or "SHIFT_L" => 0xA0, "SHIFT_RIGHT" or "SHIFT_R" => 0xA1,
                "CONTROL_LEFT" or "CTRL_LEFT" or "CONTROL_L" => 0xA2,
                "CONTROL_RIGHT" or "CTRL_RIGHT" or "CONTROL_R" => 0xA3,
                "ALT_LEFT" or "ALT_L" => 0xA4, "ALT_RIGHT" or "ALT_R" => 0xA5,
                "MOUSE_0" => 1, "MOUSE_1" => 2, "MOUSE_2" or "MIDDLEMOUSE" => 4,
                "MOUSE_3" => 5, "MOUSE_4" => 6, "SPACE" => 0x20, "TAB" => 9,
                "KP_ADD" => 0x6B, "KP_SUBTRACT" => 0x6D, "KP_MULTIPLY" => 0x6A,
                "KP_DIVIDE" => 0x6F, "KP_DECIMAL" => 0x6E,
                "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
                "INSERT" => 0x2D, "DELETE" => 0x2E, "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27,
                _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => key[0],
                _ when key.StartsWith("KP_") && int.TryParse(key[3..], out int digit) && digit is >= 0 and <= 9 => 0x60 + digit,
                _ when key.StartsWith('F') && int.TryParse(key[1..], out int number) && number is >= 1 and <= 24 => 0x6F + number,
                _ => 0,
            };
            // An unknown modifier must never broaden a capture binding to an ordinary key.
            if (value == 0) return [];
            keys.Add(value);
        }
        return keys.ToArray();
    }
    public static bool Pressed(int[][] bindings, Func<int, bool> down) => bindings.Any(keys => keys.Length > 0 && keys.All(down));
}
