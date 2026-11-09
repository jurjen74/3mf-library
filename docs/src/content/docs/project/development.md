---
title: Development
description: Build, test and run 3MF Library and this documentation site from source.
---

The site is an ASP.NET Core (.NET 10) app with Razor Pages and SQLite; the 3D viewer uses three.js. The source is on
[GitHub](https://github.com/jurjen74/3mf-library).

## Run it locally

You need the .NET 10 SDK.

```bash
git clone https://github.com/jurjen74/3mf-library.git
cd 3mf-library
mkdir dev-library            # put some 3MF/STL files in here
dotnet run --project src/Library.Web --launch-profile http
```

Open `http://localhost:5180`. Locally, `localhost` counts as the home network, so there's no login. The index and
previews go to `data/`; both `dev-library/` and `data/` are git-ignored.

## Tests

```bash
dotnet test Library.slnx
```

The tests build their own small 3MF and STL files in code; no real models are needed (or allowed) in the repository.

## Container images

GitHub Actions runs the tests on every push and pull request, and publishes images for amd64 and arm64 to
`ghcr.io/jurjen74/3mf-library` from the main branch (`latest`) and from version tags (`v1.2.3` → `1.2.3` and `1.2`).

To build one yourself (Docker or Podman; the .NET SDK cross-compiles, so another architecture needs no emulation):

```bash
docker build --platform linux/amd64 -t 3mf-library:local .
```

Then set `IMAGE=3mf-library:local` and `PULL_POLICY=never` in `.env`.

## This documentation

The documentation is an [Astro Starlight](https://starlight.astro.build) site in `docs/`, published to
[www.3mf-library.com](https://www.3mf-library.com) by GitHub Actions on every change. You need Node.js 22 or newer.

```bash
cd docs
npm ci
npm run dev        # http://localhost:4321
```

The screenshots come from a script, so they can be made again after a change to the site. Run an instance with a demo
library (never a real one: the screenshots are published) on port 5183, with `localhost` as trusted network, and
optionally an empty one without trusted networks on 5184 for the setup page:

```bash
npx playwright install chromium     # once
SETUP_URL=http://localhost:5184 npm run screenshots
```
