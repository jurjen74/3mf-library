# Code review — 3mf-library

Date: 2026-11-09 · Commit reviewed: `57e46c3` (main, clean tree) · Scope: the whole repository (`src/`, `tests/`,
`tools/`, Dockerfile, compose files). The vendored `wwwroot/lib/three` and the binary assets are not reviewed.

Build and tests: `dotnet test Library.slnx` passes, **155/155** tests green, no warnings (TreatWarningsAsErrors on).

## Summary

The code is in good shape. The security-sensitive parts (trusted network, forwarded headers, setup, cookies, slicer
tokens, path traversal, description sanitising, local-copy helpers) are careful and well reasoned, and I found no
exploitable hole in them. The problems I did find are about **robustness and resources**, mostly in the 3D-viewer mesh
pipeline, plus a few small UI and query bugs.

| # | Severity | Area | Finding |
|---|----------|------|---------|
| 1 | Medium ✅ fixed | Meshes / ops | Mesh builds have no overall concurrency limit and the container has no memory limit, so it can run out of memory |
| 2 | Medium ✅ fixed | Meshes | Some malformed 3MF input throws exception types that `MeshService` doesn't catch, which gives a 500 |
| 3 | Medium ✅ fixed | Indexing | A file that fails to index once (e.g. a brief NFS error) is never retried until the file itself changes |
| 4 | Low | Meshes | `/mesh/{key}/plate_N.bin` accepts any N for any model: duplicate STL meshes, wasted 3MF parses |
| 5 | Low | Front-end | Pressing `"` or `\` throws in the ←/→ keyboard handler |
| 6 | Low | Front-end | The first 3D load can create two viewers (two canvases) |
| 7 | Low | Search | The 3MF/STL filter is applied after the 200-result cut |
| 8 | Low | Search | Folder search is case-insensitive for ASCII letters only |
| 9 | Low | Display | Dates are shown in UTC (the container has no `TZ`) |
| 10 | Low | Indexer | If the first scan throws, the site shows "still indexing" forever and `/health` still says ok |
| 11 | Low | Meshes | Leftover `.tmp-*` mesh files when a write fails; the 3MF parser has no triangle limit while parsing |
| 12 | Info | Various | Small things: shared rate-limit budget, server paths in error text, query costs per request |

---

## Medium

The three medium findings were fixed after the review (not yet committed); each section ends with what changed.

### 1. Mesh builds can exhaust memory

`Meshes/MeshService.cs:38` takes one `SemaphoreSlim` **per target file** (`_gates.GetOrAdd(target, …)`), so different
models are built at the same time with no upper limit. One build can be big:

- `StlReader` allows 25 M triangles (`StlReader.cs:11`) and welds vertices through a `Dictionary<Vector3,int>`. For a
  large STL that is several GB of managed memory.
- `ThreeMfMeshReader` allows 30 M triangles (`ThreeMfMeshReader.cs:15`).
- The indexer may be rendering an STL preview at the same time (`LibraryScanner.ReadStl`).

`docker-compose.yml` sets no `mem_limit`, so if a few people open large models' 3D views together (or one person opens
several tabs), the whole VM can run short of memory, not just the container.

Also, `Task.Run(..., CancellationToken.None)` (`MeshService.cs:47`) is deliberate (the result is cached), but it means
closing the tab doesn't stop a build.

**Suggested fix:** add one global `SemaphoreSlim(1)` (or 2) around `Build`, used together with the per-file gate. Add
`mem_limit` (and possibly `DOTNET_GCHeapHardLimit`) in `docker-compose.yml`, so an out-of-memory error stays inside
the container. Optionally use a lower STL limit for the viewer than for the indexer.

**Fixed:** new `Meshes/MeshWorkGate.cs` (a singleton) lets one full mesh load run at a time, shared by
`MeshService.GetAsync` and the indexer's STL and geometry previews (`LibraryScanner.ReadStl` / `TryRenderGeometry`).
A viewer request that is cancelled while waiting gives up its turn. `docker-compose.yml` now sets
`mem_limit: ${MEMORY_LIMIT:-3g}` (override in `.env`); .NET sizes its heap to 75% of the container limit by itself.

### 2. Uncaught exception types in the mesh path give a 500

`MeshService.Build` only catches `InvalidDataException`, `IOException`, `UnauthorizedAccessException` and `XmlException`
(`MeshService.cs:91`). Malformed (or unusual) 3MF input can throw other types:

- **Negative `instance_id`** in `model_settings.config`: `i.InstanceId < list.Count` passes for −1, then `list[-1]`
  throws `ArgumentOutOfRangeException` (`ThreeMfMeshReader.cs:64`).
- **Non-numeric transform values**: `float.Parse` in `Transform(...)` throws `FormatException`
  (`ThreeMfMeshReader.cs:350`). The vertex and triangle readers use `TryParse` and throw `InvalidDataException`;
  the transform reader doesn't.
- **Non-string `filament_colour` entry** (e.g. `null` or a number): `colour.GetString()` in `ReadPalette` throws
  `InvalidOperationException`, which the `catch (JsonException)` around it doesn't catch
  (`ThreeMfMeshReader.cs:479`, `:488`). `ThreeMfReader.ReadFilamentColours` checks `ValueKind == String` first; `ReadPalette` doesn't.

The result is the error page / a 500 instead of the 422 with a message that the viewer shows. During indexing,
`TryRenderGeometry` catches everything, so only the viewer is affected.

**Suggested fix:** check `InstanceId >= 0`, use `float.TryParse` and throw `InvalidDataException` in `Transform`,
check `ValueKind` in `ReadPalette`. As a fallback, also catch `FormatException`, `ArgumentException` and
`InvalidOperationException` in `Build` (or catch all except `OutOfMemoryException`). One test per case would fit in
`MeshTests`.

**Fixed:** `instance_id < 0` is skipped, `Transform` uses `TryParse` (and rejects non-finite values) with an
`InvalidDataException`, `ReadPalette` skips non-string entries. `MeshService.Build` now also catches any other
exception (logged as an error, the viewer gets "Could not read the geometry of this file.") and deletes its
`.tmp-*` file in a `finally` (this also covers the first point of finding 11). Tests:
`Skips_plate_instances_with_a_negative_index`, `Rejects_a_transform_that_is_not_numbers_as_invalid_data`,
`Ignores_filament_colours_that_are_not_strings`.

### 3. Index errors are never retried

`LibraryScanner.ScanOnce` only re-reads a file when its size, mtime or `IndexerVersion` changed
(`LibraryScanner.cs:72-77`). If reading fails, `files.error` is stored with the current size and mtime, so the file
counts as done. The comment at line 228 says it "gets another try when it changes", and that is the only way.

That's fine for a truly broken file. But the library is an NFS mount with `soft,timeo=100` (`docker-compose.yml`),
and soft mounts return `EIO` on timeouts. Synology Cloud Sync can also leave a file the same size for longer than the
10 s settle delay. Either case leaves a good file showing "Could not read this file" until someone edits it in OneDrive
or the indexer version is bumped.

**Suggested fix:** also treat `error IS NOT NULL AND indexed_at < now - X` as changed (for example, retry errored files
once an hour, or with back-off), or retry `IOException` failures on the next scan only. `ExistingFile` would need
`error` and `indexed_at`.

**Fixed:** a file whose last read failed is read again once `LibraryScanner.RetryFailedAfter` (1 hour) has passed,
even if it hasn't changed; a file that reads fine is never re-read for this reason. The scanner takes an optional
`TimeProvider` for the test `Retries_a_file_that_failed_to_read_after_an_hour_even_if_unchanged`, which simulates a
temporary read error with file permissions.

---

## Low

### 4. `/mesh` accepts any plate name for any model

`MeshService.IsValidName` allows `model` and `plate_0`…`plate_999` (`MeshService.cs:98`), and nothing checks the name
against the file:

- For an **STL**, the name is ignored (`MeshService.cs:66-68`), so `plate_1` … `plate_999` each build and cache
  another copy of the same mesh.
- For a **3MF**, a plate number that doesn't exist still opens the ZIP and parses `model_settings.config` before it
  returns an empty mesh, and that empty mesh is cached as a file too.

Only logged-in users can reach this, so it's waste rather than an attack. Still, it's cheap to fix: accept only
`model` for STLs, and for 3MFs check the plate index against `queries.Plates(file.Id)` (or against
`file.PlateCount`).

### 5. Keyboard handler throws on `"` and `\`

