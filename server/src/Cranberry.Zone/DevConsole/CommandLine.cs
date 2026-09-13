using System.Globalization;
using System.Numerics;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// One typed line, split. The client has already done the hard half: it strips the leading
/// <c>/</c> and the name and sends only the tail, with its case intact, as the
/// <c>argByteCount x utf8</c> field of <c>Command.ExecuteCommand</c>
/// (<see cref="ExecuteCommandRequest.Arguments"/>, sender <c>FUN_141296a90:67,123-140</c>). This
/// type is everything that happens after that.
/// <para>
/// <b>Case.</b> Words are lower-cased unless the command sets <see cref="ConsoleCommand.KeepCase"/>,
/// which is the owner's own lesson from his 1087 server (R1 §5 item 10): <c>/give AR15</c> must
/// work, and <c>/announce Hello There</c> must not be shouted back in lower case. Even a
/// lower-casing command can still reach the untouched text through <see cref="Text"/>.
/// </para>
/// <para>
/// <b>Quotes and <c>~</c>.</b> A double-quoted run is one token, so a place name with a space
/// survives; a <c>~</c> in a coordinate keeps the caller's own value on that axis, and <c>~5</c>
/// offsets it (design §2.2).
/// </para>
/// </summary>
public sealed class CommandLine
{
    private readonly int[] _offsets;

    private CommandLine(string name, string raw, string[] rawTokens, int[] offsets, bool keepCase)
    {
        Name = name;
        Raw = raw;
        RawTokens = rawTokens;
        _offsets = offsets;
        KeepCase = keepCase;
        Tokens = keepCase ? rawTokens : [.. rawTokens.Select(t => t.ToLowerInvariant())];
    }

    /// <summary>The command name as typed, lower-cased (the client folds to upper before hashing anyway).</summary>
    public string Name { get; }

    /// <summary>The argument tail exactly as the client sent it.</summary>
    public string Raw { get; }

    /// <summary>The tokens, lower-cased unless the command keeps case.</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>The tokens with their original case, whatever the command asked for.</summary>
    public IReadOnlyList<string> RawTokens { get; }

    /// <summary>True when this line was parsed for a <see cref="ConsoleCommand.KeepCase"/> command.</summary>
    public bool KeepCase { get; }

    /// <summary>How many tokens the tail held.</summary>
    public int Count => Tokens.Count;

    /// <summary>True when the caller typed a bare name.</summary>
    public bool IsEmpty => Tokens.Count == 0;

    /// <summary>Splits an argument tail. <paramref name="name"/> is stored lower-cased.</summary>
    public static CommandLine Parse(string name, string? arguments, bool keepCase = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        string raw = arguments ?? string.Empty;
        Tokenise(raw, out string[] tokens, out int[] offsets);
        return new CommandLine(name.ToLowerInvariant(), raw, tokens, offsets, keepCase);
    }

    /// <summary>The same line read as a case-keeping one (or not), without re-splitting the quotes.</summary>
    public CommandLine WithKeepCase(bool keepCase) =>
        keepCase == KeepCase ? this : new CommandLine(Name, Raw, [.. RawTokens], _offsets, keepCase);

    /// <summary>The line as it would be typed again, e.g. <c>/give ar15 1</c>.</summary>
    public string Typed => Raw.Length == 0 ? $"/{Name}" : $"/{Name} {Raw}";

    /// <summary>
    /// Whitespace split, with double quotes joining a run. A quote is only special at the start of
    /// a token; an unterminated quote runs to the end of the line rather than failing, because a
    /// half-typed line should answer with a usage rather than an exception.
    /// </summary>
    public static IReadOnlyList<string> Tokenise(string line)
    {
        Tokenise(line ?? string.Empty, out string[] tokens, out _);
        return tokens;
    }

    private static void Tokenise(string line, out string[] tokens, out int[] offsets)
    {
        List<string> parts = [];
        List<int> starts = [];
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            starts.Add(i);
            if (line[i] == '"')
            {
                i++;
                int begin = i;
                while (i < line.Length && line[i] != '"')
                {
                    i++;
                }

                parts.Add(line[begin..i]);
                if (i < line.Length)
                {
                    i++;
                }
            }
            else
            {
                int begin = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                parts.Add(line[begin..i]);
            }
        }

