# 3MF Library

A self-hosted preview site for a folder of 3D print files. Point it at the folder where your 3MF and STL files live
(on the same machine or on a NAS) and it shows that folder exactly as it is on disk, with a preview for every model:

- **Previews from the files themselves**: the preview images Bambu Studio, OrcaSlicer and PrusaSlicer store in a 3MF
  (one per plate where the slicer has plates), plus MakerWorld photos. STLs and 3MFs without images get a rendered preview.
- **A page per file** with all plates, photos, the description, print time, filament and colours, and a **3D viewer**.
- **Search, sorting and filters**, Recent, favourites, "include subfolders" for big kits.
- **Downloads** of single files or a whole folder as ZIP, and **Open in your slicer** (Bambu Studio, OrcaSlicer,
  UltiMaker Cura, Creality Print, ElegooSlicer; PrusaSlicer through the local-copy helper).
- **Accounts** with an admin, browse-only accounts (no downloads), optional "no login at home", light and dark mode,
  installable as an app on a phone.

**Documentation, with screenshots: [www.3mf-library.com](https://www.3mf-library.com)**

It's one small container (ASP.NET Core, .NET 10). The library is mounted **read-only**: the site never changes, moves or
deletes your files. Organise them in folders as you like; the folders are the only organisation the site uses.

## Quick start

Step-by-step guides for Linux, Mac and Windows (Docker Desktop), Synology, Unraid, TrueNAS SCALE, Raspberry Pi,
Proxmox, Portainer and a VPS are on
[www.3mf-library.com/install/overview](https://www.3mf-library.com/install/overview/). The short version: you need
Docker with Compose. On the machine that will run the site (Linux; on a Mac or Windows PC, follow
[that guide](https://www.3mf-library.com/install/desktop/)):

```bash
mkdir 3mf-library && cd 3mf-library
curl -fsSLO https://raw.githubusercontent.com/jurjen74/3mf-library/main/docker-compose.yml
curl -fsSLO https://raw.githubusercontent.com/jurjen74/3mf-library/main/compose.nfs.yml
curl -fsSL -o .env https://raw.githubusercontent.com/jurjen74/3mf-library/main/.env.example
```

Edit `.env`. The essentials:

- `LIBRARY_DIR`: the folder with your models (or an NFS share, see below).
- `TRUSTED_NETWORKS`: your home network, e.g. `192.168.1.0/24`. Visitors there need no login. Leave it empty to
  require a login for everyone.
- `TZ`: your time zone, e.g. `Europe/Amsterdam`.
- `PUID`/`PGID`: the user that may read the library (default 1000). Check with `id` or `ls -n` on the library.

Create the data folder for that user and start:

```bash
mkdir -p data && sudo chown 1000:1000 data    # use your PUID:PGID
docker compose up -d
```

Open `http://<this machine>:8085`. The first scan reads every file once; large libraries take a few minutes.

### The first account

The site asks you to create the first account, which becomes the admin:

- **With `TRUSTED_NETWORKS` set**, open the site from that network, directly on its own address (not through a
  proxy).
- **Without it**, the setup page asks for a **setup code** that is printed in the container log, so nobody who happens
  to find the site first can claim it:

  ```bash
  docker logs 3mf-library | grep "setup code"
  ```

Admins add other people under **Users** (optionally asking them to choose their own password at the first login),
reset passwords, grant admin rights and make accounts **browse only** (they see everything, the 3D view
included, but can't download or open files in a slicer). Everyone changes their own password under their name in the top bar.

## Library on a NAS (NFS)

Instead of a local folder, the library can come straight from an NFS export (Synology, TrueNAS, Unraid, …). Export the
folder read-only to the Docker host, then add to `.env`:

```bash
COMPOSE_FILE=docker-compose.yml:compose.nfs.yml
NFS_SERVER=192.168.1.10
NFS_EXPORT=/volume1/3d-models
```

A folder that is mounted on the host some other way (SMB, a sync client) works with `LIBRARY_DIR` as well. File
system notifications don't work over network shares, so the site checks for changes every few minutes
(`SCAN_INTERVAL`) and waits until a new file stops growing before it reads it (`SETTLE_DELAY`); a sync tool that
writes files in parts is fine.

## Reverse proxy and HTTPS

To use the site away from home, put it behind a reverse proxy with HTTPS (Nginx Proxy Manager, Caddy, Traefik, plain
nginx): forward to `http://<docker host>:8085` and leave asset caching off. Then set the proxy's IP address in `.env`:

```bash
KNOWN_PROXIES=192.168.1.20
```

The site then sees visitors' real addresses (for the login rate limit) and knows the connection is HTTPS (secure
cookies). Anything that comes through the proxy **always needs a login**, even when it claims to come from the home
network; only direct visitors from `TRUSTED_NETWORKS` skip it. Don't forward port 8085 itself to the internet.

## Configuration

Set these in `.env`. Everything is optional.

| Setting | Default | |
|---|---|---|
| `LIBRARY_DIR` | `./library` | Folder with the models on the Docker host, mounted read-only |
| `COMPOSE_FILE`, `NFS_SERVER`, `NFS_EXPORT`, `NFS_VERSION` | – | Library on an NFS share instead (see above; version default 4.1) |
| `TRUSTED_NETWORKS` | – | `;`-separated networks whose direct visitors need no login, e.g. `192.168.1.0/24` |
| `KNOWN_PROXIES` | – | Reverse proxy IP address(es) |
| `PORT` | `8085` | Port on the Docker host |
| `PUID`, `PGID` | `1000` | User and group the app runs as (reads the library, writes `./data`) |
| `MEMORY_LIMIT` | `3g` | Memory cap for the container; very large models need room while they're read |
| `TZ` | `UTC` | Time zone for dates on the site |
| `SITE_TITLE` | `3D Print library` | Name in the header and the browser tab |
| `UPDATE_CHECK` | on | Once a day, look up the latest release on GitHub and show "x.y.z available" in the footer; `false` turns it off |
| `SCAN_INTERVAL` | `00:03:00` | How often the library is checked; only new and changed files are read |
| `SETTLE_DELAY` | `00:00:10` | A new or changed file is read once its size and time stay the same this long |
| `IGNORE` | see `.env.example` | `;`-separated name patterns to skip; names starting with a dot are always skipped |
| `IMAGE`, `PULL_POLICY` | `ghcr.io/jurjen74/3mf-library:latest`, `missing` | Image to run; pin a version (e.g. `:1.0.0`) for predictable updates |

The container itself understands `LIBRARY_PATH` and `DATA_PATH` too (`/library`, `/data`); with Compose you don't need
them.

## Updating and backups

```bash
docker compose pull && docker compose up -d
```

`data/` holds everything the site keeps:

- `users.db`: accounts and favourites. **Back this up**; updates migrate it in place.
- `library.db` and `cache/`: the index and the previews, rebuilt from the library whenever needed. To start the index
  afresh: stop the container, delete `data/library.db*` and `data/cache/`, start again.
- `keys/`: keys for login cookies and slicer links. Without them everyone just logs in again.

## Slicers and the local copy

File pages have an **Open in …** button. Each computer picks its slicer under **Account → This computer** (stored in that
browser; default Bambu Studio):

| Slicer | Downloads from this site | Local copy (helper) |
|---|---|---|
| Bambu Studio | 3MF only (asks once whether to trust the site) | yes |
| OrcaSlicer | yes (needs a download folder set in OrcaSlicer) | yes |
| PrusaSlicer | no, it only downloads from Printables/Thingiverse | yes |
| UltiMaker Cura | yes | yes |
| Creality Print | yes | no (downloads instead) |
| ElegooSlicer | yes | no (downloads instead) |

Link formats follow each slicer's source code and [Manyfold](https://github.com/manyfold3d/manyfold)'s handlers; Bambu
Studio has been tried with the real app, the others not yet. The button isn't shown on phones and tablets.

**Local copy:** if a computer has the same library synced (OneDrive, Dropbox, Syncthing, a mounted share), the slicer
can open the **actual file** instead of a downloaded copy, so saving updates the original. Install the small helper
once, then tick **Open models from my local copy** under Account → This computer. The helper only opens model files
inside the folder you give it.

**Mac:** builds `3MF Library Opener.app` in `~/Applications` and registers `threemflib://` links. Pass the local
library folder (default: `3D Print` in OneDrive). Uninstall by deleting the app.

```bash
tools/mac-opener/build.sh "$HOME/path/to/your/library"
```

**Windows:** copies the helper to `%LOCALAPPDATA%\3MF Library Opener` and registers `threemflib://` for your user (no
admin rights). Pass `-LibraryFolder` (default: `3D Print` in OneDrive); it finds the installed slicers itself, or pass
`-BambuStudio`, `-OrcaSlicer`, `-PrusaSlicer` or `-Cura`. Run it again after installing another slicer; uninstall with
`uninstall.ps1`.

```powershell
powershell -ExecutionPolicy Bypass -File tools\windows-opener\install.ps1 -LibraryFolder "D:\3D Print"
```

## Development

```bash
dotnet test Library.slnx
dotnet run --project src/Library.Web --launch-profile http    # http://localhost:5180, indexes ./dev-library
```

`dev-library/` and `data/` are git-ignored: put a copy of some models in `dev-library/`. Locally, `localhost` counts as
the home network, so no login is needed.

Container images are built by GitHub Actions (`.github/workflows/build.yml`) for amd64 and arm64 on every push to
`main` (`latest`) and for version tags (`v1.2.3` → `1.2.3`, `1.2`). To build one yourself (Docker or Podman; the .NET
SDK cross-compiles, so another architecture needs no emulation):

```bash
docker build --platform linux/amd64 -t 3mf-library:local .
```

Then set `IMAGE=3mf-library:local` and `PULL_POLICY=never` in `.env`. `compose.dev.yml` runs a local build against
`dev-library/`. Architecture, decisions and conventions are in [CLAUDE.md](CLAUDE.md).

## License

[MIT](LICENSE), © Jurjen Ladenius. Included third-party parts keep their own licences:

- `src/Library.Web/wwwroot/lib/three/`: [three.js](https://threejs.org), MIT (`LICENSE` in that folder).
- `src/Library.Web/wwwroot/fonts/`: [Inter](https://rsms.me/inter/), SIL Open Font License 1.1 (`Inter-OFL.txt`).
- NuGet packages: Dapper (Apache 2.0), HtmlSanitizer (MIT), Microsoft.Data.Sqlite (MIT).
- `src/Library.Web/wwwroot/backgrounds/`: login backgrounds generated with Magnific; they are **not** covered by the
  MIT licence. Replace them with your own images if you redistribute the site.
