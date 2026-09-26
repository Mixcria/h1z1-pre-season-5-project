# Native bleeding and healing presentation

Completed medical use now sends the August client's native effect tag and timed resource modifier. The existing client HUD uses these to select the bandage/first-aid icon and draw the remaining projected recovery beside current health. Bleeding severity changes publish the native named status tag, and completion of treatment removes it. Existing authoritative health changes, item amounts, durations, casting rules and remote particle replication are retained.

This medical implementation is part of the current working restoration baseline synchronized onto community commit `81e00a2`. It retains the tested workspace's existing healing policy, including the additional bandage completion boost. That balance policy is not established as original August behavior. Client files and deployment configuration remain unchanged.

## Reproducible client evidence

Target: August 5, 2017, Steam app 433850 / depot 433851 / manifest 6373368576374184611. Analyzed `H1Z1.exe`: 72,818,304 bytes; SHA256 `d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd`; image base `0x140000000`. The completed Binary Ninja database's triage identity matches this executable; relevant paths were also traced using Ghidra. All addresses below are virtual addresses in this binary.

The preserved stock `HudPlayerResourcesWindow.gfx` has SHA256 `c9dabbb639cbe7124ad9803864fe9b7af92467786678937d5993b6136f16bacb`. Its `ResourceManager` binds `Effects.EffectTagDataSource` and `Effects.EffectModifyResourceDataSource`. `EffectView` selects healing/bleeding icons from effect IDs and localized names. `PlayerResourceView` reads `ResourceRegenValuePercentAddend` and calls `ResourceBar.setRegen`, which caps projected width at the remaining health-bar space. Resource projection describes presentation; authoritative health still comes from the server's normal resource updates.

Main dispatch `0x140af3950` routes the `9e` family to `0x140cef800`. Add-tag handler `0x140cefb90`, tag decoder `0x140a38b00`, tag constructor `0x1421e7810`, datasource getter `0x1414d1a60` and publisher `0x1414ce4c0` establish the tag path. Add-modifier handler `0x140cef9a0` and decoder `0x140a38070` establish the resource layout. Consumer `0x1414d2820` calculates positive remaining milliseconds times the regeneration rate, with rounding. Its synchronized clock is the server-time basis supplied through existing `8c` synchronization. Remove handler `0x140cf0390` clears both representations by instance ID. Native insertions ignore duplicate instance IDs, so explicit resynchronization removes/re-adds each instance while retaining its original start time.

## Language-independent wire contract

All scalars are little-endian. Lengths exclude the existing one-byte gateway wrapper.

| Packet | Fields | Total bytes |
|---|---|---:|
| `9e 06` | subject u64; nested tag below | 115 |
| `9e 07` | subject u64; instance u64; resource u32; regen f32 units/ms; burn f32 units/ms; start u64 synchronized server ms; duration i32 ms | 42 |
| `9e 08` | subject u64; instance u64 | 18 |

The nested tag is 105 bytes. Relative to its start: instance u64 at 0; unknown u32 at 8; effect ID u32 at 12; ability ID u32 at 16; ability stage i32 at 20; name/description/icon u32 at 24/28/32; source GUID u64 at 36; unknown u32 at 44; unknown u64 at 48/56/64; four zero 32-bit words at 72/76/80/84; four zero u32 at 88/92/96/100; zero u8 at 104. All unused fields retain observed constructor defaults; ability stage is **-1**, not zero. Default vector zeroing is corroborated by initializer `0x1401d93d0`. No meaning is invented for unused fields.

| Status | Effect ID | Localized name ID |
|---|---:|---:|
| Field Bandage healing | 120583 | 8886 |
| First Aid healing | 120581 | 8887 |
| Light bleeding | 120107 | 9034 |
| Moderate bleeding | 120111 | 1110 |
| Heavy bleeding | 120112 | 14116 |
| Severe bleeding | 120113 | 9035 |
| Critical bleeding | 120114 | 14117 |

English locale source hashes: `en_us_data.dat` SHA256 `5c1379c20d3ea81862b2c67802ea209e78e93505f984d561cee0ee0ecc3b600e`; `.dir` SHA256 `17f0645577e08d6c96936780c009fb7e14fb7ecc5de40bf2c984262847a1c5c9`. Mapping the existing five server severities to these five tags in ascending order is adapter policy, not proof of original wound thresholds. Gauze/procoagulant receive native projected recovery without an invented icon assignment.

## Implementation and lifecycle

`Combat/MedicalEffectPackets.cs` contains standalone codecs. `ZoneService.HealingHud.cs` allocates independent positive instance IDs and publishes normalized resource-1 recovery at the same integer rate as authoritative healing. Tags and modifiers share each recovery's instance, so one removal clears both. Finishing one stacked recovery leaves others active. Existing reset/death/world/inventory invalidation removes stale projections; the health tick uses a wide intermediate before clamping to configured maximum health.

`ZoneService.Bleeding.cs` adds owner-only native tags before the existing particle-band early return, allowing a named severity change even when the particle loop stays unchanged. It retains the destination baseline's peer particle path. `WorldDisplayLabel.Values` stops supplying the former `Cranberry.Healing` key; all original gameplay entries remain. The native implementation is independent of the prior HUD extension, whose observed missing-key fallback also allows native icons.

Original August server healing amounts, probabilities, bleed rates, armour rules, interruption thresholds and stacking policy remain unverified. Older source comments referring to other-version research are not promoted to August evidence. This port changes presentation and cleanup, with no retuning of those policies.

## Validation and acceptance scope

The newer local restoration workspace passed 385 focused protocol/gameplay/bootstrap checks and a local native-client session. The owner explicitly accepted healing presentation and bleeding's indicator, continuing health drain and clearing after treatment. That installed HUD was already modified before this project task; its decompilation still uses the native projected-health path and the missing-key native icon fallback. These observations are local implementation evidence, not original-server evidence or unmodified-stock visual parity.

Validation of the exported baseline is recorded in [the restoration record](../../docs/RESTORATION.md). Owner acceptance of the working research session does not claim a fresh native playtest of the rebuilt community package. Automated coverage includes packet vectors, cast application/cancellation, distinct recovery instances, maximum health, cleanup, concurrent bleeding/recovery, owner-only HUD traffic, peer particles and bootstrap key removal.

For native verification of the packaged candidate, use isolated local hosting and a local test account: enter a match and land, turn god mode off, `/hurt 5000`, `/give bandage 5`, then Q while stationary; repeat with `/give medkit 2` and E. Observe the icon and projected segment after the cast. For bleeding, allow a local bot hit, `/bots freeze`, verify continuing drain and indicator, then treat. Check death/return/rejoin separately. No live service is involved, and no client files need changing.

No publishing approval is implied by gameplay acceptance. This work belongs in a reviewed branch/PR; there is no automatic deployment in the community repository workflow.
