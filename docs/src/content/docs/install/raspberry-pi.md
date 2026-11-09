---
title: Raspberry Pi
description: Run 3MF Library on a Raspberry Pi 4 or 5 with a 64-bit OS.
---

A Raspberry Pi 4 or 5 runs the site well. The image is built for arm64, so the Pi needs a **64-bit** operating
system.

## What you need

- Raspberry Pi 4 or 5, **2 GB of memory or more** (4 GB is comfortable for large models in the 3D viewer).
- **Raspberry Pi OS Lite (64-bit)**, installed with Raspberry Pi Imager. In the Imager's settings, set a host name,
  your user and enable SSH.
- A USB SSD or a NAS share for the models. A microSD card works for the system, but is slow for a big library and
  for the preview cache.

Check that the system is 64-bit:

```bash
uname -m        # must say aarch64
```

## Install

1. **Docker:** follow Docker's guide for Debian (Raspberry Pi OS is based on Debian):
   [docs.docker.com/engine/install/debian](https://docs.docker.com/engine/install/debian/). Then
   `sudo usermod -aG docker $USER` and log in again.
2. **Models:** mount the USB drive (for example at `/mnt/models`) or use an [NFS share](/guides/nas/).
3. **The site:** follow [Docker Compose on Linux](/install/linux/) from step 2. The default user `1000:1000` is the
   first user on Raspberry Pi OS, so `PUID`/`PGID` usually stay as they are.

## Memory

On a Pi with 2 GB, lower the cap so a huge model can't starve the system:

```bash
# .env
MEMORY_LIMIT=1200m
```

Models that need more than that can't be opened in the 3D viewer, but still show their previews and download
normally.

## Speed

The first scan reads every file once and renders previews for STLs, which takes a while on a Pi: a few minutes per
few hundred files. After that only new and changed files are read, and browsing is fast.
