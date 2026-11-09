---
title: Configuration
description: Every setting of 3MF Library, set in the .env file next to docker-compose.yml.
---

All settings go in `.env`, next to `docker-compose.yml`. Every one is optional; the default is used when it's missing
or empty. After a change, apply it with `docker compose up -d`.

## Library

| Setting | Default | |
|---|---|---|
| `LIBRARY_DIR` | `./library` | Folder with the models on the Docker host. Mounted read-only. |
| `COMPOSE_FILE` | – | `docker-compose.yml:compose.nfs.yml` to mount an NFS share instead ([guide](/guides/nas/)) |
| `NFS_SERVER` | – | The NAS's address (with the NFS add-on) |
| `NFS_EXPORT` | – | The exported path, e.g. `/volume1/3d-models` |
| `NFS_VERSION` | `4.1` | NFS protocol version; `3` for older NAS systems |
| `SCAN_INTERVAL` | `00:03:00` | How often the library is checked for new, changed and removed files (`hh:mm:ss`) |
| `SETTLE_DELAY` | `00:00:10` | A new or changed file is read once its size and time stayed the same this long |
| `IGNORE` | see below | Names to skip: `;`-separated, `*` and `?` as wildcards, matched against each file and folder name |

The default `IGNORE` list is
`datapackage.json;._*;@eaDir;#recycle;#snapshot;Thumbs.db;desktop.ini;*.tmp`. Names that start with a dot are always
skipped. Put the value in quotes in `.env`, because of the `#`.

## Access

| Setting | Default | |
|---|---|---|
| `TRUSTED_NETWORKS` | – | `;`-separated networks whose **direct** visitors need no login, e.g. `192.168.1.0/24;fd00::/8`. Empty: everyone logs in. |
| `KNOWN_PROXIES` | – | `;`-separated addresses of your reverse proxy. Only then are `X-Forwarded-For` and `X-Forwarded-Proto` believed. |

See [Reverse proxy and HTTPS](/guides/reverse-proxy/) and [Security](/reference/security/).

## Container

| Setting | Default | |
|---|---|---|
| `PORT` | `8085` | Port on the Docker host |
| `PUID`, `PGID` | `1000` | User and group the app runs as. They need read access to the library and write access to `./data`. |
| `MEMORY_LIMIT` | `3g` | Memory cap. Reading a very large model for the 3D viewer can take a few GB for a moment. |
| `IMAGE` | `ghcr.io/jurjen74/3mf-library:latest` | The image; pin a version like `:1.0.0` for predictable updates |
| `PULL_POLICY` | `missing` | `missing`: pull once; `always`: check on every `up`; `never`: only use a local image |

## Site

| Setting | Default | |
|---|---|---|
| `SITE_TITLE` | `3D Print library` | Name in the header, the browser tab and the installed app |
| `TZ` | `UTC` | Time zone for dates on the site, e.g. `Europe/Amsterdam` |

## Inside the container

The image itself reads `LIBRARY_PATH` (`/library`) and `DATA_PATH` (`/data`), and listens on port 8080. With the
compose file you don't need to change those; they're useful when you run the image some other way (Kubernetes,
Portainer, Unraid templates):

```bash
docker run -d --name 3mf-library -p 8085:8080 --user 1000:1000 \
  -v /srv/3d-prints:/library:ro -v /srv/3mf-library-data:/data \
  -e TRUSTED_NETWORKS=192.168.1.0/24 -e TZ=Europe/Amsterdam \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  ghcr.io/jurjen74/3mf-library:latest
```

## Health check

`GET /health` (no login needed) answers `200` with `{"status":"ok",…}` while scans succeed, and `503` with
`"status":"scan-failed"` when the last scan failed. Use it for uptime monitoring.
