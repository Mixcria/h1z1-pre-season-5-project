namespace Cranberry.Zone.Emotes;

public readonly record struct AugustEmote(
    uint SlotId, uint ItemDefinitionId, uint AnimationId, string Name);

/// <summary>
/// The twelve August F-key defaults, joined from EmoteAnimationSlots.DEFAULT_EMOTE_ITEM_ID,
/// ClientItemDefinitions.PARAM1 and EmoteAnimations.ID/INPUT_ACTION_KEY. Only this default
/// set is enabled; additional account emotes require their ownership and equipment path.
/// Source hashes and the native lookup are recorded in docs/emotes-20260906.md.
/// </summary>
public static class AugustEmotes
{
    public static IReadOnlyList<AugustEmote> DefaultSlots { get; } = Array.AsReadOnly<AugustEmote>([
        new(1, 3276, 2, "WaveHello"),
        new(2, 3287, 3, "WaveBye"),
        new(3, 3277, 7, "Applause"),
        new(4, 3278, 8, "Beckon"),
        new(5, 3279, 9, "CutThroat"),
        new(6, 3280, 18, "TeaBag"),
        new(7, 3281, 13, "Laugh"),
        new(8, 3282, 15, "NoWay"),
        new(9, 3283, 5, "Point"),
        new(10, 3284, 10, "Salute"),
        new(11, 3877, 4, "DoubleBird"),
        new(12, 3878, 14, "No"),
    ]);

    public static bool TryGetSlot(uint slotId, out AugustEmote emote)
    {
        if (slotId is >= 1 and <= 12)
        {
            emote = DefaultSlots[(int)slotId - 1];
            return true;
        }

        emote = default;
        return false;
    }

    public static bool TryGetAnimation(uint animationId, out AugustEmote emote)
    {
        foreach (AugustEmote entry in DefaultSlots)
        {
            if (entry.AnimationId == animationId)
            {
                emote = entry;
                return true;
            }
        }

        emote = default;
        return false;
    }
}
