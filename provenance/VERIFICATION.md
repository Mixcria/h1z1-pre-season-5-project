# Preview verification, 2026-09-13

The Windows x64 release was built from the isolated community source with .NET SDK 10.0.400. No build or test modified the live deployment, its configuration, the owner's accounts or the original installed client. A final read-only check found the same live process active on `20260913-landing-fall-grace` with the same game manifest.

## Automated results

- Server suite: 6,960 tests passed across the initial run and its focused documentation recheck. One missing generated configuration reference was restored from the source snapshot; the three configuration-documentation tests then passed.
- Launcher-service suite: 213 passed.
- Harness suite: 151 passed, 34 skipped because historical capture/live-host fixtures were not enabled.
- Package lifecycle: 20 checks passed again after extracting the final build ZIP into a directory containing spaces. These cover a fresh identity, exclusive data-folder ownership, loopback startup, certificate pinning, administrator registration, authenticated game tunnel and launch ticket, the bundled manifest/download configuration, offline installation, shutdown, account persistence, settings preservation and port conflicts.
- Public Cloudflare content: all 851 unique objects used by the 853-file manifest returned their expected lengths.
- Real client installation: all 853 entries, 14,589,666,826 bytes, passed SHA-256 verification. The test seeded 852 files from the existing client using links in an isolated directory and forced a real Cloudflare download of another file. The complete client then passed another installation/verification with a handler that rejects every download request; zero requests occurred.
- Static server data: all 14 `Data/` files from the deployed runtime match this Windows package by hash. The additional appearance compatibility input matches the live server's separate input.
- Export review: no private key files, account databases or launcher-host credentials are included. Seven pattern matches were reviewed and found to be explicit test fixtures/canaries. This is a targeted source/package check, not a guarantee about every possible sensitive value.

Existing test-analyzer warnings remain (five xUnit2031 warnings and one xUnit2027 warning). Native game gameplay was not launched during this verification. The included GitHub Actions workflow has not yet run on GitHub; its build/test commands were exercised locally.

The result is a technically verified **community preview candidate**. A native gameplay check and the unresolved publication/license decisions remain before calling it a stable public release.
