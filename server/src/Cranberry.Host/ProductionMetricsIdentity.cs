using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Host.Config;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Host;

public static class ProductionMetricsIdentity
{
    public static object Create(CranberryConfig config)
    {
        // Only known gameplay blocks, numeric/bool settings and these named presets are hashed.
        // Root paths, names, account stores, console/admin settings and launcher secrets are excluded.
        string[] blocks = ["sky", "gas", "movement", "descent", "drop", "loot", "vehicles", "doors",
            "weapons", "combat", "ammo", "crafting", "match", "lobby", "bounty", "menu", "transport", "features", "dev", "peers"];
        string[] presets = ["sky.preset", "gas.preset", "movement.preset", "descent.preset"];
        var settings = ConfigKeys.All.Where(key => blocks.Contains(key.Block)
                && (key.Kind != ConfigKind.Text || presets.Contains(key.Path)))
            .OrderBy(key => key.Path, StringComparer.Ordinal)
            .ToDictionary(key => key.Path, key => key.Effective(config), StringComparer.Ordinal);
        Type[] types = [typeof(ProductionMetricsIdentity), typeof(ZoneService), typeof(SoeListener), typeof(LoginService),
            typeof(LauncherHost), typeof(GameInstaller), typeof(PacketWriter)];
        var builds = types.Select(type => type.Assembly).Distinct().OrderBy(assembly => assembly.GetName().Name)
            .ToDictionary(assembly => assembly.GetName().Name!, assembly =>
            {
                using var file = File.OpenRead(assembly.Location);
                return Convert.ToHexString(SHA256.HashData(file));
            });
        return new
        {
            nodeId = config.Metrics.NodeId, role = "host", processId = Environment.ProcessId,
            runId = Guid.NewGuid().ToString("N"), clientVersion = "0.0.118.208059",
            runtimeVersion = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            assemblySha256 = builds,
            configurationSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings))),
            configurationScope = "gameplay-numeric-bool-and-four-presets-v1; excludes paths, ports, identities, secrets and external launcher config",
        };
    }
}
