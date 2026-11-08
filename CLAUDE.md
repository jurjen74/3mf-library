# CLAUDE.md

Guidance for Claude Code sessions in this repository. Repo: `jurjen74/3mf-library` (private GitHub).

## What this is

A self-hosted preview site for the owner's 3D print folder (OneDrive "3D Print" → synced to a Synology NAS). It shows
the folders exactly as on disk, previews every model, and adds file pages, search, a 3D viewer, favourites, folder ZIPs,
"Open in Bambu Studio" and accounts. Live at **https://models.13thpenthouse.com**. One ASP.NET Core container; the
library is mounted **read-only** and never written to.

## Decisions (don't re-litigate)

- **Own app, not Manyfold.** Manyfold was tried and rejected: its one-model-per-folder approach doesn't fit (most 3MFs
  sit loose in category folders) and its previews were poor. The **folder structure on disk is the only organisation**;
  no tags, no restructuring.
- **Previews come from the 3MF itself** (Bambu Studio's `Metadata/plate_N.png`, MakerWorld photos in
  `Auxiliaries/Model Pictures/`); only STLs and image-less 3MFs are rendered (CPU renderer). Card cover = plate 1.
- **Plain docker-compose, not Aspire** (the owner's other project uses Aspire; this one deliberately doesn't).
- **No Docker on the Mac: Podman.** Images are built there for amd64 (the SDK cross-compiles, no emulation) and copied
  to the VM. Automated builds (GitHub Actions → GHCR) are still open.
- **Accounts live in the app** (first-run setup from the home network, Users page for admins), not in env vars.
  Multi-user with an admin flag; optional forced password change for new/reset accounts (default on).
