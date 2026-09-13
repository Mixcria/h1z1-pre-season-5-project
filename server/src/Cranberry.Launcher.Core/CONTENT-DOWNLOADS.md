# Optional content origin

Downloads continue through the authenticated game API unless the trusted local
launcher configuration explicitly sets `ContentBaseUrl`. No existing endpoint or
profile is changed by this feature.

For a future object-storage deployment, add this property to the applicable
`launcher.json` (merge it with the existing server and player settings):

```json
{
  "ContentBaseUrl": "https://downloads.example.invalid/aug2017/content/"
}
```

The value is a directory URL. A trailing slash is optional and is added when
missing. Each file is fetched as `<ContentBaseUrl><UPPERCASE_SHA256>`; no extra
`api/content/` segment is added. This matches the uppercase filenames in
`Publish-Game.ps1`'s `content` directory. Copy those objects under the chosen
prefix without renaming them. A nested prefix is retained.

Only HTTPS is accepted, including for localhost. User information, queries,
fragments, backslashes and whitespace are rejected. The installer does not accept
an origin from the manifest or follow download redirects. Configure the final
content directory, not an API endpoint that redirects to it.

The content client has its own lifetime and connection pool, normal platform TLS
certificate verification, cookies disabled and no account bearer token, default
credentials or game certificate pin. The manifest is still fetched from the
existing authenticated, trusted game API. File hashes, path validation and Range
resume remain unchanged. A content error fails install/repair; it does not silently
switch back to the game server. The game can only launch after verification.

This route expects content accessible without launcher account credentials. If
downloads require private access, design that access mechanism before enabling
this setting; do not place R2 credentials or signed query strings in it.

An omitted or empty value uses the original relative `api/content/{hash}` route
and the existing authenticated API client. Setting it back to `""` restores that
behavior. For existing users, update the applicable saved preference file as well
as the distributed profile: saved settings take precedence and do not automatically
inherit a later content-origin change from the packaged profile. Profiles supplied
with `--profile` have separate saved preference files.

Before enabling the setting, publish and verify all immutable objects, validate
full and resumed downloads from the actual storage origin, then publish the
trusted API manifest. Keep the original API content route during the launcher
rollout. This change does not provision storage or change release publication.
