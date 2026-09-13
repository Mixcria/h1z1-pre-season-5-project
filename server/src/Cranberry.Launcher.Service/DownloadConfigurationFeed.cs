using System.Text.Json;
using Cranberry.Launcher.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cranberry.Launcher.Service;

public static class DownloadConfigurationFeed
{
    public static void Map(WebApplication app, string root)
    {
        string path = Path.Combine(Path.GetFullPath(root), "download-host.json");
        app.MapGet("/api/downloads", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!File.Exists(path)) return DownloadConfiguration.Default;
            // This small, public routing document contains no credentials. Read on demand
            // so an atomic replacement switches downloads without restarting game sessions.
            using var input = File.OpenRead(path);
            if (input.Length > 8192) throw new InvalidDataException("Download configuration is too large.");
            return (JsonSerializer.Deserialize<DownloadConfiguration>(input, DownloadConfiguration.Json)
                ?? throw new InvalidDataException("Download configuration is empty.")).Validate();
        });
    }
}
