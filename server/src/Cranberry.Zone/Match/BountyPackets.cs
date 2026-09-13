using Cranberry.Protocol;

namespace Cranberry.Zone.Match;

// The Bounty family of MatchHistory (base 0x67) - "Bounty: Backing your Match in Fort Destiny",
// the client's own words (en_us 4162258810). The mechanic is a self-wager placed in the PRE-GAME
// LOBBY: you back the upcoming match with Crowns, Skulls or earned Free Bounty Credits, and a
// high placing pays out in Skulls.
//
// Vocabulary rule (AUDIT-bounty §1.1, D252): the August client has ZERO occurrences of "bet",
// "wager" or "prediction" on any surface - the exe string table, BountyWindow.gfx, the 9,330-entry
// en_us locale, three datasheets and all 1,743 packet registrations. Its word is Bounty and its
// verb is BACK YOUR MATCH, so every identifier in this file is Bounty / ante / BountyType.

/// <summary>
/// <c>MatchHistory.MatchBounty</c> (<c>67 0d</c>, s2c) - the one row the Bounty screen's
/// <c>MatchBounty</c> data source holds: how much is staked on this match and in what.
///
/// <para>
/// <b>Layout, from the client's own parser <c>FUN_1413e9db0</c></b>
/// (<c>out\ghidra-aug\menu-matchhistory-A\…\FUN_1413e9db0_1413e9db0.c</c>): the family stream
/// re-reads the base and the sub itself before the body, exactly as every other <c>0x67</c> sub
/// does (docs/02, 2026-08-28, "MatchHistory (0x67) sub-opcode is a u8 in both directions").
/// </para>
/// <code>
///   u8   base           0x67    -> +0x08
///   u8   sub            0x0d    -> +0x10   (read as a BYTE, not a u16)
///   u32  BountyAmount           -> +0x18
///   u32  BountyType             -> +0x1c
/// </code>
/// <para>
/// The handler is <c>FUN_141655cf0</c>: it pushes exactly three keys into the UI table at
/// <c>mgr+0x300</c> - <c>Id = 1</c>, <c>BountyAmount</c> = the <c>+0x18</c> word and
/// <c>BountyType</c> = the <c>+0x1c</c> word - and reads nothing else. Ten bytes.
/// </para>
/// </summary>
/// <param name="BountyAmount">What is staked. 0 until an ante is confirmed.</param>
/// <param name="BountyType">
/// Which ante was taken. The value the client itself sends back in <see cref="SelectBountyRequest"/>
/// is echoed here verbatim, so the screen's "BOUNTY BACKED" state agrees with the button that was
/// pressed: 1 Crowns, 2 Skulls, 3 earned Credits; 0 is not backed (UIBountyManager.as).
/// </param>
public sealed record MatchBountyState(uint BountyAmount, uint BountyType)
{
    /// <summary><c>cPacketIdMatchHistoryBase</c>.</summary>
    public const byte Opcode = ZoneOpcodes.MatchHistoryBase;

    /// <summary>The <b>u8</b> sub - <c>FUN_1413f1210</c> case 0x0d.</summary>
    public const byte SubOpcode = 0x0d;

    /// <summary><c>1 + 1 + 4 + 4</c>.</summary>
    public const int Length = 10;

    /// <summary>The state a lobby opens in: nothing staked, no ante taken.</summary>
    public static MatchBountyState None { get; } = new(0u, 0u);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(BountyAmount);
        writer.WriteUInt32(BountyType);
    }
}

/// <summary>
/// One of the two payout blocks inside <see cref="MatchBountyTables"/>, in the shape the client's
/// own sub-parser <c>FUN_140007d9c</c> reads:
/// <code>
///   u32   Word0          -> block+0x28
///   u32   Word1          -> block+0x2c
///   i32   n                             (FUN_140a4caf0, a counted u32 vector)
///   u32   Rewards[n]
/// </code>
/// <para>
/// <b>The two leading words are parsed and never read.</b> <c>FUN_1416576d0</c>, the only handler
/// for this packet, touches the envelope's <c>+0x18</c> / <c>+0x1c</c> and each block's vector -
/// and no block-level word (grep of the decompile for <c>param_2 + 0x48/0x4c/0xa8/0xac</c>: zero
/// hits). They are therefore written as 0 and this is <b>[P]</b>, not a guess.
/// </para>
/// <para>
/// <b>What the client draws from the vector.</b> Element <c>i</c> becomes one
/// <c>BountyPayoutList</c> row keyed <c>{ Rank = i + 1, Reward = element, PlayerCount,
/// CurrencyImageSetId }</c> - the PLACE / BONUS table the window renders. The currency icon is NOT
/// on the wire: the client walks its own currency list and picks id <b>5 (Skulls)</b> for the first
/// block and id <b>6 (Credits)</b> for the second (<c>FUN_1416576d0</c> lines 158-179, the two
/// <c>*(int *)(lVar5 + 0x30) == 5 / == 6</c> searches). That is what fixes which block is which.
/// </para>
/// <para>
/// A row whose reward is <c>&lt;= 0</c> is skipped by the handler (<c>if (0 &lt; iVar2)</c>), so a
/// short table simply draws fewer places rather than drawing zeroes.
/// </para>
/// </summary>
/// <param name="Rewards">The payout for place 1, place 2, … in order.</param>
/// <param name="Word0">Block <c>+0x28</c>. Parsed, unread; 0.</param>
/// <param name="Word1">Block <c>+0x2c</c>. Parsed, unread; 0.</param>
public sealed record BountyPayoutTable(IReadOnlyList<uint> Rewards, uint Word0 = 0u, uint Word1 = 0u)
{
    /// <summary>An empty table - the client draws no rows for it.</summary>
    public static BountyPayoutTable Empty { get; } = new([]);

