using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cranberry.Host.Config;

/// <summary>
/// Generates the two documents that must never drift from <see cref="ConfigKeys"/>:
/// <c>cranberry.json.example</c> (every key at its shipped default, one <c>_comment</c> per block)
/// and <c>docs/110-config.md</c> (every key with its legacy environment name, default and D-row).
/// <para>
/// Both are committed files, and <c>CranberryConfigDocumentationTests</c> asserts the committed
/// text still equals what this class produces — so adding a switch without documenting it fails
/// the build rather than the next play-test.
/// </para>
/// </summary>
public static class ConfigDocs
{
    /// <summary>Switches deleted in lane 0D, with the reason and the ruling that settles it.</summary>
    public static IReadOnlyList<(string Switch, string Ruling, string Fate)> Removed { get; } =
    [
        ("CRANBERRY_GAS_CENTRE_ON_SPAWN", "D23",
            "bring-up only, and \"must never be set for a real match\" — the play area's centre and radius have been ordinary gas.centreX / gas.centreZ / gas.initialRadiusM knobs since docs/77 §8"),
        ("CRANBERRY_RESEND_ZONE_DETAILS", "D19",
            "the LGT-H3 A/B; it added a packet to the zoning burst docs/32 proves is fragile, and it was never once turned on. The winning behaviour — send zone details once — is now the only behaviour"),
        ("CRANBERRY_DOOR_RESOLVE_M", "D78",
            "the 6 m positional fallback for an unrecognised interact guid. D78 settled it: doors resolve by guid only. Now a constant 0"),
        ("CRANBERRY_INTERACTION_STRING_ORDER", "D38",
            "\"unknown, possibly fatal\": the alternative writes a locale id into the entry-count word and walks the client off the end of a 19-byte buffer. The derived order is now the only order"),
        ("CRANBERRY_NPC_COMPONENT", "D71",
            "superseded by the August proximity predicate: ground items always receive ClientNpcComponent with IsWorldItem=true; the optional switch stays removed (docs/loot-proximity-20260906.md)"),
        ("CRANBERRY_GAS_PHASE_WINDOW_MS", "D43",
            "inert under the shipped SpeedPaced pacing and documented as inert since docs/53; the knob only ever produced a rejection note"),
        ("CRANBERRY_CRAFTING_SELFRECORD", "D289",
            "the experiment is concluded FOR: DIAG-recipes-vehicles §A proved the crafting window reads its rows only from the self record's 0x11a list, so stage 2 is the delivery. The field keeps its ruling value (now true) as a constant"),
        ("CRANBERRY_CRAFT_COUNTS", "D48", "concluded experiment; the field keeps its ruling value (false) as a constant"),
        ("CRANBERRY_CRAFT_SENTINEL", "D48", "concluded experiment; the field keeps its ruling value (false) as a constant"),
        ("CRANBERRY_CRAFT_SEED", "D48", "concluded experiment; the field keeps its ruling value (false) as a constant"),
        ("--rc4probe (with ProbeService.cs)", "-",
            "a one-off keystream-offset diagnostic from the 2026-08-27 bring-up, referenced by nothing"),
    ];

    /// <summary>The committed <c>cranberry.json.example</c>, generated from the defaults.</summary>
    public static string ExampleJson()
    {
        CranberryConfig defaults = CranberryConfig.Defaults();
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "_comment",
                "Every key at its shipped default. Delete what you do not override — an absent key, "
                    + "an absent block and an absent file all mean \"the default\". Precedence is "
                    + "defaults < this file < environment, and the legacy CRANBERRY_* name in the "
                    + "table of docs/110-config.md always wins, so every one-word revert still works.");

