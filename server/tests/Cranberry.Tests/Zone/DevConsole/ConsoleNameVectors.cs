namespace Cranberry.Tests.Zone.DevConsole;

/// <summary>
/// The 51 names Cranberry's console pushes with <c>Command.AddWorldCommand</c>, with the hash the
/// August client will compute for each (design Appendix A, computed 2026-09-02 with the byte-exact
/// Python model <c>out\devconsole-20260901\hash-check.py</c>).
/// <para>
/// They are pinned in one place because two different tests need them for two different reasons:
/// <see cref="CommandHashTests"/> asserts the number a rename would change, and
/// <see cref="ClientRegistry1148Tests"/> asserts that not one of them is a name the client already
/// owns - a collision there means the client refuses the name and it can never be typed.
/// </para>
/// </summary>
internal static class ConsoleNameVectors
{
    /// <summary>Name and the client's hash of it, in the design's grouping order.</summary>
    public static readonly IReadOnlyList<(string Name, uint Hash)> Appendix =
    [
        // menu verbs
        ("m", 0x5a1fb42au), ("menu", 0x69c7d0eeu), ("d", 0xda97351bu), ("u", 0xec3b585fu),
        ("s", 0xca8b9500u), ("b", 0xb7346e56u), ("q", 0xa51fca29u), ("r", 0xdc49b87cu),
        ("commands", 0x7b3d2451u),

        // player
        ("where", 0x9bbe36b2u), ("tp", 0x81d924e9u), ("teleport", 0x12dd8283u), ("up", 0x6229646au),
        ("chute", 0xf4b31337u), ("heal", 0xa3dbd14au), ("hurt", 0xd4ea55c2u), ("kill", 0x1361eab5u),
        ("godmode", 0x969e8ebbu), ("speed", 0x16c6a2ddu),

        // items
        ("give", 0x1428aa1bu), ("kit", 0x830e3284u), ("drop", 0x4bafc213u), ("inv", 0x5b12c306u),

        // vehicles
        ("car", 0xbb1ec224u), ("spawncar", 0xefd50387u), ("enter", 0x547faeecu), ("exit", 0x137cf9cfu),
        ("fuel", 0x400c2171u), ("cars", 0x49bb13bfu),

        // loot / match
        ("loot", 0x66f583b6u), ("match", 0xe5954130u), ("gas", 0xc8f3c022u), ("startmatch", 0x59cae7a1u),
        ("endmatch", 0xdde1beedu), ("lobby", 0x59d94bfbu), ("matchstatus", 0x1dc1977bu),
        ("kotkdrop", 0xd2a177d9u),

        // world
        ("doors", 0xfebdf77du), ("target", 0x3861124bu), ("win", 0x19c89f3au),

        // players
        ("players", 0xc304bddcu), ("announce", 0x8b500e5fu), ("evict", 0xa206333bu), ("tier", 0x7c85714du),
        ("tphere", 0xb3d4c7a8u),

        // debug / info
        ("dump", 0x032080ddu), ("raw", 0x01388e2fu), ("watchdog", 0x17555abbu), ("log", 0x1fb5b3ecu),
        ("info", 0x9874b818u), ("surface", 0x998c7da4u),
    ];

    /// <summary>xunit theory rows: name, expected hash.</summary>
    public static TheoryData<string, uint> Rows()
    {
        var data = new TheoryData<string, uint>();
        foreach ((string name, uint hash) in Appendix)
        {
            data.Add(name, hash);
        }

        return data;
    }
}
