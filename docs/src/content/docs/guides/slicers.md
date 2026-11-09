---
title: Open in your slicer
description: Send a model from the site straight to Bambu Studio, OrcaSlicer, Cura, Creality Print or ElegooSlicer.
---

Every model page has an **Open in …** button. It hands the model to the slicer on the computer you're using, without
saving a download first.

## Pick your slicer

Each computer picks its slicer under **Account → This computer**. The choice is stored in that browser, so your
laptop and your desktop can use different slicers. The default is Bambu Studio.

| Slicer | Opens from the site | Opens the local copy |
|---|---|---|
| Bambu Studio | 3MF only; asks once whether to trust the site, answer **Yes** | yes |
| OrcaSlicer | yes; set a download folder in OrcaSlicer's preferences | yes |
| PrusaSlicer | no: it only downloads from Printables and Thingiverse | yes |
| UltiMaker Cura | yes | yes |
| Creality Print | yes | no |
| ElegooSlicer | yes | no |

"Opens the local copy" needs a small helper on your computer; see [Open the local copy](/guides/local-copy/). When
neither route works for a file (an STL with Bambu Studio, PrusaSlicer without the helper), the button isn't shown.

## How it works

The slicer downloads the file itself, without your login cookie. So the button first asks the site for a **link that
works for 15 minutes and only for this one file**, and passes that link to the slicer through its own link scheme
(`bambustudioopen://`, `orcaslicer://`, `cura://` …). Nothing else of the library is reachable with it.

The first time, your browser asks whether it may open the slicer. Tick "always allow" to skip that next time.

## Phones and tablets

The button isn't shown on touch devices: there's no desktop slicer there, and the phone apps (like Bambu Handy) can't
open 3MF files from a link. Use **Download** instead.

:::note
Only Bambu Studio has been tried with the real app so far. The link formats for the others follow each slicer's source
code (and [Manyfold](https://github.com/manyfold3d/manyfold), which does the same). If one doesn't work for you, please
[open an issue](https://github.com/jurjen74/3mf-library/issues).
:::
