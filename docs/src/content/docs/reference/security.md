---
title: Security
description: "How 3MF Library protects your library: read-only access, logins, proxies, slicer links and the container."
---

3MF Library is made to run at home and be reachable from outside through a reverse proxy. These are the rules it
follows.

## Your files

- The library is mounted **read-only**. The site has no way to change, move or delete your files.
- Only files that are in the index can be downloaded; paths are checked so nothing outside the library is reachable.
- Descriptions from 3MF files are cleaned of scripts and styling before they're shown; the site's strict content
  security policy allows no inline scripts at all.

## Logins

- Everything needs a login, except the login and setup pages, the site's own styling and scripts, `/health`, and
  slicer links (which carry their own token).
- **Home network:** a visitor that reaches the site **directly** from `TRUSTED_NETWORKS` is let in without a login.
  Any request that comes through a proxy (or carries forwarding headers, or comes from a `KNOWN_PROXIES` address)
  never counts as home network, whatever it claims.
- **First account:** only while there are no accounts, never through a proxy, and either from the home network or
  with the one-time setup code from the container log.
- Passwords are stored as salted hashes (ASP.NET Core Identity's hasher). Admin rights and browse-only are checked in
  the database on every request, so a change works immediately.
- **Browse-only accounts** get no file downloads, folder ZIPs, slicer links or 3D meshes from the server (images
  in the library excepted, since folder pages show them). A slicer link handed out before the change stays valid for
  its 15 minutes.
- Changing or resetting a password ends that account's other sessions.
- Logins, setup and password changes are rate-limited: 8 attempts per 5 minutes per address (logins) or per account
  (password changes).

## Proxies

`X-Forwarded-For` and `X-Forwarded-Proto` are only believed from addresses in `KNOWN_PROXIES`. Without it they're
ignored, so a visitor can't fake their address to dodge the rate limit or look like the home network.

## Slicer links

"Open in your slicer" hands the slicer a link to **one file**, valid for **15 minutes**, signed with the site's key.
Anyone with that link can download that file during those minutes (that's how the slicer gets it without your login);
it gives access to nothing else.

## Previews and caching

Previews, photos and 3D meshes are sent with `Cache-Control: private`, so a shared proxy cache never keeps them. Turn
off asset caching in your proxy anyway.

## The container

The image runs as an unprivileged user with a read-only root file system, no Linux capabilities and
`no-new-privileges`, and only `./data` is writable. A memory cap keeps one huge model from affecting the rest of the
machine.

## Outgoing connections

The site makes one connection to the internet: once a day it fetches the latest release from GitHub's API
(`api.github.com`) to show "x.y.z available" in the footer. It sends nothing but the request itself; set
`UPDATE_CHECK=false` to turn it off.

## Reporting a problem

Found a security issue? Please report it privately through
[GitHub's security advisories](https://github.com/jurjen74/3mf-library/security/advisories/new) instead of a public
issue.
