# tools/capture — wire capture parser

This project's own reader for `C:\Aug2017\captures\wire-*.txt`, the files written by
`src/Cranberry.Host/FilePacketRecorder.cs`. Python 3, stdlib only, no dependencies.

    python tools/capture/capture.py <subcommand> <capture> [filters]

A capture argument may be a full path, a bare file name (resolved under `C:\Aug2017\captures`),
or just the stamp (`20260829-184346`).

## What it knows

| layer | how it is read |
| --- | --- |
| record | `time \| remote \| protocol \| direction \| length \| bytes`; `c2s-raw@N` carries the RC4 keystream position |
| `LoginUdp_14` | byte 0 is a login opcode (docs/03). Tunnel opcodes 0x10/0x11 are unwrapped (`u8 op; u64 serverId; u32 len; payload`) |
| `ExternalGatewayApi_3` | byte 0 is the gateway header: `opcode = b & 0x1F`, `channel = b >> 5` (`src/Cranberry.Zone/GatewayPackets.cs`) |
| zone opcode | joined against `C:\Aug2017\out\registrations-1148.json` — the client's own packet-id table |

**Channels 2 and 3 are never decoded as opcodes.** `ZoneService.HandleClientTunnel` diverts them
before base-opcode dispatch (FUN_140dd1400): they are opcode-free movement streams. They are
labelled `ch2 PlayerMovement` / `ch3 ManagedMovement` and their bytes are left opaque. Decoding
them as base opcodes manufactures tens of thousands of phantom packets.

`c2s-raw` rows are ciphertext and are likewise not layered: byte 0 there is keystream, not a
header. They are excluded from every subcommand except `list --raw` and `summary`.

Sub-opcode *width* is not in the registration table, so `resolve()` tries the u16 little-endian
sub first and the u8 sub second and prints which matched (`u8`, `u16`, or `u8|u16` when the two
cannot be told apart because the third byte is zero). A byte with no registration is reported as
`unregistered 0xNN`, never guessed.

## Subcommands

| command | use |
| --- | --- |
| `list <cap>` | one line per record, with wall clock, inter-packet gap, resolved name and a hex head |
| `seq <cap>` | the same, with runs of one message folded (`x24 over 0.01s`) — the behavioural sequence |
| `diff <a> <b>` | unified diff of the two collapsed sequences |
| `family <cap> <op>` | every packet of one family: `0f`, `0f:45`, or a registered name |
| `summary <cap>` | per-protocol/direction/channel counts and the first occurrence of every message |
| `latency <cap>` | for each server message, the next client message and min/median/max delay |

`latency` pairs each server message with whatever the client sent next, so the client's
free-running timers (§9 of docs/71 — KeepAlive at 1 Hz, Synchronization at 5 s, GameTimeSync at
11 s) dominate its output and drown genuine request/response pairs. Use it to spot candidates,
then confirm the pair with `list --from/--to`.

Filters accepted by `list`/`seq`/`diff`/`family`: `--from HH:MM:SS`, `--to HH:MM:SS`,
`--direction`, `--protocol`, `--channel`, `--no-movement`, `--grep`. `diff` also takes
`--left-from/--left-to/--right-from/--right-to`, which is how you compare one attempt in a
multi-attempt capture against another.

## Examples

    # the known-good session's whole flow, movement folded away
    python tools/capture/capture.py seq 20260829-184346 --no-movement

    # the Z2 hang, good vs bad, one attempt each
    python tools/capture/capture.py diff 20260829-184346 20260829-151627 --no-movement \
        --left-from 18:44:29.7 --left-to 18:44:34 --right-from 17:18:47.2 --right-to 17:19:16

    # every loot interaction
    python tools/capture/capture.py family 20260830-090725 09:15

See `docs/71-client-behaviour-spec.md` for what this tool was used to derive.

## Also here

`burst-diff.py` prints one line per gateway message — line number, wall clock, direction,
channel, resolved opcode, length and a hex head — so two runs' zone bursts can be diffed against
each other:

    python tools/capture/burst-diff.py <capture> [s2c|c2s|both] [firstLine] [lastLine]

It was written for the G10 lane (docs/108, D211), which used it to cut the lobby `ClientIsReady`
burst down to the shape the August client accepts.
