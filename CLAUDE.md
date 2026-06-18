# OfflineMapsTest

## What this is

A WinUI 3 desktop app (.NET 8, Windows App SDK 2.1.3, packaged MSIX, x64) that hosts
MapLibre GL JS inside a single WebView2 to show fully-offline vector basemaps (PMTiles),
and overlays live weather data: SPC severe/fire-weather outlooks and **real NEXRAD
Level II radar** (raw base data decoded and GPU-rendered in the WebView). Think a
lightweight, offline-capable weather map.

The Visual Studio project lives in the `OfflineMapsTest/` subfolder; this file is at the
repo root.

## Architecture

MVVM with interface seams, hand-wired in the `MainWindow` constructor (no DI container).

- **`MainWindow` is the ONLY class that touches WebView2.** It implements `IMapView`,
  whose single method `RunScriptAsync(js)` runs JS against the map via
  `ExecuteScriptAsync`.
- **`IMapService` / `MapService`** build JS command strings and dispatch them through
  `IMapView`. A `Call(fn, args...)` helper formats `window.fn(...)` calls (invariant
  culture). Methods: `ApplyStyleAsync`, `ShowOutlookAsync` / `ClearOutlookAsync` /
  `SetOutlookOpacityAsync`, radar loop (`BeginRadarLoopAsync` / `AddRadarFrameAsync` /
  `ShowRadarFrameAsync` / `ClearRadarAsync` / `SetRadarOpacityAsync`) + markers
  (`ShowRadarSitesAsync` / `SetSelectedRadarSiteAsync`), `FlyToAsync` (now unused).
- **`MapViewModel`** owns: selected basemap style, ribbon collapsed state, SPC
  day/product selection + outlook opacity, radar site selection + the animation loop
  (frame index, playback timer, ~5-min reload) + radar opacity, and the main map region
  (CONUS).
- **Providers / services:** `StyleProvider` (basemaps), `RegionProvider` (only CONUS
  used now), `ISpcOutlookService` / `SpcOutlookService` (+ editable `SpcOutlookCatalog`
  endpoint table), `IRadarSiteProvider` / `RadarSiteProvider` (full ~160-site WSR-88D
  network, loaded from the bundled `Assets/radar-sites.json` data file — see "Radar sites
  data" below),
  `ILevel2RadarService` / `Level2RadarService` (fetch/cache of Level II volumes from the
  archive bucket, plus `GetLiveFrameAsync` for the near-real-time chunks bucket — see
  "Radar live frame" below).
- **`Assets/Map/map.js`** is ONE parameterized page loaded by `map.html` in the single
  WebView. URL params: `key`, `interactive`, `style`, `lng`, `lat`, `zoom`. It exposes
  `window.applyStyle`, `flyTo`, `showOutlook` / `clearOutlook` / `setOutlookOpacity`,
  `radarBeginLoop` / `radarAddFrame` / `radarShowFrame` / `clearLevel2Radar` /
  `setRadarOpacity`, `showRadarSites` / `setSelectedRadarSite`, and posts
  `{type:"mapReady"}` (and `radarSiteClick` / `radarFrameReady`) on the message channel.
  MapLibre is **v5.21.1**.
- **Radar JS (3 files under `Assets/Map`):** `radar.js` owns the **MapLibre WebGL custom
  layer** (dBZ ramp) + host shims (`window.RadarLayer`); `map.js` wires the shims and
  re-adds the layer after a style switch. The heavy work runs **off the UI thread** in
  `radar-worker.js`, which calls the shared `radar-decode.js` to bzip2-decode the `.V06`
  (vendored `nexrad-level-2-data` + `seek-bzip`) and build the lowest-tilt reflectivity
  gate geometry, then transfers the typed arrays back zero-copy (decode is ~5 s of pure-JS
  bzip2, so it must not block the UI). A main-thread fallback uses the same
  `radar-decode.js` if a Worker can't be created. The decoder is vendored offline (jsDelivr
  `+esm`, no Node/build step) — see "Vendoring the decoder" below.
- **WebView2 virtual hosts** (all `Allow`): `mapassets` → `Assets/Map`; `mapdata` → a
  hardcoded Desktop folder holding the PMTiles file
  (`C:\Users\jhamm\OneDrive\Desktop` — a known hack); `spcoutlooks` → SPC GeoJSON
  cache; `radarlevel2` → cached Level II `.V06` volumes (archive frames + the `_live_` chunks frame).

