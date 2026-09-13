namespace Cranberry.Harness.Replay;

/// <summary>
/// Finds the capture directory. It is not inside the repository — the host writes to
/// <c>C:\Aug2017\captures</c> while the repository is <c>C:\Aug2017\Server</c> — so it is located
/// by walking up from the running assembly rather than by a relative path from the project.
/// </summary>
public static class CaptureLocator
{
    public const string DirectoryName = "captures";

    /// <summary>The capture directory, or null when this machine has none.</summary>
    public static string? TryFindDirectory(string? startAt = null)
    {
        var directory = new DirectoryInfo(startAt ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, DirectoryName);
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "wire-*.txt").Any())
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static string Root =>
        TryFindDirectory() ?? throw new DirectoryNotFoundException(
            "No 'captures' directory with wire-*.txt files was found above " + AppContext.BaseDirectory);

    /// <summary>The full path of one named capture.</summary>
    public static string? TryFind(string fileName)
    {
        string? directory = TryFindDirectory();
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Every <c>wire-*.txt</c>, oldest name first (the names sort chronologically).</summary>
    public static IReadOnlyList<string> All()
    {
        string? directory = TryFindDirectory();
        return directory is null ? [] : [.. System.IO.Directory.GetFiles(directory, "wire-*.txt").Order(StringComparer.Ordinal)];
    }
}
