# Releasing a community update

1. Merge the intended source changes and update `VERSION`. Keep the player-facing release notes focused on changed behavior and known limitations.
2. If game files change, construct the matching `package/game-manifest.json`. Upload its SHA-256-named objects to the configured Cloudflare R2 content prefix and verify every referenced object's size and hash before distributing the manifest. Keep R2 credentials only in your private publishing environment. A server-only change can reuse the existing game manifest.
3. Run the server tests and build a new output directory with `Build-Local.ps1 -Zip`. The script bundles fresh Windows runtimes, the matching launcher, the game manifest and static compatibility input. It never copies the maintainer's accounts or settings.
4. Run `LocalEdition.Smoke` on the resulting folder. Verify client installation/repair when its manifest changes. Playtest the actual game explicitly before marking a new player release stable.
5. Create a Git tag such as `v0.1.0-preview.1`, then a GitHub Release containing the generated Windows ZIP, its SHA-256 and clear installation/update notes. Preview builds should be marked prerelease. Upload the ZIP as a release asset rather than committing build outputs to source.

Publishing a community release does not require deploying to the production game server. Players update by extracting the selected ZIP and reopening its launcher. There is no production launcher auto-update feed in local mode.

Retain Cloudflare objects referenced by older supported releases. Content is addressed by hash, so existing objects must not be overwritten with different bytes. Changes to the public download domain should preserve old published URLs or provide compatible redirects.

For the first public release, choose the project license and settle the redistribution treatment of the imported/game-derived material listed in `THIRD-PARTY-NOTICES.md`. That decision has not been made by this prepared snapshot.

Relevant platform documentation: [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases), [repository licensing](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository), and [Cloudflare R2 public custom domains](https://developers.cloudflare.com/r2/buckets/public-buckets/).