## What's built

- **Bottom control ribbon** (themed bar): `Radar Opacity | Day | Outlook | Opacity |
  Basemap | Hide/Show Sites | collapse`, with a radar **loop bar** (play/pause + scrubber
  + timestamp) shown above it while a loop is active, and an **issued/valid readout** line
  (above the controls) while an outlook with known times is shown. The **Day combo is
  labeled by date** ("Day 1 · Sat Jun 14"). **Radar sites are picked by clicking
  on-map markers** (RadarScope-style), not a dropdown. The **Hide/Show Sites** button
  toggles all site markers (`setRadarSitesVisible`); it's independent of the radar layer —
  hiding the markers leaves an active loop rendering. Collapsing hides the ribbon and shows
  a small "Controls" reveal handle.
- App starts **maximized**; basemap defaults to **Data Viz Black**; outlook opacity
  defaults to **0.05**; radar defaults to **None** at **0.85** opacity.
- **SPC outlooks:** `SpcOutlookService` fetches/caches all 23 SPC products (convective
  categorical d1–3, torn/wind/hail d1–2, d3 combined prob, extended prob d4–8, fire
  d1–8) as GeoJSON via `HttpClient` (conditional GETs, last-known-good on failure,
  per-product isolation, results returned not thrown). URLs live in the editable
  `SpcOutlookCatalog`; convective come from SPC `.lyr.geojson` "cake layers", fire from
  the NOAA ArcGIS `SPC_firewx` MapServer. A background refresh fires on launch **and then every
  `OutlookRefreshInterval` (15 min) via a `PeriodicTimer`** in `MainWindow.RefreshOutlooksInBackgroundAsync`
  (conditional GETs keep periodic cycles cheap — mostly 304s); when it finishes, `MainWindow`
  re-applies the current outlook on the UI thread (`MapViewModel.OnOutlooksRefreshed`) so a
  first-run (empty-cache) overlay appears and the issued/valid readout picks up the freshly-written
  times. The re-apply happens **on launch and on any cycle that actually pulled new data** (≥1
  product `Updated`), not on all-304 cycles, so a long session doesn't needlessly re-render the
  overlay every 15 min. UI: dependent Day + Product
  comboboxes (cascade, with a "None" option) and an Opacity slider. The **Day combo is
  labeled by date** ("Day 1 · Sat Jun 14") — `MapViewModel.BuildDayOptions` computes Day N
  = today + N-1 (local). A small **issued/valid readout** above the controls shows the
  authoritative times parsed from the loaded product's cached GeoJSON
  (`ISpcOutlookService.GetTimesForProduct` → `SpcOutlookTimes`): convective carries
  `ISSUE_ISO`/`VALID_ISO`/`EXPIRE_ISO`, fire-weather only lowercase `valid`/`expire`
  (`yyyyMMddHHmm`, no issue). It reads from disk so it stays correct even when the cache is
  a refresh behind; empty outlooks (0 features) show no times. Convective polygons render
  in their embedded SPC colors; "significant" / **Conditional Intensity Group** areas
  (separate polygons, `LABEL` = `CIG1`/`CIG2`/`CIG3` — tornado & wind go to 3, hail to 2;
  legacy single-significant `SIGN` still handled) render as **hatching** via runtime canvas
  `fill-pattern` images, **one pattern per group matching SPC's intensity legend**: CIG1 = a
  diagonal row of **dashes**, CIG2 = solid diagonal **lines**, CIG3 = solid **cross-hatch**
  (`sig-hatch-1/2/3`, built by `makeHatchImage(tile, width, cross, dash)` in `map.js`), drawn
  on three constant-`fill-pattern` layers filtered by `LABEL` (`spc-outlook-sig1/2/3`). The
  groups **nest** (CIG3 ⊂ CIG2 ⊂ CIG1, each ~tracking a probability contour), so rendered
  as-is the lower hatch shows through the higher one's gaps — so `map.js` **fetches the GeoJSON
  itself, clips each lower group to exclude the next-higher one** (`clipSigFeatures` punches the
  higher polygon in as a winding-corrected hole; valid because they're strictly nested — no
  clipping library), then renders from that in-memory object (`outlookData`, reused on basemap
  re-add). The clip + the per-group patterns live entirely in `map.js`; the cached GeoJSON and
  the C# time-parsing are untouched. (History: this was first mis-rendered as one shared hatch
  for all CIG levels — the actual per-group encoding lives in `LABEL`/`DN`=2·group, verified
  against cached GeoJSON.) Outlook sits below the basemap labels.
