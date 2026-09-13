using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Crafting;

/// <summary>
/// The switches that decide how much of crafting is live, and the reasoning for each default.
/// <para>
/// <b>Why crafting ships in two delivery stages rather than one.</b> docs/59's headline finding is
/// that <c>SendSelfToClient</c> blob offset <c>0x11a</c> already carries the recipe list and
/// <c>SelfRecord.cs</c> has always written it as <c>00 00 00 00</c>, so delivery costs one existing
/// field. That is true and it is the cheapest path - but it is also the <em>riskiest</em> place in
/// the whole server to be wrong. The self-record loader ends with
/// <c>if (streamError || cursor - base &lt; length) FUN_1409e1080()</c>, and <c>FUN_1409e1080</c>
/// writes <c>0xbadbeef</c> to address zero: a single mis-sized recipe record does not produce an
/// empty crafting tab, it produces a client that cannot log in at all. The owner is away and cannot
/// play-test.
/// </para>
/// <para>
/// <c>0x26 09 Recipe.List</c> carries <b>byte-for-byte the same records</b> - both go through
/// <c>FUN_140a54bc0</c> and both end at <c>FUN_141516280</c> - but it arrives on its own after
/// <c>ClientIsReady</c>, where <c>FUN_141518250</c> arm 9 answers a malformed body by returning
/// early and nothing else. So stage 1 sends the records where a mistake costs a missing crafting
/// tab, and stage 2 moves the identical bytes into the login burst only once stage 1 has been seen
/// to work. Turning stage 2 on before stage 1 has been observed live is the one thing in this lane
/// that can break a working server.
/// </para>
/// </summary>
public sealed record CraftingOptions
{
    /// <summary>Environment switch for <see cref="SendRecipeList"/> (stage 1). Default on.</summary>
    public const string RecipeListVariable = "CRANBERRY_CRAFTING";

    /// <summary>Environment switch for <see cref="AllowCrafting"/> (stage 3). Default on.</summary>
    public const string CraftVariable = "CRANBERRY_CRAFT";

    /// <summary>Environment switch for <see cref="RetailRecipes"/> (D256). Default on.</summary>
    public const string RetailRecipesVariable = "CRANBERRY_CRAFT_RECIPES";

    /// <summary>Environment switch for <see cref="CraftCastBar"/> (D257). Default on.</summary>
    public const string CastBarVariable = "CRANBERRY_CRAFT_CAST_BAR";

    /// <summary>Environment switch for <see cref="SendInteractionStop"/> (D258). Default on.</summary>
    public const string InteractionStopVariable = "CRANBERRY_INTERACTION_STOP";

    // CRANBERRY_CRAFTING_SELFRECORD, CRANBERRY_CRAFT_COUNTS, CRANBERRY_CRAFT_SENTINEL and
    // CRANBERRY_CRAFT_SEED were deleted in lane 0D (plan §5.1): four concluded experiments whose
    // alternative is known-bad ("a length error there fails the login") or unused. The four fields
    // survive as ordinary options and keep their D48 ruling values, which is what the generated
    // Rulings table and RulingsTests pin; nothing reads them from the environment any more.

    /// <summary>
    /// <b>STAGE 1 - DEFAULT ON.</b> Send <c>0x26 09 Recipe.List</c> with the whole catalogue once
    /// per session, after <c>ClientIsReady</c>.
    /// <para>
    /// <b>Zero risk to an existing session.</b> The packet is not part of the zoning burst, so it
    /// cannot disturb guard 4's opcode order; <c>FUN_141518250</c> arm 9 validates its own length
    /// and returns early on any error; and no other system reads the recipe manager. The worst case
    /// is a crafting tab that stays empty, which is what happens today anyway.
    /// </para>
    /// <para>
    /// <b>Success is observable on the client, not in the host log</b> (docs/32, D29): the crafting
    /// tab lists four recipes, or the client raises the <c>RecipeLearned</c> toast. A host-log line
    /// saying the packet was sent proves only that it was sent.
    /// </para>
    /// </summary>
    public bool SendRecipeList { get; init; } = Rulings.Crafting.SendRecipeList;

    /// <summary>
    /// <b>STAGE 2 - DEFAULT ON (D289).</b> Fill the <c>SendSelfToClient</c> list at blob offset
    /// <c>0x11a</c> with the recipe records instead of writing a zero count.
    /// <para>
    /// <b>This is the only delivery that populates the crafting WINDOW.</b> DIAG-recipes-vehicles §A
    /// settled it from the binary: the August window reads its rows only from the self record's
    /// <c>0x11a</c> list (<c>FUN_141513580</c>, whose single caller is the <c>SendSelfToClient</c>
    /// sink <c>FUN_140db5680</c>); <c>0x26 09 Recipe.List</c> fills the recipe <em>container</em> and
    /// can never publish a UI row. So the recipes must ride in <em>every</em> self record — the menu
    /// one and the one inside <c>ClientBeginZoning</c> — because <c>FUN_141513580</c> clears the
    /// datasources first, so a later empty self record would wipe a populated window.
    /// </para>
    /// <para>
    /// The stage-2 login risk is retired: the record stream parsed identically twice live on
    /// 2026-09-03 with no disconnect (DIAG §A.3). <see cref="Effective"/> still refuses stage 2 when
    /// stage 1 is off, so the two can never be enabled in the wrong order.
    /// </para>
    /// </summary>
    public bool SendRecipesInSelfRecord { get; init; } = Rulings.Crafting.SendRecipesInSelfRecord;

