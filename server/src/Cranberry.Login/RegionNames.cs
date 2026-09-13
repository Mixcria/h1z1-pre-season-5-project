namespace Cranberry.Login;

// docs/105 §9 (MENU-RETAIL-GAP U-7) — the `ServerInfo Region` attribute is a LOCALE KEY, not a
// two-letter code.
//
// THE DEFECT. `C:\Aug2017\Client\Logs\StringLookup.log` records the August client failing
//     Code string mapping or T4 string name for 'EU' not found!
// fifteen times in the owner's 2026-09-02 run, on a five-second cadence for as long as the menu is
// open (lines 79-80 and 88-100). The message's format string is at VA 0x14311fa00 in
// `H1Z1.exe` 0.0.118.208059 and names the two-stage lookup the client does: first the
// CodeStringMappings table, then the T4 string names. `EU` is in neither, so the widget draws
// nothing and complains again on the next refresh.
//
// THE RETAIL FORM. The owner's own 2026-08-22 admin capture of the friend's live 2017 server
// carries the answer in the login link `1115:58316`, `ServerListReply` (op 0x0e, 901 bytes,
// `packets_1115_58316.log` line 7, and again at +20.9 s on line 15). Its two `ServerInfo`
// documents both read:
//     <ServerInfo Region="CharacterCreate.RegionEu" Subregion="UI.SubregionEu"
//                 IsRecommended="1" IsRecommendedVS="0" IsRecommendedNC="0" IsRecommendedTR="0" />
// So the field carries the name of a string, and the client resolves it.
//
// WHY THAT RESOLVES AND `EU` DOES NOT. The client's own `CodeStringMappings.txt` (extracted with
// this project's Pack1 tool to `C:\Aug2017\out\data_aug\CodeStringMappings.txt`) holds exactly five
// region rows — `CharacterCreate.RegionUs^347^`, `CharacterCreate.RegionEu^9579^`,
// `CharacterCreate.RegionAu^11022^`, `CharacterCreate.RegionBra^14728^`,
// `CharacterCreate.RegionAsia^15074^` — and no row named `EU`, `US`, `AU`, `SA` or `AS`. Stage one
// of the lookup therefore turns `CharacterCreate.RegionEu` into datasheet string id 9579; the
// locale helper hashes `Global.Text.9579` with Jenkins lookup2 to key 3,725,598,107, which is a
// live record in the client's own `Locale\en_us_data.dat` whose text is `EU`
// (`python tools/locale/localedat.py text 9579` → `9579  3725598107  EU`). The two letters the
// panel is meant to show are what comes back — the visible result is identical, and the failure
// stops because the name now exists.
//
// The names are the client's own beyond the datasheet, too: `H1Z1.exe` carries the literals
// `CharacterCreate.RegionUs` / `...RegionEu` at VA 0x143246210 (referenced from 0x141434aa8) and
// the UI-binding forms `UiBinding_CharacterCreate.RegionUs` / `...RegionEu` at VA 0x1431e4144.
//
// SUBREGION IS DELIBERATELY LEFT EMPTY. The friend server sends `Subregion="UI.SubregionEu"`, but
// that string appears nowhere in `H1Z1.exe` and there is no `UI.Subregion*` row in this build's
// `CodeStringMappings.txt`. Copying it would trade one failed lookup for another, so Cranberry
// keeps `Subregion` empty (its value today) until a build is found that defines it.

/// <summary>
/// How the login list names a region in its <c>ServerInfo</c> document.
/// </summary>
public enum RegionNaming
{
    /// <summary>The bare two-letter codes Cranberry sent before 2026-09-03. The client cannot
    /// resolve them and writes a <c>StringLookup.log</c> line every five seconds.</summary>
    RawCodes,

    /// <summary>The client's own <c>CodeStringMappings</c> names, as the friend server sends
    /// them.</summary>
    LocaleKeys,
}

/// <summary>docs/105 §9 — switchable, default <see cref="RegionNaming.LocaleKeys"/>.</summary>
public sealed record RegionNamingOptions
{
    public const string Variable = "CRANBERRY_LOGIN_REGION_KEYS";

    /// <summary>Default ON: send the resolvable key.</summary>
    public RegionNaming Naming { get; init; } = RegionNaming.LocaleKeys;

    public static RegionNamingOptions Default { get; } = new();

    /// <summary>
    /// <c>CRANBERRY_LOGIN_REGION_KEYS=0</c> (or <c>off</c> / <c>raw</c>) restores the bare codes.
    /// An unrecognised value keeps the default rather than failing a boot.
    /// </summary>
    /// <param name="read">
    /// Usually <c>Environment.GetEnvironmentVariable</c>; the host passes
    /// <c>CranberryConfig.Read</c> so <c>cranberry.json</c> can set it too (lane 0D).
    /// </param>
    public static RegionNamingOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        string? raw = read(Variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Default;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "no" or "false" or "raw" or "codes" =>
                Default with { Naming = RegionNaming.RawCodes },
            _ => Default,
        };
    }

    /// <summary>The value to put in <c>ServerInfo Region</c> for a two-letter region code.</summary>
    public string Resolve(string code) =>
        Naming == RegionNaming.LocaleKeys ? RegionNames.LocaleNameFor(code) : code;

    public string Describe() =>
        Naming == RegionNaming.LocaleKeys
            ? "login regions: locale keys (Europe → CharacterCreate.RegionEu → string id 9579 → "
                + "\"EU\"); the client's t4lookup('EU') failures stop; "
                + "CRANBERRY_LOGIN_REGION_KEYS=0 restores the bare codes"
            : "login regions: bare two-letter codes (CRANBERRY_LOGIN_REGION_KEYS=0) — the client "
                + "writes a StringLookup.log failure every five seconds";
}

/// <summary>
/// Two-letter region code → the client's own <c>CodeStringMappings</c> name for that region.
/// </summary>
public static class RegionNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // code -> CodeStringMappings MESSAGE_NAME (string id, en_us text)
        ["EU"] = "CharacterCreate.RegionEu",     //  9579  "EU"   — the capture's own value
        ["US"] = "CharacterCreate.RegionUs",     //   347  "US"
        ["AU"] = "CharacterCreate.RegionAu",     // 11022  "AU"
        ["SA"] = "CharacterCreate.RegionBra",    // 14728  "BRA"  — the only South-American row
        ["AS"] = "CharacterCreate.RegionAsia",   // 15074  "Asia"
    };

    /// <summary>Every code this table can name, in the order the login list sends them.</summary>
    public static IReadOnlyCollection<string> Codes { get; } = ["EU", "US", "SA", "AS", "AU"];

    /// <summary>
    /// The resolvable name, or the code unchanged when there is no row for it — an unknown code is
    /// no worse off than it is today.
    /// </summary>
    public static string LocaleNameFor(string code) =>
        code is not null && Names.TryGetValue(code, out string? name) ? name : code ?? string.Empty;
}