- **SPC outlook info card (top-right):** shown while an outlook (not "None") is selected; same
  theme brushes as the ribbon/radar card. Header (`Day N · Type`) + **Issued/Effective** (from the
  cached GeoJSON times) + the **SPC forecast-discussion text** in a scrollable monospace block.
  The discussion is scraped from SPC's HTML page's `<pre>` block by
  `ISpcOutlookService.GetNarrativeAsync` (`SpcOutlookService`: `NarrativeUrlFor` maps day→page —
  `day{1,2,3}otlk.html` and `exper/day4-8/`; **fire-weather pages use a different layout, deferred**),
  `ExtractPreText` strips tags + decodes entities, cached one file per day group
  (`narrative-day{N}.txt`, shared by that day's hazard sub-products) with last-known-good fallback.
  `MapViewModel.UpdateOutlookCard` sets the title/times and lazily fires `RefreshOutlookNarrativeAsync`
  (guards against a stale fetch when the selection changes; silent re-fetch on a same-product refresh,
  "Loading…" only on a product switch). Minor v1 cruft: ~2 footer lines (a "CLICK TO GET … PRODUCT"
  link + "NOTE:" line) trail the forecaster signature — easy to trim later.
- **Radar (NEXRAD Level II):** `Level2RadarService` lists the recent volumes for a site
  from the public AWS **archive** bucket `unidata-nexrad-level2` (keys are
  date-chronological under `<y>/<m>/<d>/<SITE>/`, so take the last N **ending in `_V06`**;
  a small `_MDM` metadata sidecar sorts after the volume for the same timestamp — don't grab
  it), then in C# (`SharpCompress`, off-thread)
  **decompresses only the lowest-elevation records** — it walks the LDM records, stops at
  the first elevation 2 record, and caches a ~7 MB single-tilt buffer instead of the full
  ~86 MB / ~14-tilt volume — serving it via the `radarlevel2` host. (Records align to
  elevation boundaries, scanned lowest-first; elevation is read as the byte 22 after the
  ICAO at the start of each Message 31 block — `ElevationOf` clamps it to a plausible 1..32,
  and the walk only treats elevation ≥ 2 as "past the lowest tilt" *after* it has seen a real
  elevation-1 radial: the leading metadata record can contain the ICAO string at an offset
  whose byte-22 reads as a bogus elevation, which otherwise aborted extraction → `?? raw`
  cached the whole compressed volume → the JS bzip2-decoded all 14 tilts every frame, the
  site-specific ~7 s/frame "empty sites load slow" bug.) The
  WebView decodes it **off the UI thread** (`radar-worker.js` → `radar-decode.js`) and
  GPU-renders the lowest-tilt reflectivity via a WebGL custom layer in `radar.js`. UI: a
  **Radar Opacity** slider, plus the full ~160-site WSR-88D network shown as **clickable
  on-map markers** (`showRadarSites`; click to select, click the active one to clear —
  `radarSiteClick`), with a **Hide/Show Sites** toggle (`setRadarSitesVisible`) to declutter
  without touching an active loop. **Offline sites** (no recent data in *this* feed, e.g. a
  site absent from the bucket like KLIX) render in a muted dark/red "down" style
  (`setRadarSitesStatus`): `MapViewModel.RunSiteStatusLoopAsync` polls
  `ILevel2RadarService.GetLiveSiteIdsAsync` (one delimited date-prefix listing of the archive
  bucket = every site with data today/yesterday; ~1-2 requests) every ~10 min and flags our
  sites not in that set — so a feed outage reads as "offline", not a broken app.
  Clicking a site loads a **~10-frame loop** (last ~hour) at the current view (no recenter): the newest frame shows
  immediately, older frames backfill off-thread, cached one file per volume timestamp. On
  top of those archive frames an extra **near-real-time "live" frame** (from the chunks
  bucket — see "Radar live frame") is appended as the newest when available. UI
  adds **play/pause + a frame scrubber + a timestamp**; the archive loop reloads when a new
  volume appears (~5 min) and the live frame refreshes on its own ~30 s poll. Renders
  beneath the SPC outlook and basemap labels (dBZ ramp in
  `radar-decode.js`, `MIN_DBZ` in `radar.js`). Loop state + playback live in `MapViewModel`;
  `radar.js` stores the decoded frames and renders the current one (uploading on switch; a
  re-decoded current frame now invalidates `uploadedFrame` so live in-place updates repaint).

- **Build needs an explicit platform:**
  `dotnet build OfflineMapsTest/OfflineMapsTest.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64`.
  A plain AnyCPU build fails on MSIX packaging.
- **It's a packaged app:** launch via Visual Studio F5 (deploys the package). The loose
  exe throws (no package identity). Caches live under
  `%LocalAppData%\Packages\<pfn>\LocalCache\Local\OfflineMapsTest\{SpcOutlooks,RadarLevel2}`.
- **Construct the ViewModel BEFORE `InitializeComponent()`** in the `MainWindow` ctor —
  `Maximize()` can make x:Bind evaluate early; if `ViewModel` is null then, all bindings
  silently break (empty combos, dead buttons).
- **WinUI 3 `Window` has no `Window.Resources`** (use `Grid.Resources`). x:Bind on a
  `Window` can't use `{StaticResource converter}`; use x:Bind functions (see
  `VisibleWhen`).