    /// <summary>
    /// <b>STAGE 3 - DEFAULT ON.</b> Answer <c>09 1a Command.RecipeStart</c> by actually crafting.
    /// <para>
    /// On by default because it cannot fire unless the client has recipes (stage 1) and the player
    /// clicks Craft, and because every packet it emits - <c>ItemDelete</c>, <c>ItemAdd</c>,
    /// <c>Container.Error</c> - is one the ground-pickup path has been sending since wave 2. Turn it
    /// off to deliver the recipe list without letting it change any inventory.
    /// </para>
    /// </summary>
    public bool AllowCrafting { get; init; } = Rulings.Crafting.AllowCrafting;

    /// <summary>
    /// <b>DEFAULT OFF.</b> Send <c>0x26 02 Recipe.ComponentUpdate</c> after each craft so the
    /// ingredient panel's live counts follow the inventory.
    /// <para>
    /// Off because the panel is usable with static counts (docs/59 §1.6 puts this out of the first
    /// cut) and because <see cref="RecipeComponentRecord.LiveCounts"/>'s low/high packing is the one
    /// remaining <b>[INF]</b> in the record: a wrong packing prints a wrong number, and a wrong
    /// number is worse than no number.
    /// </para>
    /// </summary>
    public bool SendComponentCounts { get; init; } = Rulings.Crafting.SendComponentCounts;

    /// <summary>
    /// <b>DEFAULT OFF.</b> Write recognisable sentinels into every record field whose role is not
    /// proven, instead of real values (<see cref="RecipeDefinition.ToSentinelRecord"/>). A
    /// diagnostic for one run, never a shipping mode.
    /// </summary>
    public bool SentinelFields { get; init; } = Rulings.Crafting.SentinelFields;

    /// <summary>
    /// <b>DEFAULT OFF.</b> Put one craft's worth of every ingredient in the player's bag at
    /// bootstrap.
    /// <para>
    /// <b>Why this exists.</b> Three of the four recipes cannot be attempted in a real match today:
    /// Armor Scrap (3499) and Composite Fabric (3500) are produced by shredding and by helmet
    /// destruction, never by looting, and Scrap of Cloth (23) does not appear in
    /// <c>z2-loot-tables.json</c> either - only Duct Tape (134) and Field Bandage (2423) do. Shred
    /// is out of scope this wave (<see cref="ShredTable"/>), so without this switch the only recipe
    /// the owner can test is Procoagulant. This grants the ingredients so the whole feature can be
    /// exercised in one sitting.
    /// </para>
    /// <para>
    /// It is off by default because a bootstrap grant lands inside the <c>ClientIsReady</c> zoning
    /// burst, which docs/32 proves is the most fragile sequence in the server. It is a test switch.
    /// </para>
    /// </summary>
    public bool SeedIngredients { get; init; } = Rulings.Crafting.SeedIngredients;

    /// <summary>
    /// <b>DEFAULT ON (D256).</b> Ship the <b>six recipes the owner's admin capture decodes</b>
    /// instead of the wave-6 design four.
    /// <para>
    /// With this on, <c>0x26 09 Recipe.List</c> reproduces the friend server's 936-byte packet byte
    /// for byte for the same character; with it off, <see cref="CraftingCatalog.LegacyRecipes"/> and
    /// the old 538-byte packet come back unchanged, display fields and all. It is a data switch: no
    /// packet shape moves either way, only which records the list carries.
    /// </para>
    /// </summary>
    public bool RetailRecipes { get; init; } = Rulings.Crafting.RetailRecipes;

    /// <summary>
    /// <b>DEFAULT ON (D257).</b> Answer <c>09 1a Command.RecipeStart</c> with the same
    /// <c>cf 02 InteractionStart</c> cast bar the shred path already sends, run the craft on a timer
    /// when the bar finishes, and refuse a second request inside the window.
    /// <para>
    /// Off restores the wave-6 behaviour exactly: <c>CraftingService.Craft</c> and
    /// <c>ApplyCraft</c> run synchronously inside the request, with no bar, no animation and no
    /// busy window - a craft that completes before the player's finger leaves the mouse button.
    /// </para>
    /// </summary>
    public bool CraftCastBar { get; init; } = Rulings.Crafting.CraftCastBar;

    /// <summary>
    /// <b>DEFAULT ON (D258).</b> Close every completed cast with
    /// <c>cf 03 CharacterState.InteractionStop</c>, twice, the way the friend's server does.
    /// <para>
    /// This is not cosmetic for the shred path: animation 10's <c>InteractionAnimations</c> row is
    /// <c>Action / ActionEnd / EXPIRE_MSEC 2000</c>, so the shred animation is authored to run
    /// <b>2,000 ms</b> while the bar runs 1,000. <c>ActionEnd</c> is what stops it early and
    /// <c>cf 03</c> is the packet that fires it; without it the character keeps shredding for a
    /// second after the item has already changed.
    /// </para>
    /// </summary>
    public bool SendInteractionStop { get; init; } = Rulings.Crafting.SendInteractionStop;

    /// <summary>Everything off - wave-5 behaviour, byte for byte.</summary>
    public static CraftingOptions AllOff { get; } = new()
    {
        SendRecipeList = false,
        // Stage 2 now defaults ON (D289), so "everything off" must say so explicitly rather than
        // lean on the field initializer.
        SendRecipesInSelfRecord = false,
        AllowCrafting = false,
    };

    /// <summary>The shipped default: stage 1 and stage 3 on, everything else off.</summary>
    public static CraftingOptions Default { get; } = new();

    /// <summary>
    /// The options after the ordering rules are applied. Nothing else is altered.
    /// <list type="bullet">
    /// <item>Stage 2 is dropped when stage 1 is off, because the self-record field must never be
    /// the first place a recipe record is tried.</item>
    /// <item>Stage 3 is dropped when the client was never sent any recipes, because a craft
    /// request cannot arrive and answering one would change an inventory the client's crafting
    /// window does not know about.</item>
    /// </list>
    /// </summary>
    public CraftingOptions Effective
    {
        get
        {
            CraftingOptions effective = this;
            if (effective.SendRecipesInSelfRecord && !effective.SendRecipeList)
            {
                effective = effective with { SendRecipesInSelfRecord = false };
            }

            if (effective.AllowCrafting && !effective.SendRecipeList && !effective.SendRecipesInSelfRecord)
            {
                effective = effective with { AllowCrafting = false };
            }

            return effective;
        }
    }

    /// <summary>True when <see cref="Effective"/> had to drop stage 2 - say so on the boot log.</summary>
    public bool SelfRecordRefusedForMissingList => SendRecipesInSelfRecord && !SendRecipeList;

    /// <summary>True when <see cref="Effective"/> had to drop stage 3 - say so on the boot log.</summary>
    public bool CraftingRefusedForMissingRecipes =>
        AllowCrafting && !SendRecipeList && !SendRecipesInSelfRecord;

    /// <summary>
    /// Read the switches from the environment. Only the exact strings <c>"1"</c> and <c>"0"</c>
    /// move a switch; anything else (including <c>"true"</c>) leaves the default, so a typo can
    /// never silently enable the stage that can break a login.
    /// </summary>
    public static CraftingOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return new CraftingOptions
        {
            SendRecipeList = Switch(read, RecipeListVariable, @default: true),
            AllowCrafting = Switch(read, CraftVariable, @default: true),
            RetailRecipes = Switch(read, RetailRecipesVariable, @default: Rulings.Crafting.RetailRecipes),
            CraftCastBar = Switch(read, CastBarVariable, @default: Rulings.Crafting.CraftCastBar),
            SendInteractionStop = Switch(
                read, InteractionStopVariable, @default: Rulings.Crafting.SendInteractionStop),
        };
    }

    /// <summary>The recipe set this configuration ships.</summary>
    public IReadOnlyList<RecipeDefinition> Catalogue => CraftingCatalog.For(RetailRecipes);

    /// <summary>A one-line boot-log description, so a play-test can never guess which stages ran.</summary>
    public string Describe()
    {
        CraftingOptions effective = Effective;
        string refused = SelfRecordRefusedForMissingList
            ? " — SendRecipesInSelfRecord IGNORED: stage 2 needs the recipe list to have been proven first"
            : CraftingRefusedForMissingRecipes
                ? $" — {CraftVariable}=1 IGNORED: nothing delivers recipes, so no craft can be requested"
                : string.Empty;
        return $"crafting: recipeList={On(effective.SendRecipeList)}, "
            + $"selfRecord={On(effective.SendRecipesInSelfRecord)}, "
            + $"craft={On(effective.AllowCrafting)}, "
            + $"componentCounts={On(effective.SendComponentCounts)}, "
            + $"sentinels={On(effective.SentinelFields)}, "
            + $"seedIngredients={On(effective.SeedIngredients)}, "
            + $"retailRecipes={On(effective.RetailRecipes)}, "
            + $"castBar={On(effective.CraftCastBar)}, "
            + $"interactionStop={On(effective.SendInteractionStop)} "
            + $"({effective.Catalogue.Count} recipes){refused}";

        static string On(bool value) => value ? "ON" : "off";
    }

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
