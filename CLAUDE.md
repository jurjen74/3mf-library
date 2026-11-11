# CLAUDE.md

Guidance for Claude Code sessions in this repository. Repo: `jurjen74/3mf-library` on GitHub.

**The repository is general: anyone can run it.** The owner's own installation (servers, addresses, deploy steps) is
in their private notes, referenced from the git-ignored `CLAUDE.local.md`. Never put those details (IP addresses,
host names, user names, domains, MAC addresses, NAS paths) in tracked files.

## What this is

A self-hosted preview site for a folder of 3D print files (the owner's is a synced cloud folder on a NAS). It shows
the folders exactly as on disk, previews every model, and adds file pages, search, a 3D viewer, favourites, folder ZIPs,
"Open in <slicer>" and accounts. One ASP.NET Core container, published to GHCR; the library is mounted **read-only**
and never written to. README.md is the short guide for people installing it; the full documentation is the `docs/` site. When behaviour or
settings change, update both (and re-run the screenshot script when the UI changes).

## Decisions (don't re-litigate)

- **Own app, not Manyfold.** Manyfold was tried and rejected: its one-model-per-folder approach doesn't fit (most 3MFs
  sit loose in category folders) and its previews were poor. The **folder structure on disk is the only organisation**;
  no tags, no restructuring.
- **Previews come from the 3MF itself** (Bambu Studio's `Metadata/plate_N.png`, MakerWorld photos in
  `Auxiliaries/Model Pictures/`); only STLs and image-less 3MFs are rendered (CPU renderer). Card cover = plate 1.
- **Plain docker-compose, not Aspire** (the owner's other project uses Aspire; this one deliberately doesn't).
- **Images from GitHub Actions** (`.github/workflows/build.yml`): tests, then amd64 + arm64 to
  `ghcr.io/jurjen74/3mf-library` (`latest` from main, `1.2.3`/`1.2` from `v*` tags, `sha-…`). The Dockerfile builds on
  the build platform and cross-compiles (no emulation). Locally the owner has **Podman, not Docker**.