`wwwroot/js/site.js:425` builds a selector from the key: ``document.querySelector(`a[data-key="${event.key}"]`)``. The
keys `"` and `\` make an invalid selector, and `querySelector` throws a `SyntaxError` on every such key press outside
an input. It does no harm, but it fills the console with errors.

**Suggested fix:** only look up the keys you use: `if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;`
(or use `CSS.escape(event.key)`).

### 6. First 3D load can create two viewers

`site.js:138-141`: while `await import("/js/viewer.js")` is pending, `viewer` is still `null`. If the user picks
another plate during that time (`setMesh` → `load()`), the second call also passes `if (!viewer)` and creates a second
`Viewer`. The result is two WebGL canvases in `#stage-3d`, and the first one is never disposed.

**Suggested fix:** store the promise instead of the instance:
`viewerPromise ??= import(...).then(({ Viewer }) => new Viewer(container));`.

### 7. Type filter applied after truncation

`Pages/Search.cshtml.cs:33-36` fetches 201 rows, keeps the first 200 and **then** applies `?type=stl`. With a broad
query, STL matches ranked below position 200 disappear, even though the filtered list would be short. The counts on
the type buttons have the same problem.

**Suggested fix:** pass the kind to `LibraryQueries.Search` (`AND f.kind = @kind`) when a filter is on.