    /// <summary><c>4 + 4 + 4 + 4·n</c>.</summary>
    public int Length => 12 + (4 * Rewards.Count);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(Word0);
        writer.WriteUInt32(Word1);
        writer.WriteInt32(Rewards.Count);
        foreach (uint reward in Rewards)
        {
            writer.WriteUInt32(reward);
        }
    }
}

/// <summary>
/// <c>MatchHistory.MatchBountyTables</c> (<c>67 0e</c>, s2c) - the Bounty screen's header and its
/// two PLACE-&gt;BONUS payout tables.
///
/// <para>
/// <b>Layout, from the client's own parser <c>FUN_1413eafe0</c></b>
/// (<c>out\ghidra-aug\menu-matchhistory-A\…\FUN_1413eafe0_1413eafe0.c</c>):
/// </para>
/// <code>
///   u8   base            0x67   -> +0x08
///   u8   sub             0x0e   -> +0x10
///   u32  PlayerCount            -> +0x18
///   u32  BountyCount            -> +0x1c
///   {u32; u32; i32 n; u32[n]}   -> +0x20   FUN_140007d9c, the SKULLS table  (icon currency id 5)
///   {u32; u32; i32 n; u32[n]}   -> +0x80   FUN_140007d9c, the CREDITS table (icon currency id 6)
/// </code>
/// <para>
/// The handler <c>FUN_1416576d0</c> publishes <c>{ Id = 1, PlayerCount, BountyCount }</c> into the
/// header data source at <c>mgr+0x300</c> and the two row lists into <c>mgr+0x2f0</c> (the Credits
/// block) and <c>mgr+0x2f8</c> (the Skulls block). <c>PlayerCount</c> is repeated on every row,
/// which is the window's <c>PlayerCountLabel</c> / <c>PlayerPoolLabel</c> header.
/// </para>
/// </summary>
/// <param name="PlayerCount">Players in this match - the window's "Players" header.</param>
/// <param name="BountyCount">How many of them have backed it.</param>
/// <param name="Skulls">Block at <c>+0x20</c>; the client draws it with the Skulls icon.</param>
/// <param name="Credits">Block at <c>+0x80</c>; the client draws it with the Credits icon.</param>
public sealed record MatchBountyTables(
    uint PlayerCount,
    uint BountyCount,
    BountyPayoutTable Skulls,
    BountyPayoutTable Credits)
{
    /// <summary><c>cPacketIdMatchHistoryBase</c>.</summary>
    public const byte Opcode = ZoneOpcodes.MatchHistoryBase;

    /// <summary>The <b>u8</b> sub - <c>FUN_1413f1210</c> case 0x0e.</summary>
    public const byte SubOpcode = 0x0e;

    /// <summary><c>1 + 1 + 4 + 4</c> plus the two blocks.</summary>
    public int Length => 10 + Skulls.Length + Credits.Length;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Skulls);
        ArgumentNullException.ThrowIfNull(Credits);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(PlayerCount);
        writer.WriteUInt32(BountyCount);
        Skulls.WriteTo(writer);
        Credits.WriteTo(writer);
    }
}