- **WebGL lessons (still relevant):** anything sampled into WebGL must be same-origin or
  it taints the context — `radar.js` sidesteps this by fetching the cached `.V06` from the
  `radarlevel2` host (same-origin) and drawing its own geometry; and do NOT set
  `raster-resampling: 'nearest'` on a transparent raster layer (the removed PNG overlay's
  trap — under WebView2's ANGLE/WebGL it renders as an opaque BLACK square).
- **Radar decode quirks (learned at first run):** this `nexrad-level-2-data` build returns
  `moment_data` already in **dBZ** (floats; `null` = no data — do NOT re-apply
  `scale`/`offset`), and `first_gate`/`gate_size` in **kilometres** (×1000 for metres). The
  decoder's input must be the decoder's OWN `Buffer` (re-exported from the bundle), not a
  foreign `Buffer`/`Uint8Array`. `MIN_DBZ` (in `radar.js`, passed to the decode) thresholds
  out clear-air/ground clutter near the site.
- **MapLibre v5 custom-layer `render(gl, args)`:** the 2nd arg is an options object, not a
  matrix — use `args.defaultProjectionData.mainMatrix`. If `render` *throws* (e.g. passing
  that object straight to `uniformMatrix4fv`) it aborts the frame mid-stack and blanks every
  layer drawn after the custom one — which first looked like GL-state corruption from a
  below-stack layer. Keep `render()` in a try/catch. With that, the radar safely sits beneath
  the outlook/labels via `beforeId` (MapLibre's post-render `setDirty()`/`setBaseState()`
  resets its state cache).
- **Dark launch (no white flash):** four things default to white and were set dark — the
  MSIX splash (`Package.appxmanifest` `<uap:SplashScreen BackgroundColor>`), the root
  `Grid.Background`, the WebView2 (`webView.DefaultBackgroundColor = Microsoft.UI.Colors.Black`
  before `EnsureCoreWebView2Async`), and the page (`html,body`/`#map` background in
  `map.html`). Keep all four dark or the flash returns.
- **Don't edit files with PowerShell `Set-Content`** — it mangles UTF-8 em-dashes; use
  the editor tools.
- The multi-map "mapKey" seam, the Alaska/Hawaii insets, and the promote feature were
  all **removed**. `RegionProvider` still lists alaska/hawaii and `MapRegion.InsetZoom`
  is now dead data (cleanup candidate).

## Vendoring the radar decoder (no Node / no build step)

The Level II decoder runs in the WebView; there is no JS build pipeline. Two jsDelivr
`+esm` files are vendored (downloaded once with `curl`, like `maplibre-gl.js`/`pmtiles.js`)
and loaded via dynamic `import()` (`radar-decode.js` → `./vendor/nexrad-level-2-data.esm.js`):
`nexrad-level-2-data.esm.js` and `seek-bzip.esm.js` (the decoder already inlines a `buffer`
polyfill, so no separate buffer files are needed). **All vendored web libraries live in
`Assets/Map/vendor/`** (third-party, separated from our authored JS at the `Assets/Map` root,
and marked `linguist-vendored` in the repo-root `.gitattributes` so they're excluded from the
GitHub language stats — see `Assets/Map/README.md` for the layout + the C#↔JS contract). All
are `Content`/`PreserveNewest` (the csproj `<Content Update>` paths point at `vendor\`). **Two
hand patches** are applied to the decoder file and MUST be re-applied if it is re-downloaded:
1. rewrite its import `"/npm/seek-bzip@2.0.0/+esm"` → `"./seek-bzip.esm.js"` (both sit in
   `vendor/`, so the relative import resolves);
2. append `,_ as Buffer` to the final `export{...}` — the constructor gates on
   `input instanceof <its own bundled Buffer>`, so `radar.js` wraps the bytes with the
   decoder's OWN `Buffer.from(...)`. A Buffer from any other module fails that gate and the
   decoder throws **"Unknown data provided"**.

## Radar sites data

The full ~160-site WSR-88D network lives in `Assets/radar-sites.json` (`Content`/
`PreserveNewest`), an array of `{ id, name, lat, lon }` read once by `RadarSiteProvider`
via `AppContext.BaseDirectory\Assets\radar-sites.json` (cached; falls back to a tiny
built-in set if the file is missing). It's **bundled, not fetched** — the app is
offline-first and the radar network is effectively static. The file was generated from
NOAA site coordinates (ethan-nelson/nexrad-coverage `radar_information.geoJSON`, itself
from the ROC); `name` is the title-cased `STATION_NAME` (a couple read oddly, e.g. KAPX
"Ncl Michigan" — cosmetic, the ICAO is the real label). **Dedupe by ICAO when
regenerating** — that source lists 163 features but 160 unique ICAOs (KFSX, KLWX, KMLB
each appear twice); duplicate ids stack invisible markers and break the Hide Sites toggle
(the id-keyed marker map only tracks one of each). The list includes overseas/OCONUS
sites (Alaska/Hawaii/Guam/PR/Korea) that fall outside the CONUS view and may have no data
in the AWS Level II bucket. `RadarSite.Zoom` is vestigial (clicks don't recenter); the
provider applies a constant `DefaultZoom`.

## Radar live frame (chunks bucket)

The newest loop frame comes from the near-real-time `unidata-nexrad-level2-chunks` bucket
(`Level2RadarService.GetLiveFrameAsync`), cutting live latency from the archive bucket's
~10 min to **~1-2 min** — because we render only the lowest tilt (0.5°), which the radar
scans *first*, and the chunks bucket streams it as it's produced rather than waiting for the
whole volume to finish + aggregate.

- **Bucket layout & finding the newest (very subtle — got this wrong twice):** keys are
  `<SITE>/<VOLUME#>/<yyyyMMdd>-<HHmmss>-<seq>-<S|I|E>`. Volume numbers cycle **1..999 and wrap**,
  and crucially **folders are REUSED across cycles** — a single folder (e.g. `KFDR/127/`) can hold
  chunks from the current volume *and* a 1-2-day-old one, since retention (~2 days) overlaps the
  ~3.5-day number cycle. Two traps this creates: (1) the newest volume is NOT at a run's numeric
  end — within a contiguous number-run, the current cycle's data sits at the **low** end and the
  previous cycle's leftovers at the high end (KFDR had run `[1..343]` with the newest at **127**);
  (2) a folder's *first* key (max-keys=1) is the **oldest** (lexically-first) chunk, hiding the
  current data. So `NewestVolumeStartAsync` lists the whole folder and takes the **max** chunk
  timestamp, and `FindNewestChunkVolumeAsync` splits the numbers into contiguous runs and, per
  run, takes the end **unless the run is time-wrapped** (`ts(lo) > ts(hi)`), in which case it
  **binary-searches the rotated array** for the peak (~log n peeks, memoized). And because a
  folder mixes cycles, `GetLiveFrameAsync` **filters chunks to the target volume's start stamp**
  (and keys its per-volume cache by start). ~10-15 list requests; validated against the live bucket.
- **Byte format (the key enabler):** the **S** chunk is `[24-byte AR2V0006 header][4-byte
  control word][BZh… record]`; each **I** chunk is `[control word][BZh… record]` — one LDM
  record each (`DecompressChunk` strips header/control word and bzip2-inflates the rest). The
  cached single-tilt `.V06` is the 24-byte header + the *decompressed* metadata + radial records
  (no control words), which the JS decoder reads directly.
- **Latest-sweep / SAILS chasing (the freshness lever):** precip VCPs (12/212/215) re-scan the
  0.5° tilt 1-3 *extra* times mid-volume (SAILS/MRLE), so the volume-start 0.5° isn't the freshest
  base scan available. **The re-scan carries its own, HIGHER elevation NUMBER but the SAME elevation
  ANGLE as the base tilt**, so `SelectLatestSweep` keys on **angle, not number**: it groups records
  into cuts by elevation number, reads each cut's elevation ANGLE (median of the radial-header
  big-endian float at ICAO+24 — `ElevationAngleOf`) and its moments (`HasMoment` checks for a `DREF`
  reflectivity / `DVEL` velocity block with a plausible gate count), then emits the **latest
  *complete* lowest-tilt SURVEILLANCE cut**: lowest tilt = the min angle among reflectivity cuts,
  matched within `tiltTol` (0.12°, the SAILS re-scan reads the base angle within antenna jitter);
  SURVEILLANCE = reflectivity present, NO velocity (split-cut precip VCPs scan 0.5° twice — a
  long-PRT surveillance cut we render and a short-PRT Doppler cut carrying velocity + range-folded
  reflectivity we skip). Clear-air VCPs have one combined cut, so when no velocity-free cut exists
  it falls back to the latest low-tilt reflectivity cut. (**History/bug:** the original keyed on
  elevation-NUMBER == 1, which only ever found the volume-start scan and missed the SAILS re-scans —
  so SAILS sites sat 4-7 min behind during outbreaks while the card claimed `0.5°×1`. Verified by
  decoding live KILX (SAILS, re-scan at elevation number 13) and KSHV (non-SAILS) volumes; the new
  logic picks the re-scan on KILX and the base on KSHV.) `VolumeTime` is that cut's **actual radial
  collection time** (`ReadCollectionTime`: ms-since-midnight at ICAO+4, modified Julian at ICAO+8;
  sanity-clamped to [start, now] or it falls back to the volume-start stamp). So in SAILS weather
  the live frame now refreshes every ~1.5-3 min instead of once per ~5-6 min volume; the `0.5°×N`
  mode count is the number of low-tilt sweeps found. Clear-air VCPs have one 0.5° sweep, no-op there.
- **Per-volume chunk cache:** `GetLiveFrameAsync` keeps the decoded blocks of the current
  in-progress volume (`_liveVolKey`/`_liveHeader`/`_liveBlocks`) and on each poll downloads +
  bzip2-decodes only the **new** chunks, so chasing the growing volume costs ~one volume's worth
  of bandwidth total rather than re-pulling it each poll. Reset when the newest volume changes.
  Capped at `LiveChunkCap` (90) chunks.
- **Newest → previous fallback (the "slow first live frame" fix):** `GetLiveFrameAsync` builds the
  newest volume via `BuildLiveFrameAsync(…, useCache:true)`; when that volume is still mid-scan and
  has **no complete 0.5° sweep yet**, it doesn't just return null — it **falls back to the previous
  (finished) volume** (`useCache:false`, one-shot). Without this, a freshly-clicked site whose newest
  chunks volume just started showed nothing — `mode`/`live` stayed `—` — until that volume finished
  its first tilt, **a minute or two on slow clear-air VCPs** (a volume is ~10 min, one 0.5° sweep, no
  SAILS); KLGX (Langley Hill, clear-air) was the repro. The previous volume is finished and immutable,
  so it's built **once and cached** (`_fallbackKey`/`_fallbackVolume`) and reused across the repeated
  polls instead of re-downloading ~a volume of chunks each time. Its sweep is a touch older but usually
  still ≥ the archive newest, and crucially it surfaces the mode immediately; once the newest volume
  completes, the next poll appends/updates it as the real live frame.
- **Mode readout:** `SelectLatestSweep` also returns the **VCP number** (`ReadVcp`: the radial's
  VOL data-block pointer at ICAO+32, VCP at VOL+40) and the count of complete 0.5° sweeps.
  `DescribeMode` → e.g. `VCP 215 · precip · 0.5°×3 · SAILS/MRLE ×2`, carried on
  `RadarVolume.ModeText` and shown on the debug card (`mode:` line). Clear-air VCPs = 31/32/35/90.
  The `mode:` line reads `MapViewModel._liveModeText`, captured in `RecordLivePoll` from **every**
  successful live poll — deliberately decoupled from `_liveFrame` (the appended frame). An
  offline/stale site (e.g. KVNX during an outage) still has a newest chunks volume, but it merely
  equals the archive newest, so `ApplyLiveFrameAsync` correctly does NOT append it as a new frame
  (no duplicate) — yet the mode is still known from the poll, so we show it instead of stalling on
  "— (awaiting live frame)". `_liveModeText` is cleared per site click (not per reload).
- **Hybrid:** loop *history* stays on the archive bucket (chunks expire — old volumes lose
  their S chunk, e.g. `343/` starts at chunk 015). Only the single newest/live frame is from
  chunks, cached as `{site}_live_<sweepTime>.V06` (own infix; skipped by the archive prune, kept
  newest-1 by `PruneLiveCache`; a fresh sweep = new timestamp = new file = the VM sees it as newer).
- **VM load order (tuned for fast card population):** `LoadLoopCoreAsync` loads the **newest
  archive frame first** (immediate paint), then **`RefreshLiveFrameAsync` (the live frame + scan
  mode) BEFORE the older-frame backfill**, then backfills `0.._archiveCount-2`. The ordering is
  deliberate: the card's time/mode/freshness come from the newest + live frames, so doing the live
  poll before the ~5 s backfill makes the card populate in ~1-2 s instead of ~6-8 s. (Backfill is
  bound by `_archiveCount`, not `_frameCount`, since the live append grows `_frameCount`.) The
  poll/backfill run **sequentially** under `_loopGate` — never concurrently — so the live append
  growing `_frameTimes` can't race the backfill's index writes. The **card properties are also
  decoupled from `IsLoopReady`**: `RadarCardTime`/`RadarFrameDetail`/`RadarStatus`/`RadarAgeText`
  show as soon as the relevant frame's time exists (newest loads sub-second), rather than waiting
  for every frame to decode; only the scrubber/playback wait on `IsLoopReady`. (`RadarLoopSpanText`'s
  oldest end still fills in as the backfill completes — secondary info.) `ApplyLiveFrameAsync`
  appends the live frame as an extra newest frame at index `_archiveCount`, **but only when strictly
  newer than the archive newest / current live** (the guard that stops a stale chunks volume from
  overriding fresh data).
  `RunLiveFrameRefreshAsync` re-runs it every ~30 s (`LiveFrameRefreshSeconds`, tuned to catch SAILS
  re-scans), but **polls faster (~20 s, `LiveFrameRetrySeconds`) until the first live frame lands** —
  it stamps `_nextLivePollAt` before each wait so the debug card's `poll:` line shows a live `next in
  Ns` countdown. The load-time poll
  often hits a still-scanning volume, so the `mode`/`live` card fields stay `—` until a retry
  succeeds. A `_loopGate`
  semaphore serializes the (re)load and the live poll so they can't interleave at awaits and
  desync the frame arrays / VM↔JS index state. `radar.js` re-uploads a re-decoded current
  frame so the in-place update repaints, and logs `WEBGL CONTEXT LOST`/`RESTORED`.
- **Layer re-add (the "tiles vanish and never come back" fix):** every reload calls `beginLoop`
  → `removeLayer` + `currentFrame=-1`, and the layer is auto-(re)added when a frame decodes.
  The bug: appending the live frame called `showFrame(10)` *before* it decoded, pinning
  `currentFrame=10` and defeating the `currentFrame<0` auto-show — so no archive decode re-added
  the layer, and idx-10's decode only repainted *if a layer existed* (it didn't). Paused, the
  radar stayed blank forever; playing, it limped back only when playback moved `currentFrame`.
  Fix: a `pendingFrame` slot — `showFrame` on an undecoded index records intent without touching
  `currentFrame`, and `applyFrameResult` routes every show through `showCurrent`, which **re-adds
  the layer whenever it's missing**. So any decode (pending target, first arrival, or the current
  frame) restores the layer, paused or playing.
- **Radar site card + event log:** a lower-left on-map card (shown while a loop is active, ticked
  1 s by `RunDebugTickAsync`). The **top** is a polished site readout — status dot (color from
  `MapViewModel.RadarStatus` / `RadarFreshness` Live/Recent/Stale by newest-frame age, mapped to a
  brush by `MainWindow.RadarDotBrush`), title (`RadarCardTitle`), current frame time + detail
  (`RadarCardTime`/`RadarFrameDetail`), and `RadarModeText`/`RadarAgeText`/`RadarLoopSpanText` rows.
  All those card props + the diagnostics text are raised together by `RaiseRadarCard()` (replacing
  the old scattered `OnPropertyChanged(nameof(RadarDebugText))` calls). The full developer
  diagnostics live in a **collapsible "Diagnostics" `Expander`** (collapsed by default) bound to the
  same `MapViewModel.RadarDebugText` monospace blob — which still reports site, current frame
  #/count + source (ARCHIVE/LIVE), **load timings** (`load:` line — seconds from the site click
  to the first frame, and to all frames incl. the live one, rendered; captured once per click,
  frozen after the initial load so the ~60 s live refreshes don't overwrite them, also logged as
  `TIMING …`), the radar **mode** (`mode:` line — VCP/precip-vs-clear-air/SAILS, from the live
  frame's `ModeText`), current/live/archive-newest times + ages, loop span, last live-poll
  outcome, and a tail of recent events. Behind it, `Services.RadarDebugLog` is a
  process-wide thread-safe ring buffer (4000 entries) written from the VM (`Diag`), the Level II
  service (`svc live …`), and the radar JS (`js …`, routed via MainWindow's `radarLog` handler).
  `radar.js` logs the render path — `beginLoop`/`addFrame`/`decoded`/`showFrame`/layer
  add-remove and, rate-limited, `RENDER ERROR`/`RENDER BLANK`/`render recovered` — which is how
  we catch intermittent "tiles vanish and don't come back" issues. **Copy debug log** copies the
  full timeline to the clipboard and writes `RadarLevel2/radar-debug-*.log`; **Clear** resets the
  buffer to start a clean capture.

## Next steps

**Radar:** the full ~160-site list is now bundled (above) — remaining is a **searchable /
decluttered picker** (160 markers at once is busy; the Hide/Show Sites toggle is a stopgap);
a velocity product + ramp; proper dual-pol clutter filtering (CC-based) instead of the blunt
`MIN_DBZ`; make the loop refresh *incremental* (id-keyed frames) so it doesn't re-decode
cached frames on reload. The chunks-bucket live frame is **done** (see "Radar live frame");
a possible follow-up is making the live frame its *own* extra step rather than just refreshing
the trailing slot, and tuning the ~60 s poll.

**Robust VCP parse — DONE (Message 5).** `SelectLatestSweep` now reads the VCP from
**Message 5 (RDA Volume Coverage Pattern Data)** in the leading metadata record
(`ReadVcpFromMetadata`), falling back to the old best-effort Message-31 VOL-block read
(`ReadVcp`) only when Message 5 doesn't yield a recognized VCP. **Framing (verified against
the vendored decoder's own constants/parsers, `Assets/Map/vendor/nexrad-level-2-data.esm.js`,
AND against ~22 cached `.V06` files — every one resolved a real VCP):** the metadata record
holds only non-Message-31 messages, each a fixed **2432-byte** frame (`RADAR_DATA_SIZE`) =
12-byte legacy CTM header (`CTM_HEADER_SIZE`) + 16-byte message header + body. So the
**message-type byte is at frame offset 15** (12+3) and a body starts at frame offset 28;
Message 5's body is `message_size, pattern_type, pattern_number, …`, so the **VCP
(pattern_number) is at frame offset 32** (28+4). `ReadVcpFromMetadata` walks every block at
2432 strides, **stops at the first Message-31 (radial) frame** (Message 5 is always in the
metadata that precedes the radials, and the 2432 stride doesn't hold inside variable-length
radial data), matches type 5 (or reserved twin 7), and validates via `IsKnownVcp` (the
`ClearAirVcps`/`PrecipVcps` sets) — a bad read still shows `VCP ?`, never a wrong number.
**Subtle bug that made a first attempt a no-op:** the walk must NOT key off `firstRadial` /
block elevations. The metadata record contains the ICAO string at an offset whose byte-22
reads as a bogus elevation, so `ElevationOf` flags a metadata block as a radial and
`firstRadial` collapses to 0 — a `blocks[0 .. firstRadial-1]` loop then scans nothing and
every site shows `VCP ?`. Walking all blocks until the first true Message-31 sidesteps it.
The `0.5°×N` SAILS count is independent (sweep-counting). Cosmetic only — rendering doesn't
depend on it. Files: `Services/Level2RadarService.cs` (`ReadVcpFromMetadata`, `IsKnownVcp`,
`ReadVcp`, `SelectLatestSweep`, `DescribeMode`).

**SPC:** periodic refresh timer is **DONE** (15-min `PeriodicTimer`, see SPC outlooks above);
remaining: empty-outlook handling; a fire-weather `dn`→color ramp (fire products lack embedded
colors).

**Cleanup:** trim the dead inset data (`RegionProvider` alaska/hawaii,
`MapRegion.InsetZoom`); de-hardcode the `mapdata` Desktop PMTiles path.
