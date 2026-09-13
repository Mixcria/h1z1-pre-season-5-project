# Local edition adaptations

The baseline is the live release observed on 2026-09-13 at 10:43:40 UTC. All 369 retained live runtime files matched the deployed runtime by hash. The server's build inventory verified 578 source documents; the independently published launcher inventory verified 67 files. `live-baseline.json` records the original hashes before adaptation.

The original local packaging changes include:

- `server/src/Cranberry.Host/Program.cs`: a managed local host accepts `stop` or EOF on its redirected input and follows its existing listener shutdown and score-flush path.
- `launcher/src/Cranberry.Launcher/Program.cs`: discovers a complete local package, uses a separate persistent player profile, starts the managed host and suppresses the production auto-updater in local mode.
- `launcher/src/Cranberry.Launcher/MainForm.cs`: looks up the local administrator registration code in this edition's private data folder.
- `launcher/src/Cranberry.Launcher/MainForm.LocalHost.cs`: shows local startup status and the first-account administrator option.

`launcher/src/Cranberry.Launcher.Core/LocalEdition.cs` is new. It creates a per-user local identity, selects loopback ports, installs the release's manifest and defaults, starts the bundled executable with explicit paths, isolates its environment from inherited production settings, and stops only the process it started. An exclusive data-folder lock prevents two local editions from sharing mutable state.

Additional editorial changes shorten references to archived research paths in packet comments and give the self-record schema generator a descriptive directory name, `server/tools/selfschema`. Function addresses, technical findings, original baseline checksums and third-party notices are retained. These edits do not change packet serialization or gameplay behavior.

Accounts, characters, rankings, wardrobe data and configuration are created or retained in the player's own data folder. They are never imported from the live server. The runtime appearance file is a static compatibility input; its hash matches the one used by the live release. The data-directory defaults match the live gameplay overrides, with production telemetry disabled.

Preview.2 fixes problems found in the first local playtest:

- Both `Cranberry.Launcher.Core/GameProcess.cs` copies create the client `Logs` directory and enable `LocalLogLevel=9` in the generated configuration. The original fallback used level 1, suppressing the run-state log required by the verified door helper and blocking match admission. Original `ClientConfig.ini` files are retained. The door verification and authenticated readiness gate remain active.
- `LocalAccountProfile`, `ZoneOptions`, `ZoneService.Economy.cs` and the host grant the full catalogue plus receipt-protected starter Crowns/crates to local accounts, including the owner previously excluded by the production policy. Existing progress and purchase receipts are retained. Production Client account exclusions remain in place outside the managed local edition.
- `PublicQueueOptions.ForLocalPlay()` permits one player in Solo, Duos and Fives with a five-second countdown after world readiness. The host applies this after loading configuration, so preview.1 data folders also receive the fix. Ordinary public-server population rules remain unchanged.

Tests cover the launch configuration, actual gateway menu inventory for both account levels, saved purchases across restart, and normal Play admission through launcher readiness, world loading and the drop sequence in all three modes. A native game playtest of preview.2 remains a separate verification step.

The older duplicate GUI and production launcher publishing tools in the server snapshot are excluded from the community Git export. The server solution retains its launcher-service tests. Historical design-document references remain in code; this first export includes the generated configuration reference, not the full private operations journal.

Remaining original-machine paths occur primarily in historical developer tooling, provenance headers, and fallback defaults used when starting the raw host without configuration. `Build-Local.ps1` and `LocalEdition` supply all paths needed by the packaged runtime, including the appearance input. A successful relocated source build does not make external-input generators portable.

The first GitHub build exposed additional machine-specific test inputs. Appearance tests now load the same bundled compatibility file through their test output directory. The external `AccountCrates.json` comparison is explicitly opt-in through `CRANBERRY_ACCOUNT_CRATES_REFERENCE`; its input is not added to the export. The vehicle timer test verifies listener dispatch and eventual recovery even when a busy runner delivers callbacks late; the existing deterministic tests continue checking every intermediate rotation and the exact handoff time. These changes affect test setup and assertions only, with no server or launcher runtime source changes.

The external client pack-index comparisons are also opt-in through `CRANBERRY_CLIENT_PACK_INDEX`. These six texture-name comparisons are separate from the always-enabled shader repair and model assertions, so missing external research data does not suppress those ordinary regressions.
