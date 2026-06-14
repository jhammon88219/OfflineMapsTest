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
  endpoint table), `IRadarSiteProvider` / `RadarSiteProvider` (curated WSR-88D sites),
  `ILevel2RadarService` / `Level2RadarService` (fetch/cache of Level II volumes).
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
  cache; `radarlevel2` → cached Level II `.V06` volumes.

## What's built

- **Bottom control ribbon** (themed bar): `Radar Opacity | Day | Outlook | Opacity |
  Basemap | collapse`, with a radar **loop bar** (play/pause + scrubber + timestamp) shown
  above it while a loop is active. **Radar sites are picked by clicking on-map markers**
  (RadarScope-style), not a dropdown. Collapsing hides the ribbon and shows a small
  "Controls" reveal handle.
- App starts **maximized**; basemap defaults to **Data Viz Black**; outlook opacity
  defaults to **0.05**; radar defaults to **None** at **0.85** opacity.
- **SPC outlooks:** `SpcOutlookService` fetches/caches all 23 SPC products (convective
  categorical d1–3, torn/wind/hail d1–2, d3 combined prob, extended prob d4–8, fire
  d1–8) as GeoJSON via `HttpClient` (conditional GETs, last-known-good on failure,
  per-product isolation, results returned not thrown). URLs live in the editable
  `SpcOutlookCatalog`; convective come from SPC `.lyr.geojson` "cake layers", fire from
  the NOAA ArcGIS `SPC_firewx` MapServer. A background refresh fires on launch. UI:
  dependent Day + Product comboboxes (cascade, with a "None" option) and an Opacity
  slider. Convective polygons render in their embedded SPC colors; "significant" areas
  (`LABEL` contains `CIG` or `SIG`) render as diagonal **hatching** via a runtime canvas
  `fill-pattern` (density = `TILE` const in `map.js`, currently 32). Outlook sits below
  the basemap labels.
- **Radar (NEXRAD Level II):** `Level2RadarService` lists the recent volumes for a site
  from the public AWS **archive** bucket `unidata-nexrad-level2` (keys are
  date-chronological under `<y>/<m>/<d>/<SITE>/`, so take the last N **ending in `_V06`**;
  a small `_MDM` metadata sidecar sorts after the volume for the same timestamp — don't grab
  it), then in C# (`SharpCompress`, off-thread)
  **decompresses only the lowest-elevation records** — it walks the LDM records, stops at
  the first elevation 2 record, and caches a ~7 MB single-tilt buffer instead of the full
  ~86 MB / ~14-tilt volume — serving it via the `radarlevel2` host. (Records align to
  elevation boundaries, scanned lowest-first; elevation is read as the byte 22 after the
  ICAO at the start of each Message 31 block.) The
  WebView decodes it **off the UI thread** (`radar-worker.js` → `radar-decode.js`) and
  GPU-renders the lowest-tilt reflectivity via a WebGL custom layer in `radar.js`. UI: a
  **Radar Opacity** slider, plus ~12 curated sites shown as **clickable on-map markers**
  (`showRadarSites`; click to select, click the active one to clear — `radarSiteClick`).
  Clicking a site loads a **~10-frame loop** (last ~hour) at the current view (no recenter): the newest frame shows
  immediately, older frames backfill off-thread, cached one file per volume timestamp. UI
  adds **play/pause + a frame scrubber + a timestamp**; it reloads when a new volume appears
  (~5 min). Renders beneath the SPC outlook and basemap labels (dBZ ramp in
  `radar-decode.js`, `MIN_DBZ` in `radar.js`). Loop state + playback live in `MapViewModel`;
  `radar.js` stores the decoded frames and renders the current one (uploading on switch).

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
and loaded via dynamic `import()` from `radar.js`: `nexrad-level-2-data.esm.js` and
`seek-bzip.esm.js` (the decoder already inlines a `buffer` polyfill, so no separate buffer
files are needed). Both are `Content`/`PreserveNewest` in the csproj. **Two hand patches**
are applied to the decoder file and MUST be re-applied if it is re-downloaded:
1. rewrite its import `"/npm/seek-bzip@2.0.0/+esm"` → `"./seek-bzip.esm.js"`;
2. append `,_ as Buffer` to the final `export{...}` — the constructor gates on
   `input instanceof <its own bundled Buffer>`, so `radar.js` wraps the bytes with the
   decoder's OWN `Buffer.from(...)`. A Buffer from any other module fails that gate and the
   decoder throws **"Unknown data provided"**.

## Next steps

**Radar:** expand `RadarSiteProvider` toward the full ~160 NEXRAD list with a searchable
picker; a velocity product + ramp; proper dual-pol clutter filtering (CC-based) instead of
the blunt `MIN_DBZ`; make the loop refresh *incremental* (id-keyed frames) so it doesn't
re-decode cached frames on reload; optionally the `unidata-nexrad-level2-chunks` bucket for
sub-minute freshness.

**SPC:** a scheduled/periodic refresh timer (currently manual on launch); empty-outlook
handling; a fire-weather `dn`→color ramp (fire products lack embedded colors).

**Cleanup:** trim the dead inset data (`RegionProvider` alaska/hawaii,
`MapRegion.InsetZoom`); de-hardcode the `mapdata` Desktop PMTiles path.
