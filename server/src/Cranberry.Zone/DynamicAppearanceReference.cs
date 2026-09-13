using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// Project-authored DynamicAppearance reference data for the base survivor and starter outfit. The table shape
/// is derived from the August client's own loaders; the colour values below are Cranberry's own
/// starter palette and do not come from an older server or captured reference-data blob.
/// </summary>
public static class DynamicAppearanceReference
{
    public const string TypeName = "DynamicAppearanceDefinitions";
    public const uint Float4Type = 2;
    // "CRB" plus a local row number keeps project-authored groups out of the client's low IDs.
    public const uint CranberryShirtGroup = 0x4352_4201;
    public const uint CranberryPantsGroup = 0x4352_4202;
    public const uint CranberryGearGroup = 0x4352_4203;

    private static readonly (string Semantic, Vector4 Value)[] SharedBValues =
    [
        ("BaseTintHighlightB", new Vector4(1f, 1f, 1f, 0.5f)),
        ("BaseTintMidtoneB", new Vector4(0.5f, 0.5f, 0.5f, 0.5f)),
        ("BaseTintShadowB", new Vector4(0f, 0f, 0f, 0.5f)),
    ];

    private static readonly DynamicAppearanceShaderValue[] StarterAppearanceValues =
    [
        .. SkinGroup(662,
            highlight: new Vector4(0.55f, 0.39f, 0.29f, 0.35f),
            midtone: new Vector4(0.24f, 0.13f, 0.08f, 0.35f),
            shadow: new Vector4(0.07f, 0.03f, 0.018f, 0.35f)),
        .. SkinGroup(664,
            highlight: new Vector4(0.82f, 0.68f, 0.56f, 0.35f),
            midtone: new Vector4(0.42f, 0.27f, 0.19f, 0.35f),
            shadow: new Vector4(0.16f, 0.08f, 0.045f, 0.35f)),
        .. SkinGroup(665,
            highlight: new Vector4(1.00f, 0.88f, 0.74f, 0.35f),
            midtone: new Vector4(0.68f, 0.48f, 0.34f, 0.35f),
            shadow: new Vector4(0.30f, 0.16f, 0.09f, 0.35f)),
        .. SkinGroup(666,
            highlight: new Vector4(0.93f, 0.77f, 0.63f, 0.35f),
            midtone: new Vector4(0.54f, 0.35f, 0.24f, 0.35f),
            shadow: new Vector4(0.22f, 0.11f, 0.06f, 0.35f)),

        // Neutral grey starter shirt, cool slate trousers, and
        // charcoal gloves/shoes. The August effect defaults tint alpha to 0.5; keep that material
        // response while recolouring branch A and preserving branch B's neutral detail masks.
        .. ColourGroup(CranberryShirtGroup,
            highlight: new Vector4(0.62f, 0.62f, 0.62f, 0.5f),
            midtone: new Vector4(0.30f, 0.30f, 0.30f, 0.5f),
            shadow: new Vector4(0.060f, 0.060f, 0.060f, 0.5f)),
        .. ColourGroup(CranberryPantsGroup,
            highlight: new Vector4(0.30f, 0.36f, 0.42f, 0.5f),
            midtone: new Vector4(0.12f, 0.15f, 0.19f, 0.5f),
            shadow: new Vector4(0.025f, 0.035f, 0.050f, 0.5f)),
        .. ColourGroup(CranberryGearGroup,
            highlight: new Vector4(0.18f, 0.20f, 0.22f, 0.5f),
            midtone: new Vector4(0.060f, 0.070f, 0.085f, 0.5f),
            shadow: new Vector4(0.010f, 0.014f, 0.020f, 0.5f)),
    ];

    /// <summary>
    /// The starter palette's shader values, in wire order.
    /// <para>
    /// Public since D316: <c>AugustDynamicAppearanceTable</c>'s <b>full</b> mode ships the friend
    /// table's own 2,092 shader groups, four of which (662, 664, 665, 666 - the skin tones) this
    /// palette also defines. Handing the client two sets of parameters for one group is a
    /// duplicate the client's own reader has never been asked to resolve, so the full table keeps
    /// the CAPTURED group and appends only the palette groups the source does not define (the
    /// three <c>0x435242xx</c> Cranberry ones). Doing that needs the values, not the blob.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DynamicAppearanceShaderValue> StarterValues => StarterAppearanceValues;

    private static readonly byte[] StarterAppearancePayload = BuildShaderOnlyBlob(StarterAppearanceValues);

    /// <summary>
    /// Creates the unified starter-appearance reference packet. Later appearance work should extend this one
    /// table rather than sending partial tables, because each reference-data load replaces the
    /// client's existing DynamicAppearance maps.
    /// </summary>
    public static ReferenceData CreateStarterAppearance() =>
        new(TypeName, StarterAppearancePayload.ToArray());

    public static byte[] BuildShaderOnlyBlob(
        IReadOnlyList<DynamicAppearanceShaderValue> shaderValues)
    {
        ArgumentNullException.ThrowIfNull(shaderValues);
        using var writer = new PacketWriter();
        writer.WriteInt32(0); // appearance rows
        writer.WriteInt32(0); // item -> appearance-row maps
        writer.WriteInt32(shaderValues.Count);
        foreach (DynamicAppearanceShaderValue value in shaderValues)
        {
            value.WriteTo(writer);
        }

        return writer.Written.ToArray();
    }

    private static DynamicAppearanceShaderValue[] SkinGroup(
        uint group,
        Vector4 highlight,
        Vector4 midtone,
        Vector4 shadow) =>
    [
        new(group, "BaseTintHighlightA", highlight),
        new(group, "BaseTintMidtoneA", midtone),
        new(group, "BaseTintShadowA", shadow),
        .. SharedBValues.Select(value => new DynamicAppearanceShaderValue(
            group, value.Semantic, value.Value)),
    ];

    private static DynamicAppearanceShaderValue[] ColourGroup(
        uint group,
        Vector4 highlight,
        Vector4 midtone,
        Vector4 shadow) =>
    [
        new(group, "BaseTintHighlightA", highlight),
        new(group, "BaseTintMidtoneA", midtone),
        new(group, "BaseTintShadowA", shadow),
        .. SharedBValues.Select(value => new DynamicAppearanceShaderValue(
            group, value.Semantic, value.Value)),
    ];
}

/// <summary>One table-3 row consumed by the August DynamicAppearance loader.</summary>
public sealed record DynamicAppearanceShaderValue(
    uint ShaderParameterGroupId,
    string Semantic,
    Vector4 Value,
    uint Reserved = 0,
    bool BooleanDefault = false,
    string TextureDefault = "",
    uint ShaderValueTypeId = DynamicAppearanceReference.Float4Type)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteUInt32(ShaderParameterGroupId);
        writer.WriteString(Semantic);
        writer.WriteSingle(Value.X);
        writer.WriteSingle(Value.Y);
        writer.WriteSingle(Value.Z);
        writer.WriteSingle(Value.W);
        writer.WriteUInt32(Reserved);
        writer.WriteBool(BooleanDefault);
        writer.WriteString(TextureDefault);
        writer.WriteUInt32(ShaderValueTypeId);
    }
}
