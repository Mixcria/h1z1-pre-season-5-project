using System.Buffers.Binary;

namespace Cranberry.Zone.Crafting;

/// <summary>
/// Which candidate framing of <c>09 1a Command.RecipeStart</c> a payload actually matched.
/// The value is logged with every craft so one live press settles docs/59 §1.7 needle 2 for good.
/// </summary>
public enum RecipeStartShape
{
    /// <summary>Nothing plausible - the caller must log the raw hex.</summary>
    Unrecognised = 0,

    /// <summary><c>09 1a 00 | u32 recipeId | u32 count</c> - 11 bytes. The expected shape.</summary>
    U16SubWithCount,

    /// <summary><c>09 1a 00 | u32 recipeId</c> - 7 bytes; count defaults to 1.</summary>
    U16SubNoCount,

    /// <summary><c>09 1a | u32 recipeId | u32 count</c> - 10 bytes.</summary>
    U8SubWithCount,

    /// <summary><c>09 1a | u32 recipeId</c> - 6 bytes; count defaults to 1.</summary>
    U8SubNoCount,
}

/// <summary>
/// <c>09 1a Command.RecipeStart</c> - the client asking to craft. <b>Tolerant by construction</b>,
/// because the exact field widths are the one thing about crafting the binary will not say.
/// <para>
/// <b>What is proven.</b> The crafting view calls the client command <c>RecipeStart</c>; the
/// command-table entry at <c>0x143247400</c> reaches <c>FUN_14143a3e0</c>
/// (<c>out/wave6-prep/ghidra-recipes3/_14143a3e0_.../FUN_14143a3e0_14143a3e0.c</c>, re-read this
/// pass), which is exactly:
/// </para>
/// <code>
/// recipeId = (argc &gt; 0 &amp;&amp; arg_is_number(1)) ? (int)arg_number(1) : 0;
/// count    = (argc &gt; 1 &amp;&amp; arg_is_number(2)) ? (int)arg_number(2) : 1;   // Craft 1 vs Craft Max
/// if (recipeId != 0) { packet.opcode = 9; packet.sub = 0x1a;
///                     packet.a = recipeId; packet.b = count; send(...); }
/// </code>
/// <para>
/// So the payload carries <c>(recipeId, count)</c> as two 32-bit integers and <b>a craft is never
/// requested for recipe id 0</b>. What is <b>[BLOCKED]</b> is whether the sub is serialised as a u8
/// or a u16 and whether <c>count</c> is on the wire at all - the Command family has no per-packet
/// serializer to decompile, the same wall docs/36 §4a hit for <c>09 07 InteractRequest</c> and
/// docs/43 §5.3 for <c>70 0a SeatChangeRequest</c>. Cranberry's other Command readers all use a u16
/// sub because that is what the live <c>09 07 00</c> and <c>09 15 00</c> captures show, so
/// <see cref="RecipeStartShape.U16SubWithCount"/> is the leading candidate - but "leading" is not
/// "known", and a reader that only accepts the leading candidate turns a wrong guess into silence.
/// </para>
/// <para>
/// <b>How the ambiguity is resolved without guessing.</b> <see cref="TryParse"/> is handed a
/// predicate that says whether a recipe id exists on this session. It tries the four candidate
/// framings and accepts the first whose decoded id is a <em>known</em> recipe. Every recipe id
/// Cranberry ships is its output item's definition id (2423, 3375, 3378, 93) and none of those is a
/// multiple of 256, so the u16 and u8 framings can never both decode to a known id from the same
/// bytes: the third byte is <c>0x00</c> in the u16 framing and the low byte of a non-multiple-of-256
/// id in the u8 framing. The match is therefore unambiguous for this recipe set, and the shape it
/// matched is reported so the log records which framing the client really uses.
/// </para>
/// </summary>
public readonly record struct RecipeStartRequest(
    uint RecipeId,
    uint Count,
    RecipeStartShape Shape,
    int TrailingBytes)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;

    /// <summary>The sub as a u16, which is how every other Cranberry Command reader frames it.</summary>
    public const ushort SubOpcode = 0x001a;

    /// <summary>The sub as a bare byte, for the alternative framing.</summary>
    public const byte SubByte = 0x1a;

    /// <summary>The shortest payload any candidate framing can occupy.</summary>
    public const int MinimumLength = 6;

    /// <summary>
    /// True when the payload opens <c>09 1a</c> and is long enough for the shortest candidate. Use
    /// this as the dispatcher guard: it never consumes the packet, so a payload that matches here
    /// and then fails <see cref="TryParse"/> is exactly the packet whose hex must be logged.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= MinimumLength && payload[0] == Opcode && payload[1] == SubByte;

    /// <summary>
    /// Decode a <c>09 1a</c> payload.
    /// </summary>
    /// <param name="payload">The whole zone payload, opcode byte included.</param>
    /// <param name="isKnownRecipe">
    /// Whether a decoded id names a recipe this session has delivered. Passing
    /// <c>_ =&gt; false</c> forces the leading candidate and reports
    /// <see cref="RecipeStartShape.Unrecognised"/> for everything else, which is the right
    /// behaviour for a byte-level test but the wrong one for the wire.
    /// </param>
    /// <param name="request">The decoded request; <c>default</c> on failure.</param>
    /// <returns>
    /// True when a candidate framing decoded to a known recipe id. False means the caller must log
    /// the full hex - that log line is the whole of docs/59 §1.7 needle 2's answer.
    /// </returns>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        Func<uint, bool> isKnownRecipe,
        out RecipeStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(isKnownRecipe);
        request = default;
        if (!Matches(payload))
        {
            return false;
        }

        // Order matters only for the log line, not for correctness: at most one candidate can name
        // a known recipe (see the class remarks).
        if (TryShape(payload, 3, withCount: true, RecipeStartShape.U16SubWithCount, isKnownRecipe, out request)
            || TryShape(payload, 2, withCount: true, RecipeStartShape.U8SubWithCount, isKnownRecipe, out request)
            || TryShape(payload, 3, withCount: false, RecipeStartShape.U16SubNoCount, isKnownRecipe, out request)
            || TryShape(payload, 2, withCount: false, RecipeStartShape.U8SubNoCount, isKnownRecipe, out request))
        {
            return true;
        }

        request = default;
        return false;
    }

    private static bool TryShape(
        ReadOnlySpan<byte> payload,
        int bodyOffset,
        bool withCount,
        RecipeStartShape shape,
        Func<uint, bool> isKnownRecipe,
        out RecipeStartRequest request)
    {
        request = default;
        int needed = bodyOffset + (withCount ? 8 : 4);
        if (payload.Length < needed)
        {
            return false;
        }

        if (bodyOffset == 3 && payload[2] != 0)
        {
            // A u16 sub of 0x001a is `1a 00`; a third byte that is not zero rules this framing out.
            return false;
        }

        uint recipeId = BinaryPrimitives.ReadUInt32LittleEndian(payload[bodyOffset..]);
        if (recipeId == 0 || !isKnownRecipe(recipeId))
        {
            // FUN_14143a3e0 refuses to send recipe id 0 at all, so a zero here is proof the framing
            // is wrong rather than evidence of a zero-id recipe.
            return false;
        }

        // FUN_14143a3e0's default when the view passes no second argument is 1, not 0.
        uint count = withCount ? BinaryPrimitives.ReadUInt32LittleEndian(payload[(bodyOffset + 4)..]) : 1u;
        if (count == 0)
        {
            count = 1;
        }

        request = new RecipeStartRequest(recipeId, count, shape, payload.Length - needed);
        return true;
    }
}
