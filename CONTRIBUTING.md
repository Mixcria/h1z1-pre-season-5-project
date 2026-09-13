# Contributing

Describe the behavior you want to change in an issue or pull request. Include a small example: what you did, what happened, and what you expected. Include the release version and Windows version for bug reports.

Build a separate local release with `Build-Local.ps1`, run the relevant server tests, and run `tests/LocalEdition.Smoke` when changing launcher startup, server identity, configuration or persistence. Changes to client installation should also exercise `tests/ClientDownload.Smoke`. Report native game playtesting separately from automated protocol checks.

Keep fixes focused. Preserve player accounts and saves across updates, retain attribution headers and explain any changes to packet formats or generated data. The independently released server and launcher use separate core snapshots; contract changes need checking on both sides.

Use a new branch for a contribution and open a pull request against `main`. The maintainer decides when to merge and create a player release. There is no automatic deployment from this repository.

Keep local data, passwords, account databases, registration codes, certificates, signing keys and Cloudflare upload credentials out of commits and bug reports. A public client download address is fine. Share only relevant log excerpts after checking them for account identifiers or private paths.

The initial source candidate has not yet selected a blanket project license. Resolve that before inviting contributions under an open-source license; preserve the existing third-party terms.