- **Home network needs no login**; anything through the reverse proxy always does. No public share links yet.
- **Open in Bambu Studio**: macOS `bambustudioopen://<encoded url>`, Windows/Linux `bambustudio://open?file=<encoded url>`
  (from Bambu Studio's `GUI_App.cpp`). Bambu only trusts MakerWorld and asks once ("not from a trusted site") — answer
  Yes. The slicer downloads without cookies, so the button fetches a 15-minute signed link (`/dl/{token}/{name}`) for
  that one file. Hidden on touch devices: no slicer on phones (Bambu Handy can't open 3MFs either). Tested and working.
- **Other slicers** (owner only has Bambu Studio; the others are untested): per-browser choice under Account → This
  computer (`localStorage` `library.slicer`: bambu | orca | prusa | cura | creality | elegoo). Download-link formats,
  checked against the slicers' sources and Manyfold's `app/lib/file_handlers`: `orcaslicer://open?file=`,
  `cura://open?file=` (no domain filter; filename from Content-Disposition), `crealityprintlink://open?file=`,
  `elegooslicer://open?file=` (these two from Manyfold only). Bambu Studio only opens 3MF by link (Manyfold's finding),
  so STLs need the local copy. PrusaSlicer's downloader only accepts printables.com/thingiverse.com, so it's
  local-copy only. Helpers support bambu/orca/prusa/cura (`&slicer=<id>`; Mac by bundle id or app name, Windows by the
  exe paths `install.ps1` found, `config.json` → `slicers`); Creality Print and ElegooSlicer fall back to the download
  link. The button is hidden when neither route works.
- **Local copy on a computer with the library synced** (opt-in per browser: Account → This computer, shown on
  mouse/trackpad devices; `localStorage` `library.openLocalCopy`): the button then fires
  `threemflib://open?path=<library path>`, handled by a per-OS helper that opens the synced file with Bambu Studio, so
  saves go back via OneDrive. Library paths on the site equal the paths in the OneDrive folder. Any page can fire that
  scheme, so the helpers only open model files inside the configured folder (no `..`, `.`, empty segments, absolute or
  drive paths).
  - macOS `tools/mac-opener`: built and registered on the owner's Mac (`~/Applications/3MF Library Opener.app`, folder
    `~/Library/CloudStorage/OneDrive-Personal/3D Print` baked in at build time). Tested end to end, works.
  - Windows `tools/windows-opener`: `install.ps1` → `%LOCALAPPDATA%\3MF Library Opener` (`open-model.ps1`,
    `launch.vbs`, `config.json`) + `HKCU\Software\Classes\threemflib`. `launch.vbs` hides the console and passes the
    link via the `THREEMFLIB_URL` env var (WshShell.Run expands `%NAME%`). Script logic tested in the
    `mcr.microsoft.com/powershell` container (`-DryRun -ConfigPath`); registry/launcher **not yet tested on Windows**.
    Written for Windows PowerShell 5.1 (no PS7-only syntax; pass `[string[]]` to `Path.Combine`).
- No offline mode / service worker; the "app" is manifest + icons only.

## Code layout

```
src/Library.Web/
  Program.cs            DI, middleware order, minimal endpoints (/img, /mesh, /download, /zip, /dl, /slicer-link, /manifest)
  LibraryOptions.cs     env-var settings          LibraryPaths.cs  path normalisation / traversal guard
  Display.cs            formatting + URL helpers  FolderZip.cs     streamed folder ZIP   SlicerLinks.cs  signed slicer links
  Indexing/             ThreeMfReader (metadata/plates/photos), LibraryScanner, IndexerService, DescriptionSanitizer
  Meshes/               StlReader, ThreeMfMeshReader (core + Production ext + plates + paint), MeshRenderer (CPU),
                        PngWriter, MeshSimplifier (vertex clustering), MeshCodec (viewer format "M3M1"), MeshService
  Data/                 LibraryDb (index schema), LibraryQueries, Records
  Auth/                 UserStore (users.db), FavouriteStore, Session (cookie, security stamp, forced change), TrustedNetwork
  Pages/                Browse, File, Search, Recent, Favourites, Login, Setup, Account, Users, Logout, Error
                        Shared/ModelListView: ?sort= / ?type= (3mf|stl) as plain links; Browse ?all=1 = include subfolders
  wwwroot/              css/site.css, js/site.js (classic), js/theme.js (in <head>: light/dark choice, localStorage
                        `library.theme`), js/viewer.js (ES module), lib/three (three.js 0.186.1, vendored), icons/
tests/Library.Tests/    xUnit v3 (Microsoft Testing Platform); synthetic fixtures only (TestThreeMf, TestMeshes)
tools/mac-opener/       macOS helper for threemflib:// links: build.sh (osacompile + URL scheme + ad-hoc sign),
                        opener.applescript, open-model.sh (decoding, checks; DRY_RUN=1 / LIBRARY_ROOT for testing)
tools/windows-opener/   Windows helper: install.ps1 / uninstall.ps1, launch.vbs, open-model.ps1 (-DryRun -ConfigPath)
```

Conventions: .NET 10, `TreatWarningsAsErrors`, central package versions (`Directory.Packages.props`), nullable on.
No inline scripts/styles (strict CSP: `default-src 'self'`); swatches are inline SVG. Never put the owner's model files
in the repo — tests build synthetic 3MF/STL in code.

## Data and migrations

`DATA_PATH` (`/data`, on the VM `/opt/3mf-library/data`):

- `library.db` — the **index, a cache**. `LibraryDb.SchemaVersion` bump ⇒ drop and rebuild everything (fine).
  `LibraryScanner.IndexerVersion` bump ⇒ every file re-read once (cache keys include it). Search: FTS5 `files_fts`.
- `cache/<key>/` — extracted previews, renders, viewer meshes (`mesh_*.bin.gz`). Key = hash(path, size, mtime,
  indexer version), so URLs are immutable; orphaned folders are removed after each complete scan.
- `users.db` — accounts and favourites. **Never dropped**: `UserStore.Migrate` runs numbered steps in place
  (v1 users, v2 must_change_password, v3 favourites). Add new steps at the end; back up before deploying a new step.
- `keys/` — data-protection keys (cookies, antiforgery, slicer links).

## Auth model (security-sensitive — keep these rules)

- Fallback policy: everything needs an authenticated user except login/setup pages, static assets, `/health`,
  `/manifest.webmanifest` and `/dl/{token}` (token-checked).
- `TRUSTED_NETWORKS` (192.168.2.0/24): a **direct** visitor from there becomes a "Home network" user. Any request
  with forwarding headers, or from a `KNOWN_PROXIES` address, is never trusted — NPM sits on the LAN.
- `KNOWN_PROXIES`: `UseForwardedHeaders` only runs when it's set (an empty list would trust everyone's X-Forwarded-For).
- First account only via setup from the home network, never through the proxy. Admin rights checked in the DB per
  request (not in the cookie). Security stamp in the cookie; password change/reset ends other sessions.
- Login/setup/account POSTs are rate-limited per client IP. Previews/meshes are `Cache-Control: private`.

## Environment and infrastructure

| What | Where |
|---|---|
| Library source | OneDrive `3D Print` → Synology Cloud Sync (polls every 600 s; additions, changes and deletions, verified 2026-11-08) → `192.168.2.13:/volume1/3dprinting` (NFS v4.1, read-only export to the VM). A change in OneDrive reaches the site within ~10–14 min (sync interval + settle + 3-min scan) |
| Docker host | Proxmox VM 130, Debian 13.7, `192.168.2.213` (DHCP reservation `BC:24:11:0E:B7:1D`), user `jurjenladenius`, SSH key-only |
| App on the VM | `/opt/3mf-library/` → `docker-compose.yml` (copied from the repo), `.env` (`KNOWN_PROXIES=192.168.2.233`, not in git), `data/` |
| Reverse proxy | Nginx Proxy Manager LXC `192.168.2.233` (reservation `BC:24:11:97:4F:C1`) → `http://192.168.2.213:8085`; Force SSL, HTTP/2, HSTS, Cache Assets off |
| Router | 80/443 → NPM; port 8085 is not forwarded |
| Container | port 8085→8080, UID 1000, read-only root fs, no capabilities, NFS volume `library` mounted `:ro` |

Env vars: `LIBRARY_PATH`, `DATA_PATH`, `SCAN_INTERVAL` (00:03:00), `SETTLE_DELAY` (00:00:10), `IGNORE`, `SITE_TITLE`,
`TRUSTED_NETWORKS`, `KNOWN_PROXIES`. Inotify doesn't work over NFS, so the indexer polls; Cloud Sync writes files in
parts, hence the settle delay.

## Commands

```bash
dotnet test Library.slnx                                    # run from the repo root
dotnet run --project src/Library.Web --launch-profile http   # http://localhost:5180, loopback trusted, ../../dev-library
rsync -a --delete ~/Library/CloudStorage/OneDrive-Personal/"3D Print/" dev-library/   # refresh the local copy
```

Deploy (from the Mac; the VM never builds):

```bash
podman build --platform linux/amd64 -t 3mf-library:latest .
podman save localhost/3mf-library:latest | ssh jurjenladenius@192.168.2.213 docker load
scp docker-compose.yml jurjenladenius@192.168.2.213:/opt/3mf-library/    # only when it changed
ssh jurjenladenius@192.168.2.213 'cd /opt/3mf-library && docker compose up -d && docker image prune -f'
```

Razor views are compiled at build time: restart `dotnet run` after editing `.cshtml`. The browser-pane preview uses
`.claude/launch.json` (`library-web`).

## Gotchas learned the hard way

- `HttpClient` resolves `/../` before sending — traversal tests need encoded paths (or `curl --path-as-is`).
- Antiforgery tokens are bound to the identity: a token fetched as a home-network user fails when posted anonymously.
- `ZipArchive` writes synchronously: `/zip` enables `AllowSynchronousIO` for that response only and runs on a worker.
- Dapper: map SQLite integers to `long` (expose `bool` as a computed property); `string.Create(provider, …)` needs a
  literal interpolated string, not a ternary of two.
- `PageModel` already has `File(...)` methods — don't name a page property `File`.
- CSS `display: block` on an element overrides its `hidden` attribute; add `[hidden] { display: none }` rules.
- Bambu 3MFs: model metadata stops at `<resources>`; description HTML is escaped twice plus `&#34;` for attribute
  quotes, and title/designer/license are escaped twice too (`Dooku&amp;apos;s`); painted colours are per-triangle hex trees read from the last character (`DominantPaintState`).
- Bambu Studio strips `../`-like sequences from slicer URLs; keep the last URL segment plain (`SlicerLinks.SafeName`).

## Working with the owner

- Commit only when asked; "commit" and "push" usually come as separate requests. End commit messages with the
  attribution line from the session's system reminder.
- Deploying to VM 130 over SSH is the established routine for finished features; say so when doing it.
- Never set or reveal real passwords; test accounts only in the local, git-ignored `data/`.

## Open items

- Automated builds: GitHub Actions → GHCR, then `docker compose pull` on the VM.
- Owner to check: VM 130 in the Proxmox backup job; Proxmox firewall rules for VM 130 (8085 from LAN/NPM only,
  22 from LAN).
- Optional: share links (signed, expiring, scoped), OIDC login.
- On the VM, `data/users.db.before-v2` / `before-v3` are pre-migration backups that can be deleted.
