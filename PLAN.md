# 3MF Library — Preview Site Plan

A small, self-hosted website that runs in one container on the
**Proxmox** host, mounts the 3D Print folder from the Synology
**read-only over NFS**, and shows it exactly as it is on disk: the same
folders, a grid of previews, a detail page per file, and downloads.
It's for personal use only: **no sharing for now** (share links are
parked under M5).

## Decisions so far

- **Manyfold: rejected.** Its one-model-per-folder approach doesn't fit
  this library (103 of 120 3MFs sit loose in category folders), and its
  previews weren't good enough.
- **ThumbHost3mf** already handles Finder previews on the Mac.
- **OneDrive → NAS sync** is in place. The NAS copy is what the site
  reads.
- **Build our own**, using **the folder structure as the only
  organisation**. No tagging or restructuring needed.
- **Hosting: Docker on Proxmox** (i9-12900H, 64 GB). That's x86_64, so
  an amd64-only image is enough, and CPU and RAM aren't a constraint.
  The library comes from the Synology over NFS.
- **Reverse proxy: Nginx Proxy Manager** (set up later). It terminates
  TLS with Let's Encrypt; the app only speaks plain HTTP on the internal
  network.

## What the library actually contains (checked 2026-11-07)

| | |
|---|---|
| Folders | 57; mostly one level of categories (`Stuff`, `Aquarium`, `Peter`, …), a few deeper kits (`titan3d.com/…/3MF Files - …`) |
| 3MF | 120. 103 at depth 1, the rest 2–4 levels deep. Sizes from 0.6 MB to 87 MB |
| STL | 80, with no embedded preview |
| Other | PNG (16), F3D (7), TXT (6), PDF (3), ZIP (3); 48 `datapackage.json` (**Manyfold leftovers**, ignore or delete) and `.DS_Store` |
| Producer | All sampled 3MFs come from **Bambu Studio** (2.02–2.09) |

What a Bambu 3MF gives us for free (it's a ZIP, so we read single
entries without unpacking the whole file):

| Entry | Use |
|---|---|
| `Metadata/plate_N.png`, `plate_N_small.png` | **Per-plate previews, in colour.** The main thumbnail, plus a plate gallery |
| `Auxiliaries/.thumbnails/thumbnail_3mf.png` / `_middle` / `_small` | Cover image for the project |
| `Auxiliaries/Model Pictures/*.webp` | MakerWorld photos of the real print, for a photo gallery |
| `3D/3dmodel.model` `<metadata>` | `Title`, `ProfileTitle`, `Designer`, `License`, `Description` (HTML, double-escaped), `CreationDate`, `Origin` |
| `Metadata/model_settings.config` | Plate list and object names per plate |
| `Metadata/project_settings.config` | `filament_colour` array, shown as colour swatches |
| `Metadata/slice_info.config` | Print time and weight per plate (only when the file was sliced and saved) |
| `3D/Objects/object_N.model` | Geometry (production extension, many files), for the 3D viewer |

So for 3MF, **no rendering is needed**. The slicer's own images are
better than anything we'd render. Rendering is only needed for STLs and
non-Bambu 3MFs.

---

## Architecture

```
 Internet ──► Nginx Proxy Manager (TLS, Let's Encrypt, models.ladenius.net)
                        │ http, X-Forwarded-For/Proto
                        ▼
 Proxmox (i9-12900H, 64 GB)
 ┌────────────────────────────────────────────────────────────────┐
 │  Docker host (VM or LXC)                                         │
 │  container: 3mf-library  (ASP.NET Core, .NET 10, port 8080)      │
 │   ├─ Indexer (BackgroundService)                                 │
 │   │    periodic stat-walk (every 2–5 min) + Rescan button        │
 │   │    ├─ 3MF reader  → metadata, plates, previews, photos       │
 │   │    └─ STL/mesh renderer (CPU rasteriser, parallel) → PNG     │
 │   ├─ SQLite  /data/library.db   (index, search, settings)        │
 │   ├─ Cache   /data/cache/<sha>/… (extracted PNG/WebP, meshes)     │
 │   └─ Web: Razor Pages + htmx, three.js viewer                    │
 │                 ▲ /library  (NFS volume, read-only)              │
 └─────────────────┼──────────────────────────────────────────────┘
                   │ NFS v4.1, ro export
 Synology 192.168.2.13:  /volume1/3dprinting  ◄── Cloud Sync ◄── OneDrive "3D Print"
```

### Why this stack