### 8. Folder search: ASCII-only case folding

`LibraryQueries.SearchFolders` uses `instr(lower(f.name), lower(@term))` (`LibraryQueries.cs:153`). SQLite's `lower()`
only folds ASCII letters unless it's built with ICU, so "über" doesn't find a folder named "Über". File search uses
FTS5 `unicode61`, which handles this correctly.

**Suggested fix:** filter folders in C# (there are few of them) with `CompareInfo.IndexOf(..., IgnoreCase |
IgnoreNonSpace)`, or register a custom SQLite function.

### 9. Dates shown in UTC

`Display.Date` calls `ToLocalTime()` (`Display.cs:43`), but neither the Dockerfile nor `docker-compose.yml` sets `TZ`,
and the `aspnet` image has no zone data configured. So "Modified" on the file page and "Added" on the Users page are in
UTC: a file saved at 00:30 Dutch time shows the previous day.

**Suggested fix:** `TZ: "Europe/Amsterdam"` in `docker-compose.yml` (.NET reads `TZ`; the image includes ICU/tzdata),
or format with an explicit `TimeZoneInfo`.

### 10. A failing first scan looks like "still indexing" forever

`IndexerService` only sets `LastFinished` on success (`IndexerService.cs:38`). If the first scan throws (DB locked,
cache folder not writable, …), `BrowseModel` keeps seeing `StillIndexing = true` (`Browse.cshtml.cs:55`), the page
reloads every few seconds, and `/health` still returns `status: "ok"`.

**Suggested fix:** keep a `LastError` / `LastFailed` in `IndexerStatus`, show it on the empty Browse page and in the
footer, and let `/health` report it (`503` is useful if a monitor ever watches it).

### 11. Small resource leaks in the mesh code

- `MeshService.Build`: if `MeshCodec.WriteCompressed` or `File.Move` throws (disk full, cache folder removed by a
  rescan), the `mesh_*.bin.gz.tmp-<guid>` file stays behind until the cache folder is deleted. A `try/finally` that
  deletes the temporary file fixes it.
- `ThreeMfMeshReader.ParseModel` reads every vertex and triangle of a model file into lists before `Emit` checks
  `MaxTriangles`. A single object far over the limit is fully loaded first. A running count in `ParseModel` would stop
  earlier.
- `_gates` in `MeshService` is never trimmed. That's harmless at this size (one small object per mesh ever requested);
  just noting it.

---

## Info / nice to have

- **Shared rate-limit budget.** `Account` uses the `login` policy (`Account.cshtml.cs:12`), so password-change attempts
  and logins share the same 8 per 5 minutes per IP. Behind the proxy, a household or mobile carrier shares one public
  IP, so a few mistyped current passwords can block someone else's login for 5 minutes. A separate `account` policy
  is cleaner.
- **Server paths in error text.** `IOException` messages (e.g. "Could not find file '/library/…'") end up in
  `files.error` (shown on cards and the file page) and in the `/mesh` problem detail. They're only visible to logged-in
  users and only show container paths, but a generic message plus the details in the log would be tidier.
- **Query cost per page.** `Browse` runs `Folders()` (one correlated `COUNT` per folder) and one `CoversUnder` per
  subfolder, and `CoversUnder`/`FilesUnder`/`FolderTotals` use `substr(folder, …)`, which can't use an index. At the
  current library size that's fine (milliseconds). If it ever gets slow: a `folder_path_prefix` column or a
  `folder >= @f || '/' AND folder < @f || '0'` range condition can use `ix_files_folder`.
- **Per-request user lookup.** `Session.ValidateAsync` opens `users.db` on every request, including image and mesh
  requests. That's the intended trade-off (immediate logout/admin changes) and cheap with SQLite pooling; just keep it
  in mind if a page ever loads hundreds of images.
- **`/dl` tokens are bearer links for 15 minutes**, reusable by anyone who has the URL (documented and intended,
  because the slicer has no cookie). `SlicerLinks.Create` builds the host from the request's `Host` header. With NPM in
  front that's correct; it only means the link follows whatever host the user called the site by.
- **Mac helper decoding.** `decode()` in `tools/mac-opener/open-model.sh:16` uses `printf '%b'`, which also interprets
  backslash escapes that were already in the URL (`\x2e`, `\c`). All checks run on the decoded result, so this isn't
  exploitable. A stricter decoder (only `%XX`, reject everything else) would make the script easier to reason about.
- **No `HEALTHCHECK`** in the Dockerfile/compose; `/health` exists and could be wired up.

---

## Things done well

- **Trust model.** `TrustedNetwork.RecordConnection` runs before `UseForwardedHeaders`. Any forwarding header, or the
  NPM address itself, disqualifies a request, and `UseForwardedHeaders` only runs when `KNOWN_PROXIES` is set
  (`Program.cs:98-103`). Bad network settings stop startup instead of silently opening access.
- **First-run setup** is atomic (`INSERT … WHERE NOT EXISTS`), allowed only for direct non-proxied visitors, and
  rate-limited.
- **Sessions.** Security stamp in the cookie, admin rights read from the DB on every request, last-admin protection
  inside the SQL statement itself, constant-time-ish login with a dummy hash for unknown users, `LocalRedirect` +
  `IsLocalUrl` for return URLs.
- **Paths.** `LibraryPaths` normalises and then checks containment after `GetFullPath`. Downloads, ZIPs and slicer
  links only serve files that are in the index. The cache endpoints validate keys and names with strict regexes.
- **Untrusted 3MF content.** XML readers have DTDs off and no resolver; there are entry-count, entry-size and
  expansion limits; descriptions go through HtmlSanitizer with a tight allow-list, and `<script>`/`<style>` contents
  are removed first, so `KeepChildNodes` can't leak them as text. A strict CSP with no inline scripts backs this up.
- **Indexer safety.** An offline mount or a partial walk never removes anything; previews are staged and moved in
  atomically; cache keys make URLs immutable; orphaned cache folders are cleaned only after complete scans.
- **Local-copy helpers** validate the decoded path, the extension and the slicer, and re-check containment after
  resolving symlinks / `GetFullPath`, on both macOS and Windows.
- **Tests**: 155 tests covering auth flows (proxy vs LAN, setup, forced password change, antiforgery), path traversal
  with encoded paths, 3MF/STL parsing, meshes and the scanner, all with synthetic fixtures.

## Test gaps worth filling

- An unknown plate number on `/mesh` (finding 4). (Findings 2 and 3 now have tests.)
- Search with `?type=` on more than 200 results (finding 7).
- Non-ASCII folder search (finding 8).
