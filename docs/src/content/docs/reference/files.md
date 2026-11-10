---
title: Supported files
description: What 3MF Library reads from 3MF and STL files, and how other files are shown.
---

## 3MF

3MF files are ZIP archives with the model and extra information from the slicer. 3MF Library reads:

| From | What |
|---|---|
| Bambu Studio, OrcaSlicer | One preview per plate (`Metadata/plate_N.png`), plate names, the objects on each plate, print time and filament weight per plate, filament colours |
| PrusaSlicer and others | The preview image (`Metadata/thumbnail.png` and similar) |
| MakerWorld downloads | Title, designer, licence, description (with its formatting) and the photos (`Auxiliaries/Model Pictures`) |
| The model itself | Size, number of triangles; geometry for the 3D viewer, painted colours included |

A 3MF without any image gets a rendered preview of its geometry. Files from all common slicers work; the richest
pages come from Bambu Studio and OrcaSlicer projects.

## STL

STL files (binary and text) get a rendered preview and the 3D viewer. STLs larger than 1 GB are listed but not
rendered.

## Other files

Images, PDFs, ZIPs and any other files in your folders are listed under **Other files** on the folder page, with a
download link. Images get a small thumbnail. They're also found by search.

## Limits

| | |
|---|---|
| Triangles in a 3MF | up to 30 million |
| Triangles in an STL | up to 25 million |
| 3D viewer | simplified to about 750,000 triangles for the browser; the downloaded file is always the original |

A file that can't be read (damaged, or over a limit) still appears, with a "Could not read" message. It's read again
when it changes. Files that the library folder couldn't deliver (a network or permission problem) are tried again
within the hour.

## What isn't used

The site doesn't slice, convert or change files, and doesn't read G-code. Everything it shows comes from the files
themselves. The only thing it fetches from the internet is the latest release number, once a day, to tell you about
updates; `UPDATE_CHECK=false` turns that off.
