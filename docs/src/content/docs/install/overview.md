---
title: Choose your setup
description: Which installation guide fits your hardware, and what every setup has in common.
---

3MF Library runs as one Docker container. Every setup comes down to the same three things: the **image**, a folder
with your **models** (mounted read-only), and a small **data** folder for the index, previews and accounts. Pick the
guide for the machine you have:

| You have | Guide | Good to know |
|---|---|---|
| A Linux server or PC (Debian, Ubuntu, Fedora …) | [Docker Compose on Linux](/install/linux/) | The reference setup; the other guides build on it |
| Your own Mac or Windows PC | [Mac or Windows PC](/install/desktop/) | Docker Desktop; everyone logs in, online while the computer is |
| A Synology NAS | [Synology](/install/synology/) | Container Manager, models straight from a shared folder |
| An Unraid server | [Unraid](/install/unraid/) | Docker tab, no plugins needed |
| A TrueNAS SCALE system | [TrueNAS SCALE](/install/truenas/) | Custom app from YAML |
| A Raspberry Pi 4 or 5 | [Raspberry Pi](/install/raspberry-pi/) | 64-bit OS required |
| Proxmox VE | [Proxmox](/install/proxmox/) | A small Debian VM is the easy route |
| Portainer on any of these | [Portainer](/install/portainer/) | A stack with absolute paths |
| A rented server on the internet | [VPS with HTTPS](/install/vps/) | Caddy for HTTPS, your models synced to it |

Just want to try it? The [quick start](/start/quick-start/) has the shortest route.

## What every setup needs

- **Docker** with Compose (or a NAS app that runs Docker containers).
- **amd64 or arm64.** The image is published for both: Intel/AMD machines, Apple silicon, Raspberry Pi 4/5 with a 64-bit
  OS, ARM-based NAS models. 32-bit ARM isn't supported.
- **Memory:** about 250 MB in normal use. Opening a very large model in the 3D viewer needs a few GB for a moment;
  the container is capped at 3 GB by default (`MEMORY_LIMIT`).
- **Disk:** the image is about 100 MB to download (230 MB unpacked). Previews and 3D meshes take a small fraction of
  the size of your library.
- **Access to your models** for the user the container runs as (`PUID`/`PGID`), and **write access to the data
  folder**.

## The image

```text
ghcr.io/jurjen74/3mf-library:latest
```

`latest` follows the main branch. For predictable updates, use a released version like `:1.0.0` (see
[Updating and backups](/guides/updating/)). The image needs no login to download.

## After installing

1. [Create the first account](/start/first-account/).
2. To use it away from home: [Reverse proxy and HTTPS](/guides/reverse-proxy/).
3. Optional: [Open in your slicer](/guides/slicers/), [the local copy](/guides/local-copy/),
   [install it as an app on your phone](/guides/phones/).