        tokens = [.. parts];
        offsets = [.. starts];
    }

    /// <summary>The token at <paramref name="index"/>, or null when the caller stopped short.</summary>
    public string? Word(int index) => index >= 0 && index < Tokens.Count ? Tokens[index] : null;

    /// <summary>The token at <paramref name="index"/>, or <paramref name="fallback"/>.</summary>
    public string Word(int index, string fallback) => Word(index) ?? fallback;

    /// <summary>
    /// Everything from token <paramref name="from"/> to the end of the line, with its original case
    /// and its original spacing - the shape <c>/announce</c> and <c>/raw</c> need.
    /// </summary>
    public string Text(int from = 0)
    {
        if (from < 0 || from >= _offsets.Length)
        {
            return string.Empty;
        }

        return Raw[_offsets[from]..].Trim();
    }

    /// <summary>Reads a decimal or <c>0x</c> integer.</summary>
    public bool TryInt(int index, out int value)
    {
        value = 0;
        string? token = Word(index);
        return token is not null && TryParseInt(token, out value);
    }

    /// <summary>Reads a decimal or <c>0x</c> integer, or answers <paramref name="fallback"/>.</summary>
    public int Int(int index, int fallback) => TryInt(index, out int value) ? value : fallback;

    /// <summary>Reads a decimal number.</summary>
    public bool TryFloat(int index, out float value)
    {
        value = 0f;
        string? token = Word(index);
        return token is not null
            && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value);
    }

    /// <summary>Reads a decimal number, or answers <paramref name="fallback"/>.</summary>
    public float Float(int index, float fallback) => TryFloat(index, out float value) ? value : fallback;

    /// <summary>Reads an unsigned integer written in hex, with or without the <c>0x</c>.</summary>
    public bool TryHex(int index, out uint value)
    {
        value = 0;
        string? token = Word(index);
        if (token is null)
        {
            return false;
        }

        ReadOnlySpan<char> span = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? token.AsSpan(2)
            : token;
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Reads an unsigned hex integer, or answers <paramref name="fallback"/>.</summary>
    public uint Hex(int index, uint fallback) => TryHex(index, out uint value) ? value : fallback;

    /// <summary>
    /// Reads every remaining token as hex bytes for <c>/raw</c>: <c>09 42 00</c>, <c>0942 00</c> and
    /// <c>0x09 0x42</c> all read the same. Null when a token is not an even run of hex digits.
    /// </summary>
    public byte[]? HexBytes(int from = 0)
    {
        if (from > Tokens.Count)
        {
            return null;
        }

        System.Text.StringBuilder digits = new();
        for (int i = from; i < Tokens.Count; i++)
        {
            string token = Tokens[i];
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..];
            }

            foreach (char c in token)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return null;
                }

                digits.Append(c);
            }
        }

        string text = digits.ToString();
        if (text.Length == 0 || (text.Length % 2) != 0)
        {
            return null;
        }

        return Convert.FromHexString(text);
    }

    /// <summary>
    /// Reads three coordinates starting at <paramref name="index"/>. <c>~</c> keeps
    /// <paramref name="origin"/> on that axis and <c>~5</c> / <c>~-2</c> offsets it, so
    /// <c>/tp ~ 200 ~</c> lifts the caller straight up (design §2.2).
    /// </summary>
    public bool TryVector3(int index, Vector3 origin, out Vector3 value)
    {
        value = origin;
        if (index < 0 || index + 3 > Tokens.Count)
        {
            return false;
        }

        float[] axes = [origin.X, origin.Y, origin.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            string? token = Word(index + axis);
            if (token is null)
            {
                return false;
            }

            if (token == "~")
            {
                continue;
            }

            if (token.StartsWith('~'))
            {
                if (!float.TryParse(token.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float delta))
                {
                    return false;
                }

                axes[axis] += delta;
                continue;
            }

            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float absolute))
            {
                return false;
            }

            axes[axis] = absolute;
        }

        if (axes.Any(axis => !float.IsFinite(axis))) return false;
        value = new Vector3(axes[0], axes[1], axes[2]);
        return true;
    }

    /// <summary>
    /// Resolves an item name or definition id through the caller's resolver. A bare number is taken
    /// as an id so <c>/give 2604</c> works before any roster name exists.
    /// </summary>
    public int? Item(int index, Func<string, int?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        string? token = Word(index);
        if (token is null)
        {
            return null;
        }

        return TryParseInt(token, out int id) ? id : resolve(token);
    }

    /// <summary>
    /// Matches a sub-verb (<c>/gas status</c>, <c>/loot spawn</c>) by unique prefix. On an
    /// ambiguous prefix <paramref name="error"/> lists the candidates, exactly as design §2.2 asks
    /// (<c>/gas st</c> answers <c>start status stop</c>); on no match it names the offender.
    /// </summary>
    public string? SubVerb(int index, IReadOnlyList<string> choices, out string? error)
    {
        ArgumentNullException.ThrowIfNull(choices);
        error = null;
        string? token = Word(index);
        if (token is null)
        {
            return null;
        }

        foreach (string choice in choices)
        {
            if (string.Equals(choice, token, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }

        List<string> hits = [.. choices.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase))];
        if (hits.Count == 1)
        {
            return hits[0];
        }

        error = hits.Count == 0
            ? $"not a sub-command: '{token}' -- try {string.Join(' ', choices)}"
            : $"ambiguous: '{token}' -- {string.Join(' ', hits)}";
        return null;
    }

    private static bool TryParseInt(string token, out int value)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            bool hex = int.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return hex;
        }

        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
