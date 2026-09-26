using System.Buffers.Binary;

namespace Cranberry.Zone.Crafting;

/// <summary>
/// RecipeStart framing. Only U16SubWithCount is emitted by the August client and accepted here;
/// the other numeric values remain for source compatibility with earlier diagnostic callers.
/// </summary>
public enum RecipeStartShape
{
    Unrecognised = 0,
    U16SubWithCount,
    U16SubNoCount,
    U8SubWithCount,
    U8SubNoCount,
}

/// <summary>
/// August Command.RecipeStart: u8 09, u16 001a, u32 recipe ID, u32 count (11 bytes).
/// UI binding 0x14143a3e0 calls sender 0x1411b39d0 and serializer 0x1411b41e0, which always
/// writes both four-byte fields. The binding's default argument of one does not make the
/// on-wire count optional. See docs/recipe-start-request-20260926.md for exact-build evidence.
/// </summary>
public readonly record struct RecipeStartRequest(
    uint RecipeId,
    uint Count,
    RecipeStartShape Shape,
    int TrailingBytes)
{
    public const byte Opcode = ZoneOpcodes.CommandBase;
    public const ushort SubOpcode = 0x001a;
    public const byte SubByte = 0x1a;
    public const int MinimumLength = 11;

    /// <summary>Matches the complete native framing; recipe ownership is checked by TryParse.</summary>
    public static bool Matches(ReadOnlySpan<byte> payload) =>
        payload.Length >= MinimumLength
        && payload[0] == Opcode
        && BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]) == SubOpcode;

    /// <summary>
    /// Decodes both mandatory fields and requires a nonzero recipe accepted by the supplied
    /// session catalogue predicate. Count bits are preserved, including zero; gameplay count
    /// policy belongs to the crafting service. Extra bytes are reported without assuming a
    /// strict original-server exhaustion rule.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        Func<uint, bool> isKnownRecipe,
        out RecipeStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(isKnownRecipe);
        request = default;
        if (!Matches(payload)) return false;

        uint recipeId = BinaryPrimitives.ReadUInt32LittleEndian(payload[3..]);
        if (recipeId == 0 || !isKnownRecipe(recipeId)) return false;

        request = new RecipeStartRequest(
            recipeId,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[7..]),
            RecipeStartShape.U16SubWithCount,
            payload.Length - MinimumLength);
        return true;
    }
}