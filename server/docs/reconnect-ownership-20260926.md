# Reconnect session ownership — 26 September 2026

An authenticated reconnect could replace a character's peer entry while the old
gateway connection remained alive. When that old connection eventually closed,
its GUID-based cleanup removed the replacement, sent observers `0f/01
RemovePlayer`, and removed the replacement from grenade target lookup.

The new regression fixture reproduced all three failures against public source
baseline `454f73d051cd1c077d468cf5ca06e7206e6db442`. Closing the current session
was the passing control. These are observed restoration defects, not claims
about the original server's reconnect policy.

## Implementation

After validating the exact client protocol/version and a server-issued gateway
ticket, admission closes and cleans up previous admitted connections for that
character before loading and registering the replacement. Closing the transport
also stops its existing deferred-work callbacks from running. Departure is
idempotent: the listener's later close notification cannot repeat party, vehicle,
peer, throwable, wardrobe or score cleanup. Messages delivered to a retired
session are ignored.

The current wardrobe object is carried into the immediate replacement because
the store coalesces writes in the background; reloading disk immediately could
lose a selection that has not reached disk yet. Ordinary final departure still
saves and evicts the active wardrobe entry.

This completes the server's existing replace-by-character ownership rule. It
does not resume the previous match: normal departure and fresh admission still
apply. A valid replacement ticket retires the old connection before account
initialization; if the new initialization subsequently fails, the old connection
stays closed. Invalid tickets, protocol names and build versions cannot retire it.

Runtime changes are confined to `ZoneService.cs` and `ZoneService.Peers.cs`.
There are no new packet layouts, balance changes or client modifications.

## Evidence and limits

Target: August 5, 2017, app 433850, depot 433851, manifest
6373368576374184611, ClientProtocol_1148, build 0.0.118.208059.
Executable SHA256:
`d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`.
Native addresses use image base `0x140000000`.

Fresh read-only exports from the saved Ghidra project match this executable:

| Direction/message | Native path | Relevant behavior |
|---|---|---|
| Server → client `0f/01 RemovePlayer` | Reader `0x140a5fd50`; Character dispatcher `0x140af9ca0`, case1 | Little-endian `u8 0f, u8 01, u64 characterGuid, u16 effectFlag` (12 bytes). Looks up the entity by GUID and invokes its removal path even when the flag is zero. No connection identity distinguishes an old session from its replacement. |
| Server → client `d5 AddLightweightPc` | Reader `0x140a2d880`; handler `0x140af2ba0` | Carries the character GUID and client transient ID; handler first looks up that GUID. An existing active entity can cause an early return, so blindly registering another server-side owner is not a native session replacement protocol. |

Character dispatcher export SHA256:
`6f546bbb45b8037d651d5f1d07d42f123b03d124433635d43885b11c2ff83884`.
All five bounded exports and their executable provenance index are in the
`binary-access/reconnect-native` evidence directory below. This establishes
GUID-based client consequences, not the original server's admission policy.

Binary Ninja's Python API is not installed. After the owner enabled and started
its built-in GUI MCP server, direct inspection became available at
`http://127.0.0.1:24642/mcp` (loopback only). The active database is
`C:/Aug2017/H1Z1-August-2017.bndb`, with an x86_64 PE view at `0x140000000` and
entry point `0x1400bd101`. Analysis reports idle. Fresh Binary Ninja Pseudo C
queries returned the RemovePlayer reader and AddLightweightPc handler, and its
string table contains `ClientProtocol_1148` at `0x1435fcda0`. This is actual
database inspection, separate from the earlier read-only SQLite metadata probe.
No save or analysis-update command was issued. The database container SHA256 is
`da60653fd84b40ffdf69abb4d47101bf99969aa9f1c48a6a8bf66a70794265c8`.
The current executable hash was freshly checked. Ghidra exports and Binary Ninja
queries provide independent decompiler views of the same target functions.
Binary Ninja memory reads matched the local executable's 1,536-byte PE header
and 256-byte samples at each of the two functions. This is a bounded identity
comparison, not a new full hash of the database's embedded executable. Database
size and modification time remained unchanged. The evidence README includes a
reusable read-only MCP probe; discover current view handles each session.

Private reproducible access evidence is under
`C:\Aug2017\out\compatibility-20260926\binary-access`.
The initial failing regression TRX is under
`C:\Aug2017\out\restoration-20260926\compatibility-session-audit\results`.
Original same-build server captures remain unavailable. Our generated packets
demonstrate our implementation, not original server behavior.

## Validation

`GatewayReplacementSessionTests` passed 14/14 focused cases. It uses real gateway
ticket issuance, login parsing and service admission/disconnect with in-memory
transport. The old link,
replacement and observer have distinct SOE sessions and loopback endpoint
identities. It opens no sockets, contacts no hosted service and stops before
native world bootstrap. World/interest state is seeded explicitly. The wardrobe
case uses a unique temporary store and holds its writer semaphore to make the
pending-save condition deterministic. Other cases use in-memory stores.

Coverage includes invalid ticket/GUID/protocol/version, old transport retirement,
peer/observer/throwable/vehicle/party ownership, a late party-leave message, three
successive admissions with repeated close callbacks, current-owner departure,
and wardrobe preservation/cache eviction. The original failure run had three
failures and one passing control. Independent review found no additional blocker.

The full Release solution built and passed **7,678 tests**, with **36 optional
external-fixture/running-host tests skipped** and zero failures. Existing analyzer
warnings remain. The new 14 cases are included in that total, not added again.
Native reconnect presentation is not yet confirmed; no native cycles were run
for this increment. No production services or player accounts were changed.

Reproduce from the repository root:

```powershell
dotnet test server/Cranberry.slnx -c Release
```

TRX results are under `C:\Aug2017\out\compatibility-20260926\validation`.
No `CRANBERRY_*` host/test overrides were set; opt-in running-host scenarios
remained skipped. The new gateway fixture uses only synthetic loopback identities.

For a later two-client local check, use an isolated host and two test accounts.
Have one player observe the other reconnect as the same character, then join the
same lobby again. Check that there is one actor, movement/equipment updates keep
arriving, and leaving the replacement removes that actor once. Also check the
reconnecting player's outfit. Only run this with verified local endpoints;
the current community package is loopback-only. This checklist does not claim a
completed native run or require enabling LAN access.

The owner subsequently authorized source publication with the completed
[compatibility milestone](compatibility-publication-20260926.md). A player
release remains separate.
It is on local branch `feature/august-compatibility-audit-20260926`, based on
public `main` at `454f73d`. The inspected GitHub workflow builds, tests and uploads
a preview artifact on main/PR events; it does not deploy a live game server or
publish an automatic-update release. Any later approved source push and player
release must still be reviewed separately.
