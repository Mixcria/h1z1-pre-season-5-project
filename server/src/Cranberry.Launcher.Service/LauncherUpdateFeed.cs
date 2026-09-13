using System.Text.Json;
using Cranberry.Launcher.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cranberry.Launcher.Service;

internal static class LauncherUpdateFeed
{
    internal static void Map(WebApplication app, string root, Action<LauncherRelease>? verify = null)
    {
        verify ??= r => r.Verify();
        string directory = Path.Combine(root, "launcher-updates");
        var gate = new object();
        // Release count is small; cache only the latest manifest. Old blobs remain available to downloads in flight.
        (DateTime Stamp, long Size, LauncherRelease Release)? cached = null;
        LauncherRelease Read(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Launcher manifest is too large.");
            var release = JsonSerializer.Deserialize<LauncherRelease>(File.ReadAllText(path), LauncherRelease.Json)
                ?? throw new InvalidDataException("Invalid launcher manifest.");
            verify(release); return release;
        }
        app.MapGet("/api/launcher/manifest", (HttpContext context) =>
        {
            string path = Path.Combine(directory, "latest.json");
            if (!File.Exists(path)) return Results.NoContent();
            LauncherRelease release;
            lock (gate)
            {
                var file = new FileInfo(path);
                if (cached is not { } entry || entry.Stamp != file.LastWriteTimeUtc || entry.Size != file.Length)
                    cached = (file.LastWriteTimeUtc, file.Length, Read(path));
                release = cached.Value.Release;
            }
            string etag = $"\"{release.Sequence}-{release.Sha256}\"";
            context.Response.Headers.CacheControl = "public, max-age=60";
            context.Response.Headers.ETag = etag;
            if (context.Request.Headers.IfNoneMatch.Any(value => value == etag)) return Results.StatusCode(304);
            return Results.Json(release, LauncherRelease.Json);
        });
        app.MapGet("/api/launcher/content/{hash}", (HttpContext context, string hash) =>
        {
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return Results.NotFound();
            hash = hash.ToUpperInvariant();
            string path = Path.Combine(directory, "content", hash), metadata = path + ".json";
            if (!File.Exists(path) || !File.Exists(metadata)) return Results.NotFound();
            var release = Read(metadata);
            if (release.Sha256 != hash || new FileInfo(path).Length != release.Size) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true,
                entityTag: new EntityTagHeaderValue($"\"{hash}\""));
        });
    }
}