            foreach (string block in ConfigKeys.Blocks)
            {
                writer.WritePropertyName(block);
                writer.WriteStartObject();
                writer.WriteString("_comment", ConfigKeys.BlockComments[block]);
                foreach (ConfigKey key in ConfigKeys.All.Where(k => k.Block == block))
                {
                    writer.WritePropertyName(key.Key);
                    Write(writer, key, key.Effective(defaults));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\r\n") + "\r\n";
    }

    /// <summary>The committed <c>docs/110-config.md</c>, generated from the same table.</summary>
    public static string Markdown()
    {
        CranberryConfig defaults = CranberryConfig.Defaults();
        var text = new StringBuilder();

        void Line(string line = "") => text.Append(line).Append("\r\n");

        Line("# 110 — `cranberry.json`: every setting, its legacy switch, its default and its ruling");
        Line();
        Line("Lane 0D of the overhaul plan (§3 Phase 0, S1 §2.4). One file replaces the environment-switch");
        Line("sprawl that had grown to 122 names in `Program.cs` and the option records, **without taking a");
        Line("single one-word revert away from the owner**.");
        Line();
        Line("## How a value is decided");
        Line();
        Line("1. **The option record's own default** — unchanged, and still the thing every test pins.");
        Line("2. **`cranberry.json`** — `<root>\\cranberry.json`, or the first `.json` positional argument,");
        Line("   or `CRANBERRY_CONFIG`. **An absent file is not an error**: it means every default.");
        Line("3. **The environment** — the exact legacy name in the tables below (highest), then the");
        Line("   generated overlay name `CRANBERRY_<BLOCK>_<KEY>`.");
        Line();
        Line("The host's **positional arguments still win over both** for root, ports, seed character, zone,");
        Line("bootstrap delay, auto-match, appearance source and dev loot, because `run-host.ps1` passes seven");
        Line("of them on every launch. A key this build has never heard of is ignored with a note; an");
        Line("environment name this table has never heard of still reaches the process environment untouched,");
        Line("so a lane that adds a switch does not have to wait for a row here.");
        Line();
        Line("With no file and no environment every bound record is its own shipped default instance, which is");
        Line("what keeps the golden transcript (`MatchZoningMatchesTheKnownGoodCaptureOpcodeOrder`,");
        Line("`MatchZoningPacketLengthsMatchTheKnownGoodCapture`, `ZoneBootstrapTests`) byte-identical.");
        Line();
        Line("## The boot line");
        Line();
        Line("The effective configuration is written to the host log as **one JSON line** after the existing");
        Line("banner, so every capture in `captures\\` has a machine-readable record of the configuration that");
        Line("produced it (S1 §2.3: \"the env-var convention leaves no record of what a session ran with\"):");
        Line();
        Line("```");
        Line("[info] effective config: {\"root\":{...},\"ports\":{...},\"gas\":{...}, …}");
        Line("```");
        Line();
        Line("## Every key");
        Line();

        foreach (string block in ConfigKeys.Blocks)
        {
            Line($"### `{block}` — {ConfigKeys.BlockComments[block]}");
            Line();
            Line("| key | legacy environment name | default | rules | what it does |");
            Line("|---|---|---|---|---|");
            foreach (ConfigKey key in ConfigKeys.All.Where(k => k.Block == block))
            {
                string overlay = key.Canonical == key.Legacy
                    ? $"`{key.Legacy}`"
                    : $"`{key.Legacy}` (or `{key.Canonical}`)";
                Line($"| `{key.Key}` | {overlay} | `{Display(key, key.Effective(defaults))}` "
                    + $"| {key.Ruling} | {key.Comment} |");
            }

            Line();
        }

        Line("## Removed in lane 0D");
        Line();
        Line("Concluded or superseded experiments, per plan §5.1 (\"delete now with the code path\") and the");
        Line("reason column of S1 §2.2. In every case the **winning behaviour is now a constant**: the switch,");
        Line("the option field where it had one, and the dead branch are gone.");
        Line();
        Line("| switch | rules | why it is gone |");
        Line("|---|---|---|");
        foreach ((string name, string ruling, string fate) in Removed)
        {
            Line($"| `{name}` | {ruling} | {fate} |");
        }

        Line();
        Line("## Not removed, and why");
        Line();
        Line("Plan §5.1 also lists a **fold** set (become a constant, switch deleted) and four preset");
        Line("deletions. They are kept for now, deliberately:");
        Line();
        Line("- **The fold set** (`CRANBERRY_CONTAINERS`, `_MOVEMENT_STATS`, `_BOOTSTRAP_WEAPON_ITEMS`,");
        Line("  `_CLASS_MAPPINGS`, `_QUICK_USE_CONSUMABLES`, `_VEHICLE_RELAY`, the skin rollbacks, the weapon");
        Line("  stages, five of the seven combat switches, `_DOOR_COLLISION`) — the owner's standing");
        Line("  instruction for this lane is that **every one-word revert he has used this week keeps");
        Line("  working**. Folding them removes exactly that. They are now rows in this file instead, which");
        Line("  costs nothing and keeps the bisect lever.");
        Line("- **`CRANBERRY_STARTER_WEAPON`, `_DEV_GROUND_LOOT_MS`, `_WIELD_FIRST_PICKUP`,");
        Line("  `_WIELD_SEQUENCE`, `_PRACTICE_TARGET`** — §5.1 converts these to admin verbs");
        Line("  (`give <item> wield`, `loot ring`, `/target`). `Host/AdminChannel.cs` is lane 0B and does not");
        Line("  exist yet; deleting the switch before its replacement lands would take a lever away with");
        Line("  nothing to put in its place.");
        Line("- **The `Wave4Legacy` / `Wave3Legacy` / `Wave5Legacy` / `Wave4Default` / `Owner30` /");
        Line("  `LegacyD18Grey` presets and `CRANBERRY_GAS_PACING`** — §5.2 freezes \"every `Gas/`, `Loot/`,");
        Line("  `Inventory/`, `Combat/`, `Appearance/`, `Movement/`, `Descent/` value class\", the values are");
        Line("  pinned by `Generated/Rulings.g.cs` (lane 2C owns `rulings/`), and each preset is the A/B for a");
        Line("  play-test the owner has actually run. Removing them is a rulings-lane change, not a config one.");
        Line();
        Line("## Adding a setting");
        Line();
        Line("One row in `src/Cranberry.Host/Config/ConfigKeys.cs`: block, key, the legacy environment name,");
        Line("the kind, the D-row, one line of prose, a reader for the effective value, and a flip value for");
        Line("the per-option test. `cranberry.json.example`, this document and the boot line all follow from");
        Line("it, and `CranberryConfigOptionMatrixTests` immediately asserts that flipping it moves that value");
        Line("**and nothing else**.");

        return text.ToString();
    }

    private static void Write(Utf8JsonWriter writer, ConfigKey key, object? value)
    {
        switch (key.Kind)
        {
            case ConfigKind.Bool:
                writer.WriteBooleanValue(value is true);
                break;
            case ConfigKind.Int:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ConfigKind.Float:
                writer.WriteNumberValue(
                    Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), 4));
                break;
            default:
                writer.WriteStringValue(value?.ToString() ?? string.Empty);
                break;
        }
    }

    private static string Display(ConfigKey key, object? value) => key.Kind switch
    {
        ConfigKind.Bool => value is true ? "true" : "false",
        ConfigKind.Int => Convert.ToInt64(value, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture),
        ConfigKind.Float => Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), 4)
            .ToString(CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty,
    };
}
