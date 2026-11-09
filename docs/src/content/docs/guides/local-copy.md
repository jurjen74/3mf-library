---
title: Open the local copy
description: Let the slicer open the synced original on your own computer, so saving updates the file in the library.
---

If your computer has the same library synced (OneDrive, Dropbox, Syncthing, a mounted share), the **Open in** button
can open **that file** instead of a download. When you save in the slicer, you update the original, and the sync tool
brings the change back to the library.

This needs a small helper on the computer, installed once. It registers `threemflib://` links and opens them in your
slicer. Any web page could fire such a link, so the helper only opens model files (`.3mf`, `.stl`, `.obj`, `.step`)
that are **inside the folder you give it**, and refuses anything else.

Supported slicers: Bambu Studio, OrcaSlicer, PrusaSlicer and UltiMaker Cura.

## Mac

From a copy of the repository, run the build script with your local library folder:

```bash
tools/mac-opener/build.sh "$HOME/Library/CloudStorage/OneDrive-Personal/3D Print"
```

This builds **3MF Library Opener.app** in `~/Applications` and registers it. It opens files in the slicer you picked on
the site (found by its app name). Uninstall by deleting the app.

## Windows

From a copy of the repository, in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File tools\windows-opener\install.ps1 -LibraryFolder "D:\3D Print"
```

Without `-LibraryFolder` it looks for `3D Print` in your OneDrive folder. It installs to
`%LOCALAPPDATA%\3MF Library Opener` and registers the links for your user only (no admin rights). It finds installed
slicers by itself; otherwise pass `-BambuStudio`, `-OrcaSlicer`, `-PrusaSlicer` or `-Cura` with the path to the
program. Run it again after installing another slicer. Uninstall with `uninstall.ps1`.

## Turn it on

On that computer, open **Account → This computer**, pick your slicer and tick **Open models from my local copy**. The
setting is per browser.

The paths have to match: a model at `Workshop/Gridfinity/Box.3mf` on the site must be at
`<your folder>/Workshop/Gridfinity/Box.3mf` on the computer. If the file isn't there (yet), the helper says so.

:::tip
Online-only files (OneDrive's "Files On-Demand") are fine: opening one downloads it first.
:::