- **ASP.NET Core / .NET 10** matches `dmel-2027` and `Fundraiser 2021`,
  so it's familiar tooling with one small amd64 image and no Node
  runtime in production.
- **Razor Pages + htmx** render the pages on the server. That makes
  folder pages fast, with no SPA build and working back/forward and deep
  links, and htmx handles lazy grids and search-as-you-type.
- **SQLite** for one user and a few hundred files. The database is only
  a cache: delete it and a rescan rebuilds everything. The login comes
  from env, so nothing in it is irreplaceable.
- **three.js** (CDN or vendored) only on the detail page, for the 3D
  viewer.

### Core principles

1. **The filesystem is the truth.** The URL path equals the folder path
   (`/browse/Stuff/Kintsu Shizu 261107`). The site never writes to
   `/library`.
2. **Read entries, don't unpack.** `ZipArchive` seeks through the central
   directory, so an 87 MB 3MF costs a few KB of reads to get its
   thumbnails.
3. **Cache by content.** The key is `(path, size, mtime)`, with an
   optional SHA-256 to detect moves and renames. Changed file → re-extract;
   unchanged → served from `/data/cache`.

---

## Components

### 1. Indexer

- Starts with a full scan, then **polls**. Over NFS, inotify (and so
  `FileSystemWatcher`) doesn't see changes the Synology makes, so the
  indexer stat-walks the tree every few minutes (`SCAN_INTERVAL`,
  default 3 min). With about 350 files and 57 folders, a walk is a few
  hundred `stat` calls, well under a second. Only new or changed
  `(path, size, mtime)` entries get re-read. There's also a "Rescan now"
  button.
- A file only counts once it has been stable (same size and mtime) for
  two consecutive walks, because Cloud Sync writes files in parts.
- Ignore patterns (configurable): `datapackage.json`, `.DS_Store`,
  `._*`, `@eaDir`, `#recycle`, `*.tmp`.
- Per file type:
  - **3MF:** read metadata, plates, filament colours and slice info;
    extract `plate_N.png`, the `.thumbnails` cover and `Model
    Pictures/*.webp` into the cache. Fallback when there are no images
    (non-Bambu 3MF): render the mesh.
  - **STL:** render a preview with the CPU rasteriser (component 2) and
    store bounding-box dimensions and triangle count.
  - **Images (png/jpg/webp):** thumbnail, and shown as folder
    attachments and gallery photos.
  - **Others (pdf, f3d, zip, txt):** listed as attachments with an icon
    and a download link.
- Guards: limits on entry size, entry count and decompressed bytes (zip
  bomb protection); a corrupt file is flagged in the UI instead of
  crashing the scan.

### 2. Mesh renderer (for STL and image-less 3MF)

- A small software rasteriser in C#: parse binary/ASCII STL (or 3MF mesh
  XML), auto-orient to an isometric view, flat shading with one
  directional light plus ambient, z-buffer, 512×512, and a transparent
  background.
- Written to PNG/WebP with ImageSharp (or SkiaSharp). No GPU, X11 or
  headless browser in the container.
- About 200–300 lines. Renders run in the background so a scan never
  blocks the UI.

### 3. 3MF → viewer mesh

- three.js's `3MFLoader` is unlikely to handle Bambu's production
  extension (geometry split over `3D/Objects/*.model` with `p:path`
  references). **Verify early.**
- Plan B, which is preferred anyway for 87 MB files: the server merges
  the objects of **one plate** into a single binary mesh (GLB, or a
  compact custom format) with per-object filament colour, caches it, and
  the browser loads that. Plate-at-a-time keeps downloads small.

### 4. Web UI

| Page | Contents |
|---|---|
| **Browse** `/browse/{*path}` | Breadcrumbs and a folder tree sidebar (collapsible on mobile); sub-folder tiles with a mosaic of up to 4 child previews; a **card grid** for each model (cover image, title or filename, designer, plate count badge, filament swatches, file size); attachments listed below. Sort by name, date or size; grid or list view toggle |
| **Detail** `/file/{*path}` | Large preview and **plate strip** (plate_1…N, with object names on hover); tabs for **Plates**, **Photos** (MakerWorld webp gallery with lightbox), **3D** (three.js viewer, loaded on demand) and **Info** (title, designer, license, origin, creation date, print time and weight per plate when available, sanitised description HTML). Buttons: **Download**, **Open in Bambu Studio** (custom URL scheme, to verify) |
| **Search** `/search?q=` | Searches filename, title, designer, folder name and object names (SQLite FTS5); results as the same cards |
| **Recent** `/recent` | Newest files by mtime. "What did I add lately?" |
| **Admin** `/admin` | Rescan, index stats, errors and corrupt files |

