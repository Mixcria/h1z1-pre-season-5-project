namespace Cranberry.Zone.Combat;

/// <summary>
/// <b>Lane 1F's switches</b> - everything about where a magazine's rounds come from, and whether the
/// server tells the client when an item's stack count or durability moves.
/// <para>
/// It is a nested record on <see cref="CombatOptions"/> rather than a fourth top-level options object
/// because every one of these knobs changes how <em>shooting</em> plays, and the owner reads one
/// boot banner. Only the exact strings <c>"1"</c> and <c>"0"</c> move a switch, exactly as
/// <see cref="CombatOptions.FromEnvironment"/> does, so a typo leaves the default rather than
/// silently changing a match.
/// </para>
/// </summary>
public sealed record AmmoOptions
{
    /// <summary>Environment switch for <see cref="AmmoFromBag"/>.</summary>
    public const string FromBagVariable = "CRANBERRY_AMMO_FROM_BAG";

    /// <summary>Environment switch for <see cref="GunsSpawnEmpty"/>.</summary>
    public const string GunsSpawnEmptyVariable = "CRANBERRY_GUNS_SPAWN_EMPTY";

    /// <summary>Environment switch for <see cref="SendItemUpdate"/>.</summary>
    public const string ItemUpdateVariable = "CRANBERRY_ITEM_UPDATE";

    /// <summary>
    /// A reload takes its rounds out of the bag and refuses when there are none.
    /// <para>
    /// <b>ON.</b> Off restores D118's world exactly - <c>Reload</c> refills the magazine out of
    /// nothing and no ammunition item is ever touched - which is the control arm for any session
    /// where a reload starts behaving oddly, and the state <c>UnloadWeapon</c> is still refused in.
    /// </para>
    /// </summary>
    public bool AmmoFromBag { get; init; } = true;

    /// <summary>
    /// A weapon this session has never seen before starts with an <b>empty</b> magazine.
    /// <para>
    /// The owner's own <c>ParityGunsSpawnEmpty</c> (S6 §3.1, §4.5), and the August client's own
    /// hint 15204 says the same thing in the retail voice: <i>"That gun isn't going to load itself.
    /// Remember to load your weapon once you pick up some ammo."</i> Off gives every gun its full
    /// <c>CLIP_SIZE</c> the first time it is touched, which is what Cranberry did before this lane.
    /// </para>
    /// <para>
    /// It never touches the <c>ItemAdd</c> tail: the client's trigger gate needs the fire mode's
    /// charge to be greater than zero (S5c §5.4 step 5), so an empty gun is empty in the
    /// <em>server's</em> magazine, not in the record that arms the weapon.
    /// </para>
    /// </summary>
    public bool GunsSpawnEmpty { get; init; } = true;

    /// <summary>
    /// Send <c>11 03 ClientUpdate.ItemUpdate</c> when an item's stack count or durability moves.
    /// <para>
    /// <b>ON, because the body is derived rather than guessed</b> - 73 bytes, no item-class tail,
    /// from the August client's own reader <c>FUN_140a65960</c> and applier <c>FUN_141479ae0</c>
    /// (docs/102 §3). Off, the ammunition stack is still spent server-side and the client simply is
    /// not told; the tile then lags until the next full re-announce.
    /// </para>
    /// </summary>
    public bool SendItemUpdate { get; init; } = true;

    /// <summary>
    /// Durability a weapon loses per accepted shot. <b>5, the owner's own number</b>
    /// (S6 §4.5: <c>CurrentDurability −5</c>, floor 0, and the gun is never removed when it
    /// reaches 0).
    /// </summary>
    public int DurabilityLossPerShot { get; init; } = 5;

    /// <summary>
    /// The durability a weapon starts and tops out at.
    /// <para>
    /// <b>A Cranberry design value, not a derived one</b>, and labelled as one for the same reason
    /// <see cref="CombatOptions.UnmappedWeaponBodyUnits"/> is: the August extraction ships
    /// <b>no</b> durability column anywhere in <c>ClientItemDefinitions.txt</c>, so there is nothing
    /// to read. 1,000 with the owner's −5 gives 200 shots, which is longer than any match.
    /// </para>
    /// </summary>
    public int MaxDurability { get; init; } = 1_000;

    /// <summary>The shipped defaults.</summary>
    public static AmmoOptions Default { get; } = new();

    /// <summary>Reads the three switches from the environment.</summary>
    public static AmmoOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return new AmmoOptions
        {
            AmmoFromBag = Switch(read, FromBagVariable, @default: true),
            GunsSpawnEmpty = Switch(read, GunsSpawnEmptyVariable, @default: true),
            SendItemUpdate = Switch(read, ItemUpdateVariable, @default: true),
        };
    }

    /// <summary>The boot-banner line, so a play-test can never guess which arms ran.</summary>
    public string Describe() =>
        $"ammo: fromBag={On(AmmoFromBag)} gunsSpawnEmpty={On(GunsSpawnEmpty)} "
        + $"itemUpdate 11 03={On(SendItemUpdate)} durability=-{DurabilityLossPerShot}/shot "
        + $"of {MaxDurability}";

    private static string On(bool value) => value ? "ON" : "off";

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
