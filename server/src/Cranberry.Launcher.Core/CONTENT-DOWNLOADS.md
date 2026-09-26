# CDN downloads

The trusted game API still supplies the game manifest, login and signed launcher
release manifest. Game assets and launcher executables can be delivered from a
public HTTPS content origin such as Cloudflare R2 with a custom domain.

## Routing and existing players

The launcher reads `GET api/downloads` through its existing TLS-verified/pinned
API client. The service reads `download-host.json` from the host root on each
request and sends `Cache-Control: no-store`. An absent file, or an older server
returning HTTP 404/204, keeps the original API download routes.

After publishing and verifying the release, the host configuration is:

```json
{
  "version": 1,
  "contentBaseUrl": "https://downloads.example.com/aug2017/content/",
  "launcherContentBaseUrl": "https://downloads.example.com/aug2017/content/"
}
```

Leave `launcherContentBaseUrl` empty until the signed launcher executable has
also been uploaded and verified. Both fields are directory URLs; the requested
object is `<base><UPPERCASE_SHA256>`. A trailing slash is added if absent.

Discovered URLs are not saved to player preferences. Existing players pick up
routing changes after updating to a launcher that supports `api/downloads`.
An explicit local `ContentBaseUrl` remains an operator override for game assets;
clear it to use centrally managed routing. It does not override launcher updates.
Invalid configurations and failed content requests fail visibly instead of
silently transferring a 14 GB install back to the game server.

Deploy the service endpoint and the updated launcher before enabling routing.
Keep legacy API content routes available throughout the rollout: older launchers
use them to obtain their first updated executable. Roll back routing by atomically
replacing `download-host.json` with version 1 and both URLs empty. Players with
an explicit local override must clear that override separately.

## Download behavior

CDN game installs use four simultaneous file downloads by default. A local
`ConcurrentDownloads` value from 1 to 8 can override this. API-only installs and
verify-only checks remain sequential. HTTP/2 is requested with HTTP/1.1 fallback.
Matching installed files are reused; partial files resume through HTTP Range;
transient failures retry up to three attempts. Duplicate hashes share a worker so
they cannot race over the same partial file. All replacements are SHA-256 checked
before promotion. The install lock and signed launcher update checks remain active.

The content client has a separate lifetime and connection pool, normal platform
TLS verification, cookies disabled and no account bearer token, default
credentials or game certificate pin. It follows at most three HTTPS redirects
without forwarding game credentials. Initial content URLs reject user information,
queries, fragments, backslashes and whitespace. Use the final custom domain for R2;
the publisher rejects redirects when verifying that deployment.

Content is public. Never place R2 credentials or private workspace files in the
launcher profile, bucket or public routing document.

## Cloudflare setup and publication

See [the R2 publishing guide](../../tools/launcher/R2-DOWNLOADS.md) for bucket,
cache rule, upload, verification and release ordering. Normal global caching
serves North America and international locations, including nearby locations
outside mainland China. Cross-border performance in mainland China remains
variable; this setup does not purchase Cloudflare China Network or promise
domestic Chinese CDN delivery.
