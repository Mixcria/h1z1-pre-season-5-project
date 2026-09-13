namespace Cranberry.Zone.Appearance;

/// <summary>One <c>Models.txt</c> row id the server names, and what named it.</summary>
/// <param name="ModelId">The id put on the wire.</param>
/// <param name="Origin">Human-readable provenance, e.g. <c>"loot Ammo01 item 1511"</c>.</param>
public readonly record struct AugustModelUse(uint ModelId, string Origin);

/// <summary>
/// The rule docs/54 I2 asks for: <b>every actor id the server names must resolve in the August
/// <c>Models.txt</c> and the file it names must actually be in the client's asset packs.</b>
/// <para>
/// This is not theoretical. <c>Models.txt</c> row 10137 <c>Common_Props_AmmoBoxes_Shotgun.adr</c>
/// is named by the sheet and ships in none of the 256 <c>Assets_*.pack</c> archives - no
/// <c>.adr</c>, <c>.dme</c>, <c>.dma</c>, <c>.cdt</c> or texture of that name exists. Cranberry
/// sent it for every 12-gauge ammunition box, and the owner's own client answered with
/// <c>Failed to load asset 'Common_Props_AmmoBoxes_Shotgun.adr'</c> ten times and
/// <c>Actor definition ... failed to load!</c> in the same second (docs/54 s2.3, LIVE-VERIFIED).
/// On the floor that is the "white" shotgun box in the owner's report.
/// </para>
/// <para>
/// docs/54 s1.4 is what makes this the <em>whole</em> fix for a ground prop's colour:
/// <c>AddLightweightNpc</c> carries no shader group and no appearance ids, and every ground
/// <c>.adr</c> in the roster declares one texture alias and an empty <c>&lt;TintAliases/&gt;</c>,
/// so a ground item's colour is decided entirely by its <c>Models.txt</c> row. Choosing the row is
/// the only lever the server has, and this guard is what keeps the choice honest.
/// </para>
/// </summary>
public static class AugustModelGuard
{
    /// <summary>
    /// <c>Common_Props_AmmoBox02.adr</c> - the olive-drab ammunition can, colour map mean RGB
    /// (78, 99, 65), dominant (80, 112, 80). The client's own KotK level-design markers
    /// <c>ItemSpawner_AmmoBox02_M16A4.adr</c> and
    /// <c>ItemSpawner_BattleRoyale_AmmoBox02_M16A4.adr</c> are both built on this mesh, and the
    /// M16A4 is the AR-15's own actor family - so the client's designers paired .223 with this
    /// box, which is independently what the owner asked for ("the AR-15 should have green boxes").
    /// docs/54 s2.2.
    /// </summary>
    public const uint OliveAmmunitionBoxModelId = 10;

    /// <summary>
    /// <c>Common_Props_AmmoBoxe01.adr</c> - the mesh the client's own
    /// <c>ItemSpawner_AmmoBox02_12GaShotgun.adr</c> and <c>..._308Rifle.adr</c> markers are built
    /// on. Present in the packs; colour map mean RGB (225, 109, 40). docs/54 s2.3.
    /// </summary>
    public const uint ShotgunAmmunitionBoxModelId = 8023;

    /// <summary>
    /// <c>Common_Props_AmmoBoxes_AK47.adr</c> - tan cardboard, mean RGB (94, 77, 54). Correct for
    /// 7.62x39 and for nothing else; sending it for .223 is what made the AR-15's boxes brown
    /// (docs/54 s2.2, reversing docs/39 s3.5's recorded judgement call).
    /// </summary>
    public const uint AkAmmunitionBoxModelId = 10132;

    /// <summary>
    /// <c>Common_Props_AmmoBoxes_Shotgun.adr</c> - named by <c>Models.txt</c>, shipped by nothing.
    /// Retained as a named constant only so a test can assert it is never used again.
    /// </summary>
    public const uint AbsentShotgunAmmunitionBoxModelId = 10137;

    /// <summary>
    /// The ids in <paramref name="used"/> the August client cannot draw, each with an explanation.
    /// Empty is the passing result.
    /// </summary>
    public static IReadOnlyList<string> Unloadable(IEnumerable<AugustModelUse> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        var failures = new List<string>();
        foreach (AugustModelUse use in used)
        {
            if (AugustModelCatalog.CanLoad(use.ModelId))
            {
                continue;
            }

            failures.Add($"{use.Origin}: {Explain(use.ModelId)}");
        }

        return failures;
    }

    /// <summary>Why one id is or is not usable, in words a failing test can print.</summary>
    public static string Explain(uint modelId)
    {
        if (!AugustModelCatalog.TryGet(modelId, out AugustModelRow row))
        {
            return $"model {modelId} is not a row in the August Models.txt "
                + $"({AugustModelCatalog.RowCount} rows)";
        }

        return row.Ships
            ? $"model {modelId} '{row.FileName}' is present in the client's asset packs"
            : $"model {modelId} names '{row.FileName}', which is in none of the 256 "
                + "Assets_*.pack archives - the client logs \"Failed to load asset\" and draws "
                + "nothing usable (docs/54 s2.3)";
    }
}