/// <summary>
/// <c>MatchHistory.SelectBounty</c> (<c>67 0c</c>, <b>c2s</b>) - the Confirm click on the Bounty
/// screen.
///
/// <para>
/// <b>This lane's one Ghidra needle, and it settles AUDIT-bounty U-1 against both of its
/// candidates.</b> The audit guessed <c>67 13</c> or <c>67 1d</c>; the sub is <b>0x0c</b>, and the
/// chain is:
/// </para>
/// <code>
///   "SelectBounty" @ 0x1431f4340
///     registered by FUN_1404b07a0 with binding thunk 0x140035e04
///     0x140035e04:  jmp 0x1412267a0                       (the script stub)
///     0x1412267a0:  cmp [rcx+0x28],1 ; argc == 1
///                   and [rdx+0x18],0x8f ; cmp al,3        ; arg 0 must be a number
///                   mov edx,[rdx+0x20]                    ; the u32 argument
///                   mov rcx,[0x143f6a100]                 ; the MatchHistory manager
///                   jmp 0x14004ab33 -> FUN_1413f0940
///     FUN_1413f0940: {+0x08 u16 0x0067; +0x10 u32 0x000c; +0x18 u32 arg} -> FUN_1413e8b60
///     FUN_1413e9380: writes ONE byte from +0x10 and FOUR bytes from +0x18
/// </code>
/// <para>
/// So the wire is <c>67 0c</c> then one little-endian <c>u32</c>: <b>six bytes</b>. It carries a
/// single number, which is why the three ante buttons (<c>HardButton</c> / <c>SoftButton</c> /
/// <c>FreeButton</c> in <c>BountyWindow.gfx</c>) can only be selecting a <b>type</b> - the amount
/// is the server's to decide. That reading is <b>[I]</b>; the six bytes are <b>[P]</b>.
/// </para>
/// <para>
/// <c>67 13</c> (<c>FUN_1413f3460</c>, no fields) and <c>67 1d</c> (<c>FUN_1413f1cf0</c>,
/// <c>u32 + u64</c>) remain derived-but-unobserved requests belonging to something else; the zone
/// dispatcher still logs either of them if one ever arrives.
/// </para>
/// </summary>
/// <param name="BountyType">The single <c>u32</c> the script binding passed.</param>
public sealed record SelectBountyRequest(uint BountyType)
{
    /// <summary><c>cPacketIdMatchHistoryBase</c>.</summary>
    public const byte Opcode = ZoneOpcodes.MatchHistoryBase;

    /// <summary>The <b>u8</b> sub written by <c>FUN_1413f0940</c>.</summary>
    public const byte SubOpcode = 0x0c;

    /// <summary><c>1 + 1 + 4</c>.</summary>
    public const int Length = 6;

    /// <summary>Reads the six bytes, or returns false and leaves the caller to log them.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SelectBountyRequest? request)
    {
        request = null;
        if (payload.Length < Length || payload[0] != Opcode || payload[1] != SubOpcode)
        {
            return false;
        }

        // The client's own family stream does not require an exact end (docs/02, 2026-08-28: "the
        // family does NOT check for an exact end"), so neither does this - a trailing byte from a
        // build we have not seen must not cost the owner his click.
        request = new SelectBountyRequest(BitConverter.ToUInt32(payload[2..6]));
        return true;
    }

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(BountyType);
    }
}

/// <summary>
/// GameMode ce 15: IsInBox, not "in match". FUN_140bba020 writes this bool to the
/// MatchHistory manager +0x348 and emits KOTK_ENTER_BOX_OF_DESTINY when true.
/// Send true before entering pregame UI, false before drop; ce 16 also clears it.
/// It has broader pregame/input effects, so bounty eligibility must not redefine this state.
/// </summary>
public sealed record BountyLobbyState(bool IsInBox)
{
    public const byte Opcode = 0xce;
    public const ushort SubOpcode = 0x15;
    public const int Length = 4;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteBool(IsInBox);
    }
}

/// <summary>
/// MatchHistory 67 10 supplies the COSTS that activate the backing buttons.
/// FUN_1413ed3d0 reads u64, counted {u32 id; string currencyCode; u32 amount} rows;
/// FUN_1416561a0 ignores header/id and maps SOE or KH$ to HardCurrencyBounty,
/// KS$ to SoftCurrencyBounty, KF$ to FreeCurrencyBounty. It obtains free progress
/// from the client's currency manager (hundredths), not another field in this packet.
/// Both hard codes receive the same price so platform selection cannot change the charge.
/// Zero all costs to revoke the server's offer, including hosted/custom Solo.
/// </summary>
public sealed record MatchBountyCosts(uint Crowns, uint Skulls, uint Credits, ulong HeaderId = 0)
{
    public const byte Opcode = ZoneOpcodes.MatchHistoryBase;
    public const byte SubOpcode = 0x10;
    public const int Length = 74;
    public static MatchBountyCosts Unavailable { get; } = new(0, 0, 0);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Crowns > int.MaxValue || Skulls > int.MaxValue || Credits > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Crowns), "Bounty cost exceeds the client currency limit.");
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(HeaderId);
        writer.WriteUInt32(4);
        WriteCost(writer, 1, "SOE", Crowns);
        WriteCost(writer, 2, "KH$", Crowns);
        WriteCost(writer, 3, "KS$", Skulls);
        WriteCost(writer, 4, "KF$", Credits);
    }

    private static void WriteCost(PacketWriter writer, uint id, string currencyCode, uint amount)
    {
        writer.WriteUInt32(id);
        writer.WriteString(currencyCode);
        writer.WriteUInt32(amount);
    }
}