Design: clean card grid with a dark/light theme. Thumbnails are
lazy-loaded with fixed aspect ratios so the grid doesn't jump, and
images are served with long-lived cache headers and ETags.

### 5. Access

Only you use it, so there's no sharing, no user management and no
public pages.

- **Login:** a single account set via env (`ADMIN_USER` plus a hashed
  `ADMIN_PASSWORD_HASH`) using cookie auth with a long-lived "remember
  me", so you log in once per device.
- **Behind Nginx Proxy Manager:** `ForwardedHeaders` is enabled, but
  only for the NPM address (`KNOWN_PROXIES=<npm-ip>`). That gives correct
  client IPs, the `https` scheme and secure cookies
  (`PUBLIC_BASE_URL=https://models.ladenius.net`). Nothing NPM-specific
  is needed beyond a plain proxy host to `http://192.168.2.213:8085`
  with "Block common exploits" on. No websockets and no uploads, so the
  defaults are fine.
- **Trusted networks (optional):** `TRUSTED_NETWORKS=192.168.2.0/24`
  lets direct LAN visitors browse without logging in. **Pitfall:** every
  external request reaches the app *from NPM's LAN address*. The check
  must therefore use the forwarded client IP, and requests that arrived
  via NPM never count as trusted. Simplest rule: request came through a
  known proxy → login required.
- **NPM access list (optional):** with no share links there's nothing
  that has to stay open, so NPM can also restrict the proxy host (e.g.
  basic auth or an IP allowlist) as a second gate in front of the app's
  login.
- **Later (M5):** share links (random token scoped to a file or folder,
  with expiry, revocable). Paths are already canonicalised and checked
  against a root, so adding scoped tokens doesn't need a redesign.
- **Hardening:** every path is canonicalised and must resolve inside
  `/library`; description HTML goes through `HtmlSanitizer`; the
  container runs as a non-root UID/GID that can read the share; the
  library is mounted `:ro`; responses send CSP headers.

---

## Repository layout

```
3mf-library/
├─ src/Library.Web/            ASP.NET Core app (Razor Pages, endpoints, auth)
│  ├─ Indexing/                Scanner, watcher, readers (ThreeMf, Stl, Image)
│  ├─ Rendering/               CPU rasteriser, plate→GLB exporter
│  ├─ Data/                    EF Core + SQLite (or Dapper), FTS5 search
│  ├─ Pages/                   Browse, File, Search, Recent, Login, Admin
│  └─ wwwroot/                 css, htmx, three.js viewer module
├─ tests/Library.Tests/        xUnit; fixtures: 2–3 small real 3MFs + STLs
├─ Dockerfile                  multi-stage, mcr.microsoft.com/dotnet/aspnet:10.0 (chiseled)
├─ docker-compose.yml          for Synology Container Manager
├─ .github/workflows/          build, test, push linux/amd64 image to GHCR
└─ PLAN.md
```

### Hosting on Proxmox

**Docker host: a new, dedicated Debian VM.** It mounts NFS itself, needs
no privileged-container tricks, and is easy to snapshot and back up with
Proxmox. Nginx Proxy Manager stays in its own existing LXC and proxies
to this VM over the LAN.

| Setting | Value |
|---|---|
| OS | Debian 13.7 (`debian-13.7.0-amd64-netinst.iso`), no desktop |
| CPU | 2–4 vCPU, type `host`. More cores speed up the first STL render pass |
| RAM | 2 GB (4 GB if you like headroom) |
| Disk | 20 GB on local-lvm or ZFS (OS + Docker + `/data` cache, which stays well under 1 GB) |
| Network | `vmbr0`, DHCP with a reservation bound to the VM's MAC (192.168.2.0/24) |
| Extras | QEMU guest agent on; included in the Proxmox backup schedule |

The app listens on `192.168.2.213:8085`. Only the NPM LXC and your LAN need
to reach it. The step-by-step setup is in **Provisioning the VM (M0)**
below.

**Nginx Proxy Manager (LXC):** a proxy host `models.ladenius.net` →
`http://192.168.2.213:8085`, with a Let's Encrypt certificate, Force SSL and
Block common exploits. The LXC's IP goes into `KNOWN_PROXIES`.

