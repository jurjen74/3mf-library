# 3MF Library

A self-hosted preview site for the 3D print folder. It shows the folders exactly as they are on disk, with a card per
model using the plate previews Bambu Studio embeds in every 3MF (STLs and bare geometry get a rendered
preview), a page per file with photos, description and a 3D viewer, search, favourites, downloads (single files or a
whole folder as ZIP) and an "Open in Bambu Studio" button. It can be installed as an app on a phone's home screen. It's a single ASP.NET Core
(.NET 10) container that mounts the library **read-only**. See [CLAUDE.md](CLAUDE.md) for decisions, architecture,
infrastructure and deployment.

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

## Accounts and Nginx Proxy Manager

Everything is behind a login, except direct visitors from `TRUSTED_NETWORKS` (the home LAN). A request that arrives
through a proxy always needs the login, even when it says it comes from the LAN.

**First run:** open the site from the home network (`http://192.168.2.213:8085`) and create the first account on the
page it shows (or via the "No account yet" banner). Only direct visitors from the home network can do this, never
through the proxy. Accounts live in `data/users.db` with hashed passwords; change yours under the user name in the top
bar (this logs out your other devices). Admins manage everyone else under **Users** in the top bar: add people,
reset a password (both can require the person to choose their own password at the next login, on by default),
grant or remove admin, delete (resets and deletes end that person's sessions; the last admin
always stays). Forgot your own password and no other admin? Stop the container, delete `data/users.db`, and set
up again.

**Proxy host in Nginx Proxy Manager:** forward `http://192.168.2.213:8085`, request a Let's Encrypt certificate
with Force SSL, keep *Cache Assets* off, and set `KNOWN_PROXIES=<npm-ip>` in `/opt/3mf-library/.env` so the app sees
the visitor's real address and that the connection is HTTPS (secure cookie, per-visitor rate limit on logins).

## Configuration

| Variable | Default | |
|---|---|---|
| `LIBRARY_PATH` | `/library` | Library root (read-only) |
| `DATA_PATH` | `/data` | SQLite index, preview cache, data-protection keys |
| `SCAN_INTERVAL` | `00:03:00` | How often the library is re-walked; only changed files are re-read |
| `SETTLE_DELAY` | `00:00:10` | A new or changed file is only read once its size and time stay the same this long (Cloud Sync writes in parts) |
| `IGNORE` | see `LibraryOptions` | `;`-separated name globs to skip; dotfiles are always skipped |
| `SITE_TITLE` | `3D Print library` | |
| `TRUSTED_NETWORKS` | – | `;`-separated networks whose direct visitors need no login, e.g. `192.168.2.0/24` |
| `KNOWN_PROXIES` | – | Proxy IPs whose `X-Forwarded-For`/`-Proto` are believed (in `.env`) |

The index is a cache: stop the container, delete `data/library.db*` and `data/cache/`, and the next start rebuilds both.
