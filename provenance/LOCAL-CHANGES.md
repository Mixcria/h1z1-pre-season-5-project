# Local edition adaptations

The baseline is the live release observed on 2026-09-13 at 10:43:40 UTC. All 369 retained live runtime files matched the deployed runtime by hash. The server's build inventory verified 578 source documents; the independently published launcher inventory verified 67 files. `live-baseline.json` records the original hashes before adaptation.

Runtime behavior changes for local packaging are limited to these existing source files:

- `server/src/Cranberry.Host/Program.cs`: a managed local host accepts `stop` or EOF on its redirected input and follows its existing listener shutdown and score-flush path.
- `launcher/src/Cranberry.Launcher/Program.cs`: discovers a complete local package, uses a separate persistent player profile, starts the managed host and suppresses the production auto-updater in local mode.
- `launcher/src/Cranberry.Launcher/MainForm.cs`: looks up the local administrator registration code in this edition's private data folder.
- `launcher/src/Cranberry.Launcher/MainForm.LocalHost.cs`: shows local startup status and the first-account administrator option.

`launcher/src/Cranberry.Launcher.Core/LocalEdition.cs` is new. It creates a per-user local identity, selects loopback ports, installs the release's manifest and defaults, starts the bundled executable with explicit paths, isolates its environment from inherited production settings, and stops only the process it started. An exclusive data-folder lock prevents two local editions from sharing mutable state.

Additional editorial changes shorten references to archived research paths in packet comments and give the self-record schema generator a descriptive directory name, `server/tools/selfschema`. Function addresses, technical findings, original baseline checksums and third-party notices are retained. These edits do not change packet serialization or gameplay behavior.

Accounts, characters, rankings, wardrobe data and configuration are created or retained in the player's own data folder. They are never imported from the live server. The runtime appearance file is a static compatibility input; its hash matches the one used by the live release. The data-directory defaults match the live gameplay overrides, with production telemetry disabled.

No gameplay packet or match logic was changed. The public queue still expects multiple players; the local administrator can use the existing `/startmatch` command for solo development. Native playtesting of this local distribution remains a separate verification step.

The older duplicate GUI and production launcher publishing tools in the server snapshot are excluded from the community Git export. The server solution retains its launcher-service tests. Historical design-document references remain in code; this first export includes the generated configuration reference, not the full private operations journal.

Remaining original-machine paths occur primarily in historical developer tooling, provenance headers, and fallback defaults used when starting the raw host without configuration. `Build-Local.ps1` and `LocalEdition` supply all paths needed by the packaged runtime, including the appearance input. A successful relocated source build does not make external-input generators portable.
