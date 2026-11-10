---
title: Updating and backups
description: Update to a new version, pin versions, and what to back up.
---

## Which version am I running?

The footer of every page shows the version, for example **v1.0.1**; click it for that version's release notes. Once a
day the site asks GitHub for the latest release, and when there's a newer one the footer says so: **1.0.2 available**,
linking to what's new. Builds from the main branch show something like `v1.0.1-3-gabc1234` (three changes after
1.0.1).

The check only fetches the latest release number from GitHub; nothing about your site or library is sent. To turn it
off, set `UPDATE_CHECK=false` in `.env` and run `docker compose up -d`.

## Updating

```bash
cd 3mf-library
docker compose pull
docker compose up -d
docker image prune -f      # optional: remove the old image
```

Updates migrate the data folder by themselves. If a new version reads files differently, it re-reads the library once
after the update; the site stays usable while that happens.

### Pinning a version

`latest` is built from every change on the main branch. To decide yourself when to update, pin a released version in
`.env`:

```bash
IMAGE=ghcr.io/jurjen74/3mf-library:1.0.0
```

Released images are tagged with the full version (`1.0.0`) and the minor version (`1.0`, which moves along with fixes).
Changes are listed on the [GitHub releases](https://github.com/jurjen74/3mf-library/releases) page.

## What's in `data/`

| | What | Back it up? |
|---|---|---|
| `users.db` | Accounts and favourites | **Yes.** It can't be rebuilt. |
| `keys/` | Keys for login cookies and slicer links | Optional; without them everyone simply logs in again |
| `library.db*` | The index of your library | No: rebuilt from the library |
| `cache/` | Previews, rendered images, 3D meshes | No: rebuilt from the library |

A simple backup while the site runs (needs `sqlite3` on the host):

```bash
sqlite3 data/users.db ".backup 'users-backup.db'"
```

Or stop the container and copy the whole `data/` folder.

## Starting the index afresh

The index and previews are a cache. If something looks wrong, rebuild them:

```bash
docker compose down
rm -rf data/library.db* data/cache
docker compose up -d
```

Accounts and favourites are kept.

## Moving to another machine

Copy `.env`, `docker-compose.yml`, `compose.nfs.yml` and `data/users.db` (and `data/keys/` to keep everyone logged in)
to the new machine, make sure it can read the library, and start it. The rest is rebuilt.
