namespace Cranberry.Zone.Appearance;

/// <summary>
/// Drops a <c>SetCharacterEquipment</c> that is byte-identical to the last one this session was
/// sent.
/// <para>
/// <b>D53 / docs/80 edit 5.</b> The owner measured what a full dress costs this client on his own
/// server: <c>AttachmentProcessor.log</c> pairs each <c>RemoveAttachmentInternal</c> with the next
/// <c>ProcessNewAttachment</c> for one slot at <b>76, 90, 91, 95, 169, 229 and 534 ms</b> - a
/// whole attachment-group teardown and rebuild, and the visible bare-body frame he described as
/// "goes naked for a split second". In one 21:16 session <b>11 of 25</b> full dresses were
/// byte-for-byte repeats of the packet before them and still cost <b>118</b>
/// <c>RemoveAttachmentInternal</c> across six slots, with 16 frames showing <c>Weapon_Empty.adr</c>
/// in the hand.
/// </para>
/// <para>
/// The now-derived per-slot writer (<c>94/02</c>, docs/95) lets established actors update changed
/// equipment without a whole-body rebuild. This suppressor remembers the resulting complete
/// state whether delivered as a full dress or safe slot deltas, so identical repeats stay quiet.
/// </para>
/// <para>
/// Cleared on <c>ClientBeginZoning</c>, because the client rebuilds the actor across a zone
/// transition and the post-zoning dress must always go out.
/// </para>
/// </summary>
public sealed class AugustDressSuppressor
{
    private byte[]? _last;

    public bool HasBaseline => _last is not null;

    /// <summary>A skin manager changed an existing actor; reassert its occupied slots.</summary>
    public bool RequiresSlotReassert { get; private set; }

    /// <summary>Dresses dropped because they changed nothing.</summary>
    public long Suppressed { get; private set; }

    /// <summary>Changed equipment states delivered as complete dresses or slot deltas.</summary>
    public long Sent { get; private set; }

    /// <summary>
    /// Decide whether <paramref name="packet"/> should go out, and remember it when it does.
    /// </summary>
    /// <param name="packet">The complete inner zone packet, exactly as it will be serialised.</param>
    /// <param name="enabled">
    /// <c>false</c> (<c>CRANBERRY_DRESS_SUPPRESS=0</c>) always sends, and still records the packet
    /// so that flipping the switch mid-session cannot compare against a stale baseline.
    /// </param>
    public bool ShouldSend(ReadOnlySpan<byte> packet, bool enabled)
    {
        if (enabled && _last is byte[] last && last.AsSpan().SequenceEqual(packet))
        {
            Suppressed++;
            return false;
        }

        _last = packet.ToArray();
        RequiresSlotReassert = false;
        Sent++;
        return true;
    }

    /// <summary>
    /// Forget the last packet, so the next dress is always sent. Counters are kept: they are the
    /// numbers the owner reads out of one session log.
    /// </summary>
    public void Forget()
    {
        _last = null;
        RequiresSlotReassert = false;
    }

    /// <summary>
    /// A manager can override unchanged clothes. Retain the fact that the actor already exists,
    /// while requiring its safe occupied slots to be sent again. This is not a zoning reset.
    /// </summary>
    public void InvalidateAfterSkinManager()
    {
        RequiresSlotReassert |= _last is not null;
        _last = null;
    }

    /// <summary>The tail of the equipment log line.</summary>
    public string Counters() => $"{Sent} sent / {Suppressed} suppressed";
}