### Provisioning the VM (M0)

This can be done in parallel with M1, since it doesn't need any app
code. VM ID **130**, MAC `BC:24:11:0E:B7:1D`, IP **192.168.2.213**
(DHCP reservation). Still a placeholder: `<npm-ip>` (the NPM LXC).

**1. Create the VM in Proxmox.** Upload `debian-13.7.0-amd64-netinst.iso`
to `local` → ISO Images (or use *Download from URL* there, and check the
SHA-256 against Debian's `SHA256SUMS`). Then either use the GUI wizard with the settings
from the table above, or run on the Proxmox host:

```bash
qm create 130 --name 3mf-library --ostype l26 \
  --cores 4 --cpu host --memory 4096 --balloon 2048 \
  --scsihw virtio-scsi-single --scsi0 local-lvm:20,discard=on,ssd=1,iothread=1 \
  --net0 virtio,bridge=vmbr0,firewall=1 \
  --ide2 local:iso/debian-13.7.0-amd64-netinst.iso,media=cdrom \
  --boot 'order=scsi0;ide2' --agent enabled=1 --onboot 1
```

Adjust the storage name and ISO filename to what you actually have.
**Don't start it yet.**

**2. Bind a DHCP address to the VM.** Proxmox assigns the MAC address
at creation, so you can make the reservation before the first boot. The
installer then gets the right address straight away.
- Read the MAC: `qm config 130 | grep net0` gives
  `virtio=BC:24:11:0E:B7:1D,…`. In the GUI it's under VM 130 →
  Hardware → Network Device.
- In the router's DHCP settings, add a static lease or reservation for
  that MAC. ✅ Done: `BC:24:11:0E:B7:1D` → **192.168.2.213**.
- The MAC stays the same as long as you don't delete or re-add net0,
  and it survives backup restores in place. Restoring to a *new* VM ID
  gets a new MAC, so update the reservation then.
- Check that the **NPM LXC** also has a reserved or static address. Its
  IP goes into `KNOWN_PROXIES` and the firewall rule; if it changes,
  proxied requests are rejected or treated as untrusted.

**3. Synology NFS permission** (DSM on 192.168.2.13):
- Control Panel → File Services → NFS: enable NFS, maximum protocol
  **NFSv4.1**.
- Control Panel → Shared Folder → `3dprinting` → Edit → NFS Permissions
  → Create:
  - Hostname/IP: `192.168.2.213`
  - Privilege: **Read only**
  - Squash: **Map all users to guest** (or to a dedicated read-only
    user that can read the folder)
  - Security: `sys`
  - Tick *Allow users to access mounted subfolders*.
  - Leave *Allow connections from non-privileged ports* off.
- Check that the mapped user or guest has **read** permission on
  `3dprinting` (Shared Folder → Permissions). Otherwise the mount works
  but every file is "permission denied".

**4. Install Debian 13.** Run `qm start 130` and open the console.
- Hostname `3mf-library`; network configured **automatically (DHCP)**.
  It picks up the reserved `192.168.2.213`, and the installer shows the
  address it got.
- Set no root password, so the installer gives your user sudo.
- Software selection: **SSH server + standard system utilities** only.
  No desktop.

Afterwards, eject the ISO (Hardware → CD/DVD → *Do not use any media*).

**5. Base configuration** (over SSH as your user):

```bash
sudo apt update && sudo apt full-upgrade -y
```

```bash
sudo apt install -y qemu-guest-agent nfs-common unattended-upgrades curl ca-certificates
```

```bash
sudo systemctl enable --now qemu-guest-agent
```

```bash
sudo dpkg-reconfigure -plow unattended-upgrades
```

- Copy your SSH key (`ssh-copy-id <user>@192.168.2.213`), then set
  `PasswordAuthentication no` in `/etc/ssh/sshd_config.d/local.conf` and
  run `sudo systemctl reload ssh`.
- Timezone: `sudo timedatectl set-timezone Europe/Amsterdam`.

**6. Install Docker** (Docker's own repository, not Debian's
`docker.io`):

```bash
sudo install -m 0755 -d /etc/apt/keyrings && sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
```

```bash
echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo $VERSION_CODENAME) stable" | sudo tee /etc/apt/sources.list.d/docker.list
```

```bash
sudo apt update && sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

```bash
sudo usermod -aG docker $USER
```

Log out and back in, then `docker run --rm hello-world` should work
without sudo. Optionally cap container logs in
`/etc/docker/daemon.json`:
`{"log-driver":"local","log-opts":{"max-size":"10m"}}`, then run
`sudo systemctl restart docker`.

**7. Test the NFS export by hand** before Docker ever touches it:

```bash
sudo mkdir -p /mnt/nfstest && sudo mount -t nfs -o ro,nfsvers=4.1 192.168.2.13:/volume1/3dprinting /mnt/nfstest
```

```bash
ls /mnt/nfstest | head && find /mnt/nfstest -iname '*.3mf' | wc -l
```

Check that:
- the count matches the library (about 120 3MFs);
- `ls /mnt/nfstest/Aquarium` shows `水草球.3mf` correctly, not `????.3mf`;
- `head -c 4 "/mnt/nfstest/Stuff/R2-D2.3mf" | xxd` shows `PK..`, so the
  file is readable;
- `touch /mnt/nfstest/x` fails with *Read-only file system*.

Then run `sudo umount /mnt/nfstest`. The real mount is done by the
compose NFS volume.

**8. App folder.**

```bash
sudo mkdir -p /opt/3mf-library/data && sudo chown -R 1000:1000 /opt/3mf-library/data && sudo chown $USER /opt/3mf-library
```

`docker-compose.yml` goes in `/opt/3mf-library` in M4, so `./data`
resolves to `/opt/3mf-library/data`. UID 1000 matches the compose
`user:` line.

**9. Firewall: use the Proxmox firewall, not ufw.** Ports that Docker
publishes bypass ufw/iptables rules inside the VM, so a ufw rule for
8085 wouldn't actually protect anything. The Proxmox firewall filters at
the bridge, outside the VM:
- Datacenter → Firewall → Options: Firewall **Yes**. First make sure
  the datacenter or host rules still allow your own access to the
  Proxmox GUI (8006) and SSH.
- VM 130 → Firewall → Options: Firewall **Yes**, Input policy **DROP**.
- VM 130 → Firewall → Rules:
  - `IN ACCEPT` from `<npm-ip>`, TCP 8085
  - `IN ACCEPT` from `192.168.2.0/24`, TCP 8085 (direct LAN use)
  - `IN ACCEPT` from `192.168.2.0/24`, TCP 22
  - Outbound stays open (NFS to the NAS, apt, GHCR).

**10. Backups and a baseline snapshot.**
- Datacenter → Backup: add VM 130 to the nightly job (PBS or vzdump),
  mode *snapshot*. The NFS library isn't inside the VM, so backups stay
  small.
- Take a snapshot `provisioned` (VM 130 → Snapshots) to roll back to
  before deploying the app.

**M0 is done when:**
- `ssh 192.168.2.213` works with your key and not with a password;
- the Proxmox VM summary shows the IP (guest agent works);
- `docker compose version` runs without sudo;
- the manual NFS test passed on every point in step 7;
- `nc -zv 192.168.2.213 8085` from a machine outside 192.168.2.0/24 is
  refused or times out once something listens there (test this in M4);
- the VM is in the backup job, and the `provisioned` snapshot exists.

**Container configuration** (compose on the Docker VM; NFS mounted by
Docker itself, so no `/etc/fstab` needed):

```yaml
services:
  3mf-library:
    image: ghcr.io/<you>/3mf-library:latest   # pin a version tag in practice
    container_name: 3mf-library
    user: "1000:1000"
    ports:
      - "8085:8080"
    environment:
      LIBRARY_PATH: /library
      DATA_PATH: /data
      SITE_TITLE: "3D Print library"
      PUBLIC_BASE_URL: "https://models.ladenius.net"
      KNOWN_PROXIES: "192.168.2.x"            # IP of the NPM LXC (fill in)
      ADMIN_USER: jurjen
      ADMIN_PASSWORD_HASH: "<generated by `docker run … hash-password`>"
      TRUSTED_NETWORKS: "192.168.2.0/24"      # direct LAN only, see Access
      SCAN_INTERVAL: "00:03:00"
      IGNORE: "datapackage.json;.DS_Store;._*;@eaDir;#recycle"
    volumes:
      - library:/library:ro
      - ./data:/data                          # SQLite + cache, on the VM disk
    restart: unless-stopped
    security_opt:
      - no-new-privileges:true
    read_only: true
    tmpfs:
      - /tmp

volumes:
  library:
    driver: local
    driver_opts:
      type: nfs
      o: "addr=192.168.2.13,nfsvers=4.1,ro,soft,timeo=100"
      device: ":/volume1/3dprinting"
```

- `/data` lives on the VM's local disk, not on NFS. SQLite doesn't do
  well on network filesystems, and it lets Proxmox backups include it.
- `soft` mount: if the NAS goes away, reads fail with an error instead
  of hanging the app. The site keeps serving cached previews and marks
  the library "offline".
- Only `/data` holds state, and the database is mostly rebuildable.
  Back up the VM via Proxmox Backup Server or vzdump.

---

## Milestones

| # | Deliverable | Done when |
|---|---|---|
| **M0 — Provision VM** (1 evening, in parallel with M1) | Synology NFS permission, Debian 13 VM on Proxmox, Docker, manual NFS test, Proxmox firewall, backup + snapshot (see Provisioning the VM) | All the checks at the end of the provisioning section pass |
| **M1 — Browse** (1 weekend) | Project skeleton, Dockerfile, indexer for 3MF (metadata + plate PNGs), SQLite, Browse page with folder tree and card grid, Download | **Done 2026-11-08.** Running on VM 130 at `http://192.168.2.213:8085`: first NFS scan 3.3 s, 235 files in 57 folders, 0 errors; 119 of 120 3MFs have a preview (the Kintsu "(Geometry)" export has none embedded, so it waits for M3); 33 tests pass; library mount verified read-only, container runs as UID 1000 |
| **M2 — Details & search** | Detail page (plates, photos, info, sanitised description, swatches, slice info), Search (FTS5), Recent, polling indexer with a stability check, attachments | **Built 2026-11-08.** File page with plate switcher (objects, print time and weight per plate when sliced), MakerWorld photo gallery with lightbox, sanitised description, previous/next in folder (← →); search over name, title, designer, folder, object names and description (FTS5, prefix match) plus folder matches; Recent page; 10 s settle check before reading new or changed files; orphaned-cache cleanup. Full library: 385 photos, 77 descriptions, 300 plates, 0 errors; 59 tests pass |
| **M3 — STL & 3D** | CPU rasteriser for STL / image-less 3MF; plate→GLB export; three.js viewer tab | All 80 STLs have a preview; plates of the 87 MB TitanPals file open in the viewer at an acceptable speed |
| **M4 — Login & deploy** | Login, trusted networks, admin page, hardening, GitHub Actions → GHCR (amd64), compose with the NFS volume deployed to the M0 VM, NPM proxy host | Running on Proxmox behind NPM at `models.ladenius.net`; from a phone on 4G it asks for a login and then works; an external request is never treated as LAN; the library mount is verified read-only |
| M5 — Nice to have | Share links, "Open in Bambu Studio", dimensions and triangle count, ZIP download of a folder, favourites, PWA icon, OIDC | — |

### Risks to check early (spikes in M1)

1. **NFS mount from the Docker VM**: export permissions, squash and UID
   mapping, so the container user can read every file, including the
   Chinese/German filenames (`水草球.3mf`, `Büste`), and the mount
   really is read-only (`touch` must fail). M0 step 7 checks this on the
   VM itself; re-check from inside the container in M4.
2. **NFS timestamps**: confirm mtime changes after Cloud Sync updates a
   file, so the polling indexer notices edits and not just new files.
3. **Bambu production-extension geometry**: confirm the plate→mesh merge,
   including object transforms in `3dmodel.model` `<build>` items, before
   building the viewer.
4. **OneDrive placeholder files**: make sure the NAS copy holds real
   files, not cloud-only stubs. Cloud Sync downloads everything by
   default.

---

## Open questions

- The NPM LXC's IP?
- **Cloud Sync doesn't pass deletions on** (confirmed 2026-11-08): the
  NAS has every OneDrive file with identical sizes, plus the 48
  `datapackage.json` files deleted from OneDrive the day before. Files
  deleted or renamed in OneDrive will therefore stay on the NAS and keep
  showing on the site. Check the Cloud Sync task (direction, and the
  option that keeps files removed at the source).
- Hostname for the NPM proxy host (`models.ladenius.net` is a
  placeholder)?

Settled: no sharing for now (2026-11-07); the Manyfold
`datapackage.json` leftovers were deleted from OneDrive; VM 130 has
been created with DHCP reservation `BC:24:11:0E:B7:1D` →
192.168.2.213. M0 steps 1–5 and 7 passed on 2026-11-08: SSH is key-only,
and the NFS share root holds the library directly (120 3MFs, UTF-8
names fine, mount read-only).
