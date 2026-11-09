---
title: Library on a NAS
description: Read the library straight from an NFS share on a Synology, TrueNAS, Unraid or other NAS.
---

If your models live on a NAS, the container can mount the share itself over NFS. It's mounted **read-only**: the site
can't change anything on the NAS.

## 1. Export the folder on the NAS

Share the folder over NFS, **read-only**, to the address of the machine that runs Docker. For example:

- **Synology DSM:** Control Panel → Shared Folder → (folder) → Edit → NFS Permissions → Create: hostname or IP of the
  Docker host, privilege *Read-only*, squash *No mapping*, security *sys*.
- **TrueNAS:** Shares → Unix (NFS) Shares → Add: the dataset path, *Read Only*, and the Docker host under *Hosts*.
- **Unraid:** Shares → (share) → NFS Security Settings: *Export: Yes*, *Security: Private*, rule
  `<docker-host-ip>(ro)`.

The files must be readable for the user the container runs as (`PUID`, default 1000), or for everyone.

## 2. Turn on the NFS add-on

Add to `.env`:

```bash
COMPOSE_FILE=docker-compose.yml:compose.nfs.yml
NFS_SERVER=192.168.1.10           # the NAS
NFS_EXPORT=/volume1/3d-models     # the exported path (Synology: /volume1/<shared folder>)
# NFS_VERSION=4.1                 # default; use 3 for an older NAS
```

`COMPOSE_FILE` makes Docker Compose use both files, so `docker compose up -d` keeps working as before.
`LIBRARY_DIR` is ignored then.

```bash
docker compose up -d
docker compose logs --tail 20
```

The log shows a line like `Scan finished … 235 files in 57 folders` when the share is readable.

## Good to know

- **Changes are picked up by polling.** File system notifications don't travel over NFS, so the site checks every
  `SCAN_INTERVAL` (3 minutes). A new file is only read once its size and time stopped changing for `SETTLE_DELAY`
  (10 seconds), so files that a sync tool writes in parts are read when they're complete.
- **A NAS that's away doesn't empty the site.** If the share can't be read, the site keeps showing the last index and
  says so in the footer. Nothing is removed until a complete scan sees that a file is really gone.
- **Hiccups are retried.** The share is mounted "soft": a NAS that doesn't answer gives an error instead of hanging
  the container. Files that couldn't be read that way are tried again within the hour.
- **Synology housekeeping folders** (`@eaDir`, `#recycle`, `#snapshot`) are skipped by default, as are macOS `._*`
  files and anything that starts with a dot. See `IGNORE` in [Configuration](/reference/configuration/).

## Other ways to mount

Already have the share mounted on the host (NFS in `/etc/fstab`, SMB/CIFS, a sync client like Syncthing or rclone)?
Then skip the add-on and point `LIBRARY_DIR` at the mount point.
