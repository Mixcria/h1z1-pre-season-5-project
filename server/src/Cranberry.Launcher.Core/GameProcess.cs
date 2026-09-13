using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cranberry.Launcher.Core;

public static partial class GameProcess
{
    public static ProcessStartInfo StartInfo(string directory, GameLaunch launch)
    {
        if (!Regex.IsMatch(launch.Ticket, "^cb1\\.[A-Fa-f0-9]{64}$")
            || !Regex.IsMatch(launch.LoginAddress, @"^[a-zA-Z0-9.\-]+:[0-9]{1,5}$")
            || !int.TryParse(launch.LoginAddress.Split(':')[^1], out int port) || port is < 1 or > 65535)
            throw new InvalidDataException("Server returned invalid game launch details.");
        string configPath = GameInstaller.SafePath(directory, "CranberryClient.ini");
        string original = GameInstaller.SafePath(directory, "ClientConfig.ini");
        string config = File.Exists(original) ? File.ReadAllText(original) : DefaultConfig;
        if (Regex.IsMatch(config, @"(?m)^Server=.*$")) config = Regex.Replace(config, @"(?m)^Server=.*$", "Server=" + launch.LoginAddress);
        else config = "Server=" + launch.LoginAddress + "\r\n" + config;
        config = WithStartupLogging(config);
        Directory.CreateDirectory(GameInstaller.SafePath(directory, "Logs"));
        File.WriteAllText(configPath, config);
        var info = new ProcessStartInfo(GameInstaller.SafePath(directory, "H1Z1.exe"))
        { WorkingDirectory = Path.GetFullPath(directory), UseShellExecute = false };
        foreach (string arg in new[] { "inifile=CranberryClient.ini", "sessionid=" + launch.Ticket,
            "Internationalization:Locale=en_US", "LaunchPad:Ufp=0", "LaunchPad:SessionId=0", "LaunchPad:Locale=en_US" })
            info.ArgumentList.Add(arg);
        return info;
    }

    private static string WithStartupLogging(string config)
    {
        // The verified startup helpers read TransitionClientRunState from the local game log.
        // Level 1 suppresses it and leaves the server waiting for doors-ready indefinitely.
        const string section = @"(?ims)^\[Logging\][^\r\n]*\r?\n(?<body>.*?)(?=^\[|\z)";
        if (!Regex.IsMatch(config, section)) return config + "\r\n[Logging]\r\nAddress=\r\nLocalLogLevel=9\r\n";
        return Regex.Replace(config, section, match =>
        {
            string body = match.Groups["body"].Value;
            body = Regex.Replace(body, @"(?im)^\s*LocalLogLevel\s*=[^\r\n]*\r?\n?", "");
            return "[Logging]\r\nLocalLogLevel=9\r\n" + body;
        });
    }

    public const string DefaultConfig = """
World=None
Server=127.0.0.1:20042
usenewui=1
SessionId=0

[Environment]
Sku=2
[GameSettings]
FirstPerson=1
[Paths]
PathScripts=.\Resources\Scripts\
PathUiModules=.\UI\UiModules\
[SoeData]
FilesystemRoot=.
[Libraries]
GraphicsDataPath=GraphicsData
[AssetDelivery]
DirectThreadCount=3
DirectEnabled=1
IndirectEnabled=1
AdditionalPaths=.\CommonData\;.\GraphicsData\
PackFileDir=.\Resources\Assets
[LoadingScreen]
LoadingScreenMusicId=16313
[CrashReporter]
Address=127.0.0.1:15081
NoUploadFromInit=1
[WallOfData]
Collecting=0
[Logging]
Address=
LocalLogLevel=9
[InfiniteLoopMonitor]
TimeoutSeconds=60
[LodBins]
Character=5,20,50
Vehicle=4,16,40
""";
}
