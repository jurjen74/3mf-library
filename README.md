# 3MF Library

A self-hosted preview site for the 3D print folder. It shows the folders exactly as they are on disk, with a card per
model using the plate previews Bambu Studio embeds in every 3MF, and download links. It's a single ASP.NET Core
(.NET 10) container that mounts the library **read-only**. See [PLAN.md](PLAN.md) for the design and milestones.

## Develop

`dev-library/` (git-ignored) is a local copy of part of the library. The app indexes it into `data/`.

```bash
dotnet run --project src/Library.Web --launch-profile http   # http://localhost:5180
```

```bash
dotnet test
```

## Container

Build the image for the Proxmox VM (amd64) on the Mac. The SDK cross-compiles, so nothing is emulated:

```bash
podman build --platform linux/amd64 -t 3mf-library:latest .
```

To try it locally against `dev-library/`:

```bash
podman compose -f compose.dev.yml up --build   # http://localhost:8085
```

## Deploy to VM 130 (192.168.2.213)

```bash
podman save localhost/3mf-library:latest | ssh jurjenladenius@192.168.2.213 docker load
```

```bash
scp docker-compose.yml jurjenladenius@192.168.2.213:/opt/3mf-library/
```

```bash
ssh jurjenladenius@192.168.2.213 'cd /opt/3mf-library && docker compose up -d && docker compose logs --tail 20'
```

The site is at `http://192.168.2.213:8085`. The NFS volume in `docker-compose.yml` mounts
`192.168.2.13:/volume1/3dprinting` read-only.

## Configuration

| Variable | Default | |
|---|---|---|
| `LIBRARY_PATH` | `/library` | Library root (read-only) |
| `DATA_PATH` | `/data` | SQLite index, preview cache, data-protection keys |
| `SCAN_INTERVAL` | `00:03:00` | How often the library is re-walked; only changed files are re-read |
| `SETTLE_DELAY` | `00:00:10` | A new or changed file is only read once its size and time stay the same this long (Cloud Sync writes in parts) |
| `IGNORE` | see `LibraryOptions` | `;`-separated name globs to skip; dotfiles are always skipped |
| `SITE_TITLE` | `3D Print library` | |

The index is a cache: stop the container, delete `data/library.db*` and `data/cache/`, and the next start rebuilds both.
