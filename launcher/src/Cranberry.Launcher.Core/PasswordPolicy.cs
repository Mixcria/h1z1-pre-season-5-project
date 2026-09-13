using System.Text;

namespace Cranberry.Launcher.Core;

/// <summary>Shared rules for new passwords only. Existing credentials remain valid for login.</summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 15, MaximumLength = 128;
    public const string Guidance = "Use 15-128 characters. Try a unique phrase of several unrelated words. Avoid your account name and common passwords.";

    public static string? Error(string? password, string? accountName)
    {
        if (password is null || password.Length > MaximumLength || password.EnumerateRunes().Count() < MinimumLength)
            return "Use a password between 15 and 128 characters.";
        if (password.Any(char.IsControl) || string.IsNullOrWhiteSpace(password))
            return "Choose a password with visible characters, without tabs or line breaks.";
        string compact = string.Concat(password.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (compact.Length < 8 || compact.Distinct().Count() < 5 || Repeated(compact)
            || "0123456789012345678901234567890".Contains(compact, StringComparison.Ordinal)
            || "123456789012345678901234567890".Contains(compact, StringComparison.Ordinal)
            || "abcdefghijklmnopqrstuvwxyz".Contains(compact, StringComparison.Ordinal)
            || "qwertyuiopasdfghjklzxcvbnm".Contains(compact, StringComparison.Ordinal))
            return "That password is too predictable. Choose a unique phrase or a generated password.";
        string letters = string.Concat(compact.Where(char.IsLetter));
        string name = string.Concat((accountName ?? "").Where(char.IsLetter)).ToLowerInvariant();
        string[] common = ["password", "passwordpassword", "qwerty", "qwertyuiop", "letmein", "iloveyou",
            "welcome", "admin", "administrator", "changeme", "correcthorsebatterystaple", "cranberry", "hiz", name];
        if (common.Any(word => word.Length > 0 && (letters == word || letters == word + word)))
            return "That password is too common or based on your account. Choose a unique phrase or a generated password.";
        return null;
    }

    public static void Validate(string? password, string? accountName)
    {
        if (Error(password, accountName) is { } error) throw new InvalidOperationException(error);
    }

    private static bool Repeated(string value)
    {
        for (int size = 1; size <= value.Length / 2; size++)
            if (value.Length % size == 0 && Enumerable.Range(0, value.Length).All(i => value[i] == value[i % size])) return true;
        return false;
    }
}