- **Versions:** release = tag `vX.Y.Z` + GitHub release (owner's go-ahead); CI passes `git describe` as the `VERSION`
  build arg, shown in the footer. Only release builds (and main builds after a release) check GitHub for updates.
- **General compose, settings in `.env`**: `docker-compose.yml` (bind mount `LIBRARY_DIR`, everything else from `.env`
  with defaults), `compose.nfs.yml` (library from NFS, enabled with `COMPOSE_FILE=docker-compose.yml:compose.nfs.yml`),
  `.env.example` documents every setting. Keep the three in step with README.md's configuration table.
- **Accounts live in the app** (first-run setup, Users page for admins), not in env vars.
  Multi-user with an admin flag; optional forced password change for new/reset accounts (default on).
- **Browse-only accounts** (`can_download = 0`, set by admins on the Users page): `Session.CanDownload` (home network
  or admin always true) gates `/download` (images excepted: folder and image pages show them), `/zip`,
  `/slicer-link` and `/mesh` with a 403, and the views hide the buttons, the 3D toggle and Account → This computer.
  The 3D viewer is off for them on purpose: its mesh is a printable copy.
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
  `threemflib://open?path=<library path>`, handled by a per-OS helper that opens the synced file in the slicer, so
  saves go back via the sync tool. Library paths on the site equal the paths in the synced folder. Any page can fire that
  scheme, so the helpers only open model files inside the configured folder (no `..`, `.`, empty segments, absolute or
  drive paths).
  - macOS `tools/mac-opener`: `build.sh [folder]` → `~/Applications/3MF Library Opener.app`, folder baked in at build
    time (default OneDrive's `3D Print`). Tested end to end on the owner's Mac, works.
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
                        PngWriter, MeshSimplifier (vertex clustering), MeshCodec (viewer format "M3M1"), MeshService,
                        MeshWorkGate (one full mesh load at a time, viewer and indexer together: memory)
  Updates/              AppVersion (InformationalVersion from the image build: `git describe`, "dev" locally),
                        UpdateChecker (daily GitHub latest-release lookup → "x.y.z available" in the footer)
  Data/                 LibraryDb (index schema), LibraryQueries, Records
  Auth/                 UserStore (users.db), FavouriteStore, Session (cookie, security stamp, forced change), TrustedNetwork
  Pages/                Browse, File, Search, Recent, Favourites, Login, Setup, Account, Users, Logout, Error
                        Shared/ModelListView: ?sort= / ?type= (3mf|stl) as plain links; Browse ?all=1 = include subfolders
  wwwroot/              css/site.css, js/site.js (classic), js/head.js (in <head>: light/dark choice, localStorage
                        `library.theme`; page-transition hooks), js/viewer.js (ES module), lib/three (three.js 0.186.1, vendored), icons/,
                        fonts/ (Inter, OFL), backgrounds/ (login photos per theme, generated with Magnific, JPEG 1920 px)
tests/Library.Tests/    xUnit v3 (Microsoft Testing Platform); synthetic fixtures only (TestThreeMf, TestMeshes)
docs/                   Documentation site (Astro Starlight) → www.3mf-library.com via .github/workflows/docs.yml;
                        scripts/screenshots.mjs (Playwright) shoots src/assets/screenshots/ from a demo instance
                        (git-ignored demo-library/ with CC-licensed models, credited in project/credits.md)
tools/mac-opener/       macOS helper for threemflib:// links: build.sh (osacompile + URL scheme + ad-hoc sign),
                        opener.applescript, open-model.sh (decoding, checks; DRY_RUN=1 / LIBRARY_ROOT for testing)
tools/windows-opener/   Windows helper: install.ps1 / uninstall.ps1, launch.vbs, open-model.ps1 (-DryRun -ConfigPath)
```

Conventions: .NET 10, `TreatWarningsAsErrors`, central package versions (`Directory.Packages.props`), nullable on.
No inline scripts/styles (strict CSP: `default-src 'self'`); swatches are inline SVG. Never put the owner's model files
in the repo — tests build synthetic 3MF/STL in code.

## Data and migrations

`DATA_PATH` (`/data`; `./data` next to the compose file):

- `library.db` — the **index, a cache**. `LibraryDb.SchemaVersion` bump ⇒ drop and rebuild everything (fine).
  `LibraryScanner.IndexerVersion` bump ⇒ every file re-read once (cache keys include it). Search: FTS5 `files_fts`.
  Files the library folder didn't deliver (I/O or permission error, stored as `LibraryScanner.ReadFailedError`, no
  server path) are retried hourly (`RetryFailedAfter`), since the soft NFS mount can fail; files that are really
  broken (not a ZIP, over a limit) keep their own message and wait until they change.
- `cache/<key>/` — extracted previews, renders, viewer meshes (`mesh_*.bin.gz`). Key = hash(path, size, mtime,
  indexer version), so URLs are immutable; orphaned folders are removed after each complete scan.
- `users.db` — accounts and favourites. **Never dropped**: `UserStore.Migrate` runs numbered steps in place
  (v1 users, v2 must_change_password, v3 favourites, v4 can_download). Add new steps at the end; back up before deploying a new step.
- `keys/` — data-protection keys (cookies, antiforgery, slicer links).

## Auth model (security-sensitive — keep these rules)

- Fallback policy: everything needs an authenticated user except login/setup pages, static assets, `/health`,
  `/manifest.webmanifest` and `/dl/{token}` (token-checked).
- `TRUSTED_NETWORKS` (e.g. 192.168.1.0/24): a **direct** visitor from there becomes a "Home network" user. Any
  request with forwarding headers, or from a `KNOWN_PROXIES` address, is never trusted — the proxy sits on the LAN.
- `KNOWN_PROXIES`: `UseForwardedHeaders` only runs when it's set (an empty list would trust everyone's X-Forwarded-For).
- First account only via setup, never through a proxy: from `TRUSTED_NETWORKS` when set, otherwise only with the
  one-time `SetupCode` that is logged at startup (so whoever reaches a fresh site first can't claim it). Admin rights checked in the DB per
  request (not in the cookie). Security stamp in the cookie; password change/reset ends other sessions.
- Login/setup POSTs are rate-limited per client IP, password changes (`account` policy) per account; the rate
  limiter runs after authentication for that. Previews/meshes are `Cache-Control: private`.

## Deployment

General: `docker compose up -d` with the image from GHCR and a `.env` (README.md). The owner's server, how it's
deployed there and its `.env` are in the private notes (`CLAUDE.local.md`); read them before deploying.

Env vars the app reads: `LIBRARY_PATH`, `DATA_PATH`, `SCAN_INTERVAL` (00:03:00), `SETTLE_DELAY` (00:00:10), `IGNORE`,
`SITE_TITLE`, `TRUSTED_NETWORKS`, `KNOWN_PROXIES`, `TZ`, `UPDATE_CHECK` (default on); compose adds `LIBRARY_DIR`, `NFS_*`, `PUID`/`PGID`, `PORT`,
`MEMORY_LIMIT`, `IMAGE`, `PULL_POLICY`. Empty values fall back to the app's defaults. `/health` (anonymous) returns 503
`scan-failed` while scans throw. Inotify doesn't work over network shares, so the indexer polls; sync tools write files
in parts, hence the settle delay.

## Commands

```bash
dotnet test Library.slnx                                    # run from the repo root
dotnet run --project src/Library.Web --launch-profile http   # http://localhost:5180, loopback trusted, ../../dev-library
```

`dev-library/` is a git-ignored copy of (part of) a library; the owner refreshes it from their synced folder (see
`CLAUDE.local.md`). Building an image locally: `podman build --platform linux/amd64 -t 3mf-library:latest .`

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
- Static files outside the anonymous/forced-password-change allow-list (`Session.OpenWhileChangeRequired`) get
  redirected: add new top-level `wwwroot` folders there (fonts/ and backgrounds/ were missed once).
- macOS `sips` AVIF output decodes in an `<img>` but isn't painted as a CSS background in Chromium, and the static
  assets SDK has no `.avif` content type: the login backgrounds are plain JPEG.
- Bambu Studio strips `../`-like sequences from slicer URLs; keep the last URL segment plain (`SlicerLinks.SafeName`).

## Working with the owner

- Commit only when asked; "commit" and "push" usually come as separate requests. End commit messages with the
  attribution line from the session's system reminder.
- Deploying finished features to the owner's server is the established routine (steps in the private notes); it needs
  the owner's go-ahead each time.
- Never set or reveal real passwords; test accounts only in the local, git-ignored `data/`.

## Open items

- Making the repository and the GHCR package public (licence: MIT, `LICENSE`; the Magnific login backgrounds are
  excluded from it until the owner has checked Magnific's terms, see README → License).
- Before going public: the git history still contains the owner's infrastructure details (removed from the files);
  consider publishing a fresh repository without that history.
- Optional: share links (signed, expiring, scoped), OIDC / forward-auth login, a no-login mode, a container health
  check, downloadable releases of the local-copy helpers.
