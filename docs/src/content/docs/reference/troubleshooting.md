---
title: Troubleshooting
description: Solutions for the most common problems with 3MF Library.
---

Start with the log; it says what the site is doing and why something failed:

```bash
docker compose logs --tail 50
```

## The library is empty

- **First scan still running?** Large libraries take a few minutes the first time. The page says "Indexing the
  library…" and reloads by itself.
- **Wrong folder or no access?** The log line `Scan finished … 0 files` means the container sees an empty folder. Check
  `LIBRARY_DIR` (or `NFS_SERVER`/`NFS_EXPORT`), and that `PUID`/`PGID` may read it:
  `docker compose exec 3mf-library ls /library`.
- **"The library scan failed"** on the page, and `/health` answers 503: the log has the reason. Often `./data` isn't
  writable for `PUID`/`PGID` (`sudo chown 1000:1000 data`).

## A new file doesn't show up

The library is checked every `SCAN_INTERVAL` (3 minutes), and a new file waits until it stopped changing for
`SETTLE_DELAY`. With a sync tool in between (OneDrive, Synology Cloud Sync) add its own delay. **Rescan now** in the
footer starts a scan right away.

## A model says "Could not read this file"

- *"The library folder didn't deliver the file"*: a network or permission problem. It's tried again within the hour;
  check the NAS and the permissions.
- Any other message: the file itself is damaged or over a limit (see [Supported files](/reference/files/)). It's read
  again when it changes; open and re-save it in your slicer.

## The 3D view says it can't load

Very large models take a while the first time (they're simplified and then cached). A model over the triangle limit
can't be shown in 3D, but downloads normally. If the container restarts while loading, raise `MEMORY_LIMIT`.

## I can't log in through the proxy

- After 8 wrong attempts, wait a few minutes (the page says "Too many attempts").
- Logins that seem to succeed but bounce back to the login page usually mean the proxy doesn't pass HTTPS on. Set
  `KNOWN_PROXIES` to the proxy's address and make sure it sends `X-Forwarded-Proto`.
- Everyone behind the proxy hits the rate limit at once? Then the site sees every visitor as the proxy: set
  `KNOWN_PROXIES`.

## The setup page says I can't create an account here

The first account can't be made through a proxy. Open `http://<server>:8085` directly, from the home network if
`TRUSTED_NETWORKS` is set. Without it, use the setup code from `docker logs 3mf-library`.

## "Open in …" doesn't open my slicer

- The browser asks permission the first time; allow it.
- Bambu Studio only accepts 3MF files by link, and asks once whether to trust the site: answer **Yes**.
- OrcaSlicer needs a download folder in its preferences.
- PrusaSlicer only works with [the local-copy helper](/guides/local-copy/).
- The button isn't shown on phones and tablets.

## Dates are an hour or two off

Set `TZ` to your time zone, e.g. `TZ=Europe/Amsterdam`, and run `docker compose up -d`.

## Still stuck?

[Open an issue on GitHub](https://github.com/jurjen74/3mf-library/issues) with the log lines and what you expected.
