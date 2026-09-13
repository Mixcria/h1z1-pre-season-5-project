namespace Cranberry.Login;

/// <summary>
/// Server-side character-name rules. The August UI performs its own checks, but the roster is
/// authoritative and must apply the same decision to both the validation preview and creation.
/// </summary>
public static class CharacterNamePolicy
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 20;

    /// <summary>
    /// Trims harmless surrounding whitespace and accepts an ASCII letter followed by letters or
    /// digits. Keeping the alphabet deliberately small makes names stable across JSON, logs and
    /// case-insensitive uniqueness checks.
    /// </summary>
    public static bool TryNormalize(string? candidate, out string normalized)
    {
        normalized = candidate?.Trim() ?? string.Empty;
        if (normalized.Length is < MinimumLength or > MaximumLength
            || !IsAsciiLetter(normalized[0]))
        {
            return false;
        }

        for (int index = 1; index < normalized.Length; index++)
        {
            char value = normalized[index];
            if (!IsAsciiLetter(value) && !char.IsAsciiDigit(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
