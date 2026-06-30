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
  "Radar live frame" below),
  `ILocationService` / `LocationService` (resolves the user's location — `GetFromOperatingSystemAsync`
  via `Windows.Devices.Geolocation`, `GetFromIpAddressAsync` via an HTTPS IP lookup, and `ResolveAsync`
  = OS then IP fallback — see "User location" below).
- **`Assets/Map/js/map.js`** is the parameterized page loaded by `map.html` in the single WebView. URL
  params: `key`, `interactive`, `style`, `lng`, `lat`, `zoom`. It is the **orchestrator** (~150 lines):
  it creates the MapLibre map, owns `window.applyStyle` (which re-adds every overlay after a basemap
  switch — outlook, watches, radar), `flyTo`, the thin radar/DOW `window.*` shims, the color-ramp feed,
  `setRadarSweep`, and posts `{type:"mapReady"}` (and `radarSiteClick` / `radarFrameReady`) on the
  message channel. **The per-feature LOGIC was split out of `map.js` into focused ES modules** (it was a
  ~620-line god file): `outlook.js` (SPC outlook fills + per-CIG hatching + nested-polygon clipping),
  `watches.js` (watch boxes), `radar-sites.js` (the on-map site-marker "key" buttons + their CSS),
  `markers.js` (the pulsing-blue user-location marker), and `geo.js` (the ONE site projection, shared
  with `radar-decode.js`). **Mechanism:** `map.js` stays a classic `<script>` and **dynamically
  `import()`s** each module once at startup, caching it (e.g. `var Outlook = null; import('./outlook.js')
  .then(m => Outlook = m)`); the `window.*` shims + `applyStyle`'s re-add then **delegate** to those
  modules, passing the `map` instance (`window.showOutlook = url => Outlook && Outlook.show(map, url)`).
  Each concern module owns its own state + a `reAdd(map)` for basemap switches. `geo.js` is imported
  **statically** by `radar-decode.js` (a real module) but **dynamically + cached** by the classic-script
  `radar.js`/`map.js` (guards skip drawing until it loads — it's tiny, loads before any frame). MapLibre
  is **v5.21.1**.
- **Radar JS (under `Assets/Map/js`):** `radar.js` owns the **MapLibre WebGL custom
  layer** + host shims (`window.RadarLayer`); `map.js` wires the shims and
  re-adds the layer after a style switch. The heavy work runs **off the UI thread** in
  `radar-worker.js`, which calls the shared `radar-decode.js` to bzip2-decode the `.V06`
  (vendored `nexrad-level-2-data` + `seek-bzip`), build the lowest-tilt reflectivity/velocity
  gate geometry (velocity **dealiased** — see "Velocity product"), color it via the shared
  `radar-ramps.js` (the color scales, kept separate so a legend can reuse them), and project it via the
  shared `geo.js` (the ONE site equirectangular projection — also used by `radar.js`'s range ring /
  sweep / inspector, so overlays line up with the painted gates); it then
  transfers the typed arrays back zero-copy. A main-thread fallback uses the same
  `radar-decode.js` if a Worker can't be created. The decoder is vendored offline (jsDelivr
  `+esm`, no Node/build step) — see "Vendoring the decoder" below. (NB: the cached single-tilt
  `.V06` is already bzip2-**decompressed** by the C# extraction, so this JS decode runs fast —
  off-threading just keeps the UI smooth; it is NOT the ~5 s *full-volume* bzip2, which only the
  rare raw-fallback cache takes.)
- **Decode-in-C# migration — TRIED and REVERTED (keep decode in JS).** Moving decode + dealias +
  gate-geometry into C# (shipping the WebView a prebuilt geometry blob instead of the `.V06`) was
  prototyped behind a flag and reverted as a net perf **regression**: because the cached `.V06` is
  already decompressed, JS decodes it **in-process** fast, whereas the C# path adds a ~16 MB
  expanded-geometry round-trip per frame (C# writes a blob to disk, JS fetches it back) that the
  in-process path avoids — strictly more work. (The original "C# decode is ~50× faster" premise was
  wrong: it compared against the ~5 s *full-volume* bzip2, not the fast cached-file decode.) The C#
  `Level2Decoder` was proven byte-correct — restore from git only if a standalone **C# NEXRAD decoder
  library** is ever wanted; its value is there, not in this app's render path.
- **WebView2 virtual hosts** (all `Allow`): `mapassets` → `Assets/Map`; `mapdata` → the
  user-configured offline-basemap folder (`ISettingsService.MapDataFolder`, default a runtime-resolved
  Desktop — see "App settings" below; holds the ~29 GB `usa_full.pmtiles`); `spcoutlooks` → SPC GeoJSON
  cache; `spcwatches` → cached SPC watch-box GeoJSON; `radarlevel2` → cached
  Level II `.V06` volumes (archive frames + the `_live_` chunks frame).

## What's built

- **Tool-window docks (static mock of a VS-style dock — NOT real drag/dock yet).** The old bottom
  ribbon AND the two floating cards (radar site card, outlook info card) are **GONE** — all absorbed
  into two full-height overlay "docking stations". The **stations themselves are transparent** (incl.
  the left "Tools" title bar) — the map shows through the gaps between/around the windows; only the
  tool-window **cards** are opaque. Each card = a `Border` styled by `ToolCardStyle` (5px margin so
  they don't touch, rounded, opaque `SolidBackgroundFillColorBaseBrush` fill, and an **always-on,
  theme-aware base border** = `CardStrokeColorDefaultSolidBrush` — the *solid* card stroke, chosen
  over the near-invisible low-alpha `CardStrokeColorDefaultBrush` so every card reads as a bordered
  window at rest) + a `ToolCaptionStyle` caption bar (inert pin/✕/… glyphs). **The focused card's
  border swaps to the system accent** (`MainWindow.OnCardGotFocus`, a routed `GotFocus`; captures the
  base brush once and reverts to it on focus-out; the
  last-focused card stays lit, VS-style; the read-only Selected Site card has no focusable content so
  it doesn't light). The shared `Style`s live in `Grid.Resources` (Window has no `Window.Resources`).
  **Left station**
  (`MapViewModel.IsDockExpanded`, collapses via `ToggleDock` to a left-edge "Tools" reveal button) =
  **four equal-height** tool windows: **Radar Sites** (a real `ListView` of `MapViewModel.RadarSites`,
  `SelectionMode=Single` with `SelectedItem` two-way bound to `MapViewModel.SelectedRadarSite` — a
  map-marker pick highlights + scrolls to the row and a row pick activates the site, both funnelling
  through the single `SelectedRadarOption`; `MainWindow` does the `ScrollIntoView` and a
  `_syncingSelection` guard prevents a select↔select loop), **Radar Loop** (play/pause + scrubber
  + frame time + **Loop length** (6-30 frames) + **Update interval** (live-poll cadence) + **Playback
  speed** + **Product** + Radar Opacity + Show/Hide Sites + Reset, with a disabled Tilt stub; **Stop**
  (`StopRadarLoop`) halts playback and snaps to the newest frame, enabled only while the loop is
  engaged — playing or paused — via `CanStopLoop`/`_loopEngaged` (play/pause keeps it engaged).
  (Frame readouts — `RadarFrameDetail` "frame N/M" + `RadarCardTime` — are raised from the
  `CurrentFrameIndex` setter, not just the 1s tick, so they don't lag fast playback.)
  The three new combos are `SelectedIndex`-bound to `LoopLengthIndex`/`RefreshIntervalIndex`/
  `PlaybackSpeedIndex`, each mapping an index → a presets array — loop-length change rebuilds the loop,
  the others apply live on the next poll / animation tick),
  **Outlook** (Day/Product/Opacity/**Basemap** selectors — Basemap had no other home after the ribbon),
  and **Outlook Details** (issued/effective + the SPC discussion). **Right station** = tool windows
  stacked from the top (Auto-height, so they hug the top with the map showing through below): **Selected
  Site** (the radar readout: status dot, title, frame time/detail, mode, age, loop span; shown while a
  site is selected, `HasRadarLoop`), **Color Scale** (the active product's generated legend bar; shown
  with a loop, `HasColorScale` — see "Color-scale legend"), **Selected Marker** (the user-location marker
  editor; shown while a marker is selected, `HasSelectedMarker` — see "User location + map markers"), and
  **Map** (Basemap selector + the **My Location** button). All controls bind to the **same VM properties**
  the ribbon/cards used (move, not rewrite). The dev
  **diagnostics moved out of the UI entirely** into `Services.RadarDiagnostics` (per-run JSONL +
  report files; see "Radar diagnostics" below) — the in-card Diagnostics expander + Copy/Clear were
  dropped, and the old `RadarDebugText`/`RadarDebugLog` are gone. Still a static mock: caption glyphs +
  Stop/Product/Tilt are inert; no drag/dock/tear-out; the eventual real version extracts a reusable
  `ToolWindow` control. **Planned: real drag/dock is being built as a SEPARATE project,
  `VSDragDockTest`** — a user-friendly, MVVM, DI-ready drag/dock system modeled on Visual Studio's
  (dockable/floatable/tear-out tool windows, dock targets, splitter layout), the goal being to run
  this radar app like an IDE shell. It's developed standalone and will be integrated back into this
  app once finished (replacing the static-mock docks here). **The Day combo is labeled by date** ("Day 1 · Sat Jun 14"); **radar sites are
  also pickable by clicking on-map markers** (RadarScope-style); **Show/Hide Sites** toggles all markers
  (`setRadarSitesVisible`), independent of the radar layer.
- App starts **maximized**; basemap defaults to **Data Viz Black**; outlook opacity
  defaults to **0.05**; radar defaults to **None** at **0.85** opacity. The outlook **starts hidden**:
  a **"Show outlook layer" toggle** (`MapViewModel.IsOutlookVisible`, default **off**) in the Outlook
  tool window is a master on/off gate INDEPENDENT of the Day/Product combos — Day 1 Categorical is the
  armed default, but nothing draws until the toggle is flipped on (one flip reveals it). The gate also
  drives `HasOutlookCard` / the times readout, so the Outlook Details window + issued/valid line follow
  it (`ApplyCurrentOutlook` / `UpdateOutlookTimes` / `OnMapsReadyAsync` all check `_isOutlookVisible`).
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
- **SPC watches (Tornado / Severe Thunderstorm Watch areas):** `ISpcWatchService` / `SpcWatchService`
  fetches/caches the active convective-watch polygons as GeoJSON, served via the `spcwatches` virtual
  host → `https://spcwatches/watches.geojson`. **Source is the NWS WWA event-driven map service**
  (`mapservices.weather.noaa.gov/.../WWA/watch_warn_adv/MapServer/1`, the `WatchesWarnings` layer),
  queried `where=sig='A' AND (phenom='TO' OR phenom='SV')` with `f=geojson`. That layer serves the
  **official county-aggregated** geometry — the polygon is the union of the watch's counties, so it
  **follows county lines (matching RadarScope)**, NOT the older SPC parallelogram box. (Two dead ends
  proved out and rejected: `api.weather.gov/alerts/active` watch entries have **null geometry** — only
  a `geocode.UGC` county list — so nothing renders; IEM's `spc_watch.py` serves the **parallelogram
  box**, not the county shape.) The service is current-events-only (everything returned is active —
  no client-side expiry filtering); it just validates the body is a `FeatureCollection`
  (`TryGetFeatureCount`, keeps last-known-good on an ArcGIS error object) and caches it. A background
  `PeriodicTimer` refreshes every **2 min** in `MainWindow.RefreshWatchesInBackgroundAsync`,
  re-pushing to the page (`MapViewModel.OnWatchesRefreshed`) on launch + any cycle that pulled data.
  **One toggle in the Outlook tool window** (`ShowWatches`, default **off**) →
  `IMapService.SetWatchesVisibleAsync`. `map.js` loads the GeoJSON **lazily** (only when shown) and
  draws a faint fill + bold outline (`spc-watch-fill`/`spc-watch-line`) above the radar/outlook, below
  labels — colored by the feature's `phenom` via a `match`: **TO = red, SV = yellow** (unknown →
  fallback). `setWatchSource(url)` / `setWatchesVisible(on)` are the JS shims; layers re-add after a
  basemap switch.
- **SPC outlook info (now the left dock's "Outlook Details" tool window; was a top-right floating
  card):** shown while an outlook (not "None") is selected; same theme brushes as the dock. Header (`Day N · Type`) + **Issued/Effective** (from the
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
  GPU-renders the lowest-tilt **reflectivity / base velocity / correlation coefficient** (selectable
  via the Product combo — see "Velocity product" + "Correlation Coefficient product" under Next steps)
  via a WebGL custom layer in `radar.js`. UI: a
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
- **Radar site markers + range ring + sweep arm (the on-map "look").** The site markers are
  **pushable Windows/Fluent "key" buttons** (in `map.js`'s `radar-site-style`): a `.radar-site-marker`
  wrapper (MapLibre positions it) holds an inner `.radar-site-btn` (free to use its own `transform` for
  the press/sink effect). States: **available** = calm graphite key (deliberately NOT bright — a bright
  default read as "already on"); **selected** = orange latched-down key; **down/offline** = muted
  dark-red dead key. Each carries a **secondary status halo** (`::after`): a green ring (available) /
  red ring (down). NB the file is built with a backtick **template literal**, so **no backticks inside
  it** (comments included) or it silently breaks the whole `map.js` parse — a bug we hit twice.
  **A selected site also gets two GEOGRAPHIC map layers from `radar.js`** (not DOM decorations, so they
  scale with zoom): a **range ring** (`level2-range`) drawn at the radar's REAL outer data extent
  (`rangeMeters` = `first_gate + numGates·gate_size` from the decode, RadarScope-style — varies by
  radar/VCP), and a **rotating sweep ARM** (`level2-sweep`) from the site centre out to that ring,
  **phase-locked to the live-poll cycle** (`setRadarSweep(periodSeconds)` → one revolution = time to the
  next update; a `requestAnimationFrame` updates the arm's bearing). Both re-add on basemap switch
  (`reAdd`), drop on a new site / clear; the sweep is hidden in Past Event (replay) mode. (History: the
  sweep started as a small DOM ring on the key, then a circular DOM scope — both removed once the real
  geographic ring landed.)
- **Radar Sites list rows mirror the on-map button states.** The dock's "Radar Sites" `ListView` binds
  to **`MapViewModel.RadarSiteRows`** (`ViewModels/RadarSiteRow.cs` — an observable presentation row
  wrapping the immutable `RadarSite` with an `IsOffline`/`IsAvailable` flag; the immutable record can't
  carry view state). Rows render in the same palette as the on-map keys via overridden `ListViewItem`
  state brushes (graphite available / orange selected / muted-red down), and **down rows are disabled
  (`IsEnabled` ← `IsAvailable`) so they're not clickable**. The offline set that styles the map markers
  also flips each row's `IsOffline` (one source of truth). Selection runs through **`SelectedSiteRow`**
  (replaced the old `SelectedRadarSite` bridge), still funnelling to `SelectedRadarOption`; `MainWindow`
  `ScrollIntoView`s the selected row.
- **User location + map markers (the "My Location" button + the "Selected Marker" tool window).**
  The whole feature is one self-contained block (a delimited region in `MapViewModel` + the
  `MapMarker`/`MarkerKind` models + the `map.js` marker shims + the Selected Marker tool window — built
  to be easy to peel back). **Locate:** `ILocationService` / `LocationService` resolves the position
  with **two methods**, OS first then IP fallback (`ResolveAsync = GetFromOperatingSystemAsync ??
  GetFromIpAddressAsync`). OS uses `Windows.Devices.Geolocation.Geolocator` (needs the `location`
  `DeviceCapability` in `Package.appxmanifest` + one-time consent); IP does a single HTTPS GET to
  `https://ipwho.is/` (key-less, city-level). Both **return `null` on failure** so the fallback chains.
  `UserLocation` carries `{Lat, Lon, Source, AccuracyMeters?, Description?}`.
  **Marker model:** `MapMarker` is a **mutable, observable** entity (unlike the immutable domain
  records — it moves when dragged): `{Id, Kind, Lat, Lon, Label, PositionSource, CanDrag, IsSingleton}`.
  Per-kind rules are **data, not subclasses** — `MarkerKind` is an enum (today only `UserLocation`),
  the "only one user location" rule is enforced in the VM (`UpsertUserLocationMarker` replaces the
  existing one), not by a type. The VM holds a private `_markers` list + a `SelectedMarker`.
  **Refine by dragging:** `window.showUserLocation` makes the marker `draggable:true`; a `dragend`
  posts `{type:"markerMoved", id, lng, lat}` and a click posts `{type:"markerClick", id}`;
  `MainWindow.OnWebMessageReceived` routes them to `ViewModel.OnMarkerMoved` (records the new
  position + flips `PositionSource` → `LocationSource.Manual`) / `OnMarkerClicked` (selects it). The
  flow is **JS → C# only** for now (drag-only); manual coordinate entry / address search would add the
  C# → JS push (`window.moveMarker`) + a `_syncingSelection`-style guard. **UI split (deliberate, no
  duplication):** the **Map card** owns only the locate ACTION — the button (with a `ProgressRing`
  while locating, `IsLocating`/`CanLocate`) + a **transient** line (`LocateStatusText`: "Locating…" /
  "Location unavailable", empty on success). The **"Selected Marker" tool window** (right dock, shown
  while `HasSelectedMarker`) owns the marker ENTITY's standing readout: title (`SelectedMarkerKindLabel`
  = "My Location"), subtitle (place name), coords, a **source affordance** (`SelectedMarkerSource` →
  `MainWindow.LocationGlyph`/`LocationBrush`: green MapPin = GPS, amber Globe = IP, **blue Edit pencil =
  Manually adjusted**), a "drag to refine" hint, and a **Remove** button (`RemoveSelectedMarker`).
  Locating auto-selects the marker so the window appears. `flyTo`s the location at zoom 8 on locate.

- **Past Event Viewer (radar replay mode — right dock tool window). Phase 1.** A second radar mode:
  instead of the live loop (recent volumes + an auto-refreshing near-real-time frame), replay a fixed
  HISTORICAL window the user picks. The tool window has a **Replay mode** toggle (`IsPastEventMode`),
  **Year/Month/Day** combos (year list starts at 2008, the decodable era) + a start time (local) +
  **Duration** (30 min … 12 h), a **Load** button, and a status
  line. Site is picked in the normal **Radar Sites list** (reused). Flow: toggle on → the live loop is
  cancelled and the **live-only controls gray out** (`IsLiveControlsEnabled` gates Loop length / Update
  interval / Reset loop; the on-map sweep arm is dropped — the range ring stays); pick a site → it just
  *targets* the Load action (no live loop starts — the mode-aware `SelectedRadarOption` setter routes a
  site pick to `SelectPastSiteAsync`, which clears + highlights but starts nothing); set a window →
  **Load**. Toggling replay **off clears to idle** (per design). It **reuses the entire loop machinery**
  — decode/render/scrub/play, the range ring, products, opacity, inspect — via the same
  `BeginRadarLoopAsync`/`AddRadarFrameAsync` path; the only new pieces are
  `ILevel2RadarService.GetKeysForWindowAsync(site, startUtc, endUtc)` (lists archive volume keys in an
  arbitrary UTC window — generalizes the live `GetRecentKeysAsync` day-prefix listing, handles crossing
  midnight, **does NOT prune** so past frames aren't deleted by a live prune) and `MapViewModel.
  LoadSelectedPastEventAsync` → `LoadPastLoopAsync` (the live-free counterpart of `LoadLoopCoreAsync`:
  starts at frame 0 = oldest so play moves forward, NO live poll / NO `RunRefreshAsync`). Frames are
  capped at `PastEventMaxFrames` (40): a short window loads every volume (~5 min apart, smooth), a
  long one (e.g. 12 h ≈ 140 volumes) is **evenly subsampled** to 40 frames (first+last kept) so it's
  an overview rather than melting memory. Replay sessions are tagged in `RadarDiagnostics` (`replay.load`),
  so loaded events double as a **deterministic test corpus** — the eventual automated replay-scoring
  (Phase 3) runs over these cached events. **Archive formats (the "old events return nothing" saga):**
  the `unidata-nexrad-level2` bucket is a FULL archive (back to at least 2005, not recent-only). Two
  storage details bit us: (1) older volumes are **gzip-wrapped** (`..._V0x.gz`), so `EnsureCachedAsync`
  **gunzips** a `.gz` key before `TryExtractLowestTilt` (`Gunzip` helper; recent live volumes are raw,
  so the gunzip only runs for historical fetches — and the gunzipped bytes are the FULLY-UNCOMPRESSED
  AR2V format, not the bzip2-LDM one the live `_V06` uses, but the same Message-31 records, so it
  decodes the same). (2) the **archive-format version suffix changes by era**, which the listing filter
  must accept — `IsVolumeKey` now keys on a `_V<NN>` suffix (after stripping `.gz`):
    • `_V06` (AR2V0006, **2013+**): super-res Message 31 **+ dual-pol** — all products incl. CC.
    • `_V03`–`_V05` (AR2V0003+, **~2008-2012**): super-res Message 31, refl/vel/SW but **NO dual-pol**,
      so the **CC product is empty** for those dates (e.g. KTLX got dual-pol in 2012; El Reno/Joplin
      May 2011 render refl+vel fine, no CC).
    • **no suffix (pre-~2008, AR2V0001 / legacy Message 1)** — **NOW DECODED (single-pol: refl + vel,
      no CC).** The big surprise: the **vendored `nexrad-level-2-data` decoder already parses Message 1**
      (`Te` dispatches `case 1 → Le`, which fills `record.reflect`/`record.velocity` as FLAT value
      arrays + per-record range fields), so "write a Message-1 parser" was NOT needed — the work was
      a thin **normalization layer** plus accepting the keys. What landed:
        – **C# (`IsVolumeKey`)** now also accepts legacy keys `<ICAO><yyyyMMdd>_<HHmmss>(.gz)` (still
          rejecting `_MDM`). No new extraction path: legacy gunzips to FULLY-UNCOMPRESSED AR2V (no
          per-record bzip2, no Message-31 moment markers), so `TryExtractLowestTilt`'s first
          control-word read bails instantly → `EnsureCachedAsync`'s fallback caches the **whole
          gunzipped volume** (~14 MB) and the WebView decodes the lowest tilt. (So C# was genuinely
          ~"accept the keys, cache the full volume", exactly as planned.)
        – **JS (`radar-decode.js`)** got a `momentRadials(radar, moment)` helper that yields the unified
          `{ moment_data, first_gate(km), gate_size(km) }` shape for BOTH formats: Message 31 passes
          its moment object through; **Message 1** wraps the flat array with the record's range fields
          (`surveillance_range`/`surveillance_range_sample_interval` for refl, `doppler_range`/
          `doppler_range_sample_interval` for vel). `buildReflectivity`/`buildVelocity`/
          `buildCorrelation`/`findVelocityElevation`/the diag block all call it instead of
          `getHighres*()`. `nyquistForRadial` now also reads `record.nyquist_velocity` (legacy stores
          it on the record in m/s) when the Message-31 `record.radial.nyquist_velocity` (cm/s) is
          absent. Azimuth is read the same way for both (`getAzimuth` → `record.azimuth`). No version
          routing needed — shape detection (`Array.isArray(record.reflect)`) is enough.
        – **Replay year range** extended 2008 → **1991** (`PastEventStartYear`).
      Legacy is a **split cut**: reflectivity lives at elevation NUMBER 1 (surveillance, no velocity),
      velocity at NUMBER 2 (same 0.5° angle, no reflectivity) — so `Math.min(elevations)` picks refl,
      `findVelocityElevation` finds vel, exactly like the Message-31 split-cut path. Legacy is 1°
      azimuth × 360 radials (super-res didn't exist), so `HALF_BEAM_DEG=0.5` (1° quads) fits. CC is
      empty (single-pol). The bucket goes back to **~1993** for KTLX (Moore F5 1999, Greensburg 2007,
      Picher 2008 all present in legacy format); the **crossover is per-site ~2008** (KTLX flipped
      no-suffix→`_V03` mid-June 2008). **Verify each famous event against `tools/radar_reference.py`**
      (Py-ART) — a hand-mapped byte format can't be eyeballed for correctness.
      (Validated the format empirically on KTLX 2007-05-05: 5760 Message-1 radials, 16 elev × 360,
      elev1 = 460 refl gates @ 1 km decoding to sane dBZ, elev2 = 920 vel gates @ 0.25 km @ 0.5 m/s
      res; field offsets confirmed against the vendored `Le` parser's reader widths — `readSignedInt`
      is **2 bytes**, not 4.)
  (Deferred: famous-event presets, gap handling, the automated
  scoring layer. Known Phase-1 rough edge: the "Selected Site" card's live-only readouts — Updated /
  Next / freshness — aren't grayed in replay, so they read empty/`—`.) Planned to fold into the real
  drag/dock shell (`VSDragDockTest`) like the rest of the tool windows.
- **DOW Event Viewer (mobile-radar / Doppler-on-Wheels frames — right dock tool window). RENDER-VERIFIED**
  (first frame: **DOW8 2021-10-11**, Champaign IL — reflectivity geolocated correctly within the range
  ring, camera flies to the truck). A THIRD radar source beyond live + replay: a single
  curated **mobile-radar sweep** (DOW/COW/NOXP), rendered through the *same* `RadarLayer` pipeline
  (WebGL fill + real-extent range ring + product toggle + Inspect + color-scale legend), centred on the
  **truck's** position. **DOW data is NOT NEXRAD** — it's DORADE / CfRadial, which no in-app decoder
  reads, so the design keeps the app single-language (JS) + offline by treating conversion as **offline
  data curation** (like generating `radar-sites.json`):
    – **`tools/dow_import.py`** (Py-ART, dev-only, never ships) reads a DORADE/CfRadial sweep and emits a
      normalized **`dow-frame/1`** JSON: truck `lat/lon/alt`, swept `elevationDeg`, `band`+`nyquistMps`,
      true-north `azimuth[]`, and a `moments` table (Int16-quantized `values[]` per moment, row-major,
      `firstGateKm`/`gateSizeKm`/`scale`/`nodata`). **Velocity is PRE-DEALIASED with Py-ART** (the app's
      S-band-VAD dealiaser wouldn't behave on close-range X-band DOW velocity), so the app just colors it.
    – **`radar-decode.js` `decodeDowFrame(json, minDbz)`** turns that JSON into the SAME `{geom, velGeom,
      ccGeom, grids, rangeMeters, …}` result `decodeAndBuild` returns (dequantize → the `{moment_data,
      first_gate, gate_size}` + true-azimuth shape `buildGates`/`buildGrid` already consume; CC masked by
      reflectivity like NEXRAD; NO `dealiasSweep` — already dealiased). Synchronous (our own format, no
      vendored decoder).
    – **`radar.js` `RadarLayer.showDow(map, url)`** fetches the frame, sets the site to the truck pos,
      decodes on the main thread (one sweep, fast), and feeds it as frame 0 via the existing
      `applyFrameResult` (adopts first frame → adds layer → draws the range ring). `map.js` exposes
      `window.showDowFrame(url)` / `clearDowFrame()`.
    – **C#:** `IDowEventProvider`/`DowEventProvider` lists bundled `Assets/DowEvents/*.dow.json` (label
      from the file name) served from a new **`dowevents`** WebView host (mapped in `MainWindow`,
      `Directory.CreateDirectory`d so the empty-folder case never breaks the mapping). `IMapService.
      ShowDowFrameAsync`/`ClearDowFrameAsync`. `MapViewModel` DOW region (`DowEvents`/`DowEventIndex`/
      `DowProductIndex`/`DowStatus` + `LoadDowEventAsync`/`ClearDowEventAsync`). A right-dock **DOW Event
      Viewer** tool window (Event + Product combos, Load/Clear, status).
    – **`radar.js` showDow FLIES the camera to the truck** (`map.flyTo`, zoom 9) — a DOW deployment is a
      specific far-off spot, so unlike the NEXRAD loop it recenters or the sweep renders off-screen.
  **Data-format reality (the big gotcha — learned the hard way):** **no pure-Python library reads DORADE
  on Windows.** Py-ART's only DORADE path was the NASA RSL C-lib, now deprecated/unbuildable on Windows;
  xradar/wradlib read CfRadial/Sigmet/ODIM/IRIS but NOT DORADE. So the **VORTEX2 NOXP/DOW archives (EOL +
  the Zenodo NOXP record 14194361), which are DORADE-only, can't be converted on Windows without LROSE's
  RadX (C++, needs Docker/WSL)**. The unblock: the **openradar `open-radar-data` repo ships real DOW8
  sweeps already in CfRadial** (`cfrad.20211011_*_DOW8_PPI.nc`, direct GitHub raw download, Py-ART reads
  them natively) — that's the verified first frame. So for a DOW event today: prefer **CfRadial** sources;
  DORADE needs an offline RadX→CfRadial step first. **DOW8 field names** (for the `dow_import.py` MOMENTS
  table): refl=`DBZHC`, vel=`VEL`, width=`WIDTH` (also present: NCP/SNRHC/DBMHC/VS1/VL1; no ZDR/RHOHV →
  no CC). `dow_import.py` prints `fields present:` so unmapped names surface immediately. **DOW velocity
  is dual-PRF** (`VEL` already extended past the single-PRT Nyquist ~16 m/s), so **`--no-dealias` renders
  cleanly** — Py-ART's `dealias_region_based` is also painfully slow on DOW's fine-gate sweeps (use it
  sparingly). **Status:** verified for reflectivity + (raw) velocity, both clean.
  **Noise cleanup (two stages — and the lesson that NCP alone wasn't enough):**
    – **Converter NCP quality mask** (`dow_import.py --ncp-min`, default **0.2**; also `--snr-min`): nulls
      low-coherence gates via the `NCP` field. Helps, but **does NOT clean the velocity** — the dominant
      velocity speckle is **clear-air biological scatter** (insects — Oct over IL), which is coherent
      enough to pass NCP yet carries meaningless velocity.
    – **App reflectivity mask on velocity** (`radar-decode.js` `maskByReflectivity`, used in
      `decodeDowFrame`): the real fix. DOW velocity exists at EVERY gate, so — unlike NEXRAD velocity —
      it's masked to where reflectivity ≥ `MIN_DBZ`, range-aligned (same trick the app uses for CC).
      Velocity then matches the dBZ-thresholded reflectivity coverage and the clear-air disc of speckle
      disappears. (Diagnostic that nailed it: the reflectivity image was already clean at 10 dBZ, so the
      speckle had to be sub-10-dBZ gates that reflectivity hides but raw velocity colors.)
  **Known rough edges:** (1) the event label is filename-derived; (2) the "Selected Site"/live readouts
  don't apply to a DOW frame; (3) far-range bio-scatter that happens to exceed 10 dBZ would still show —
  bump the velocity dBZ floor if a future frame needs it.
  **Redistribution caveat:** a bundled `.dow.json` redistributes research data — use openly-licensed
  archives + carry the citation (DOW8 sample = NSF/EOL via open-radar-data; the Zenodo NOXP is CC BY 4.0).
  (1999 ROTATE-99 / Bridge Creek-Moore + Mulhall DOW data is NOT publicly archived — it'd need a direct
  CSWR/FARM request.) Verify a frame against a Py-ART plot of the same sweep when correctness matters.
  Also folds into `VSDragDockTest` later.
- **App settings (gear in the left dock's "Tools" header → Settings dialog).** `ISettingsService` /
  `SettingsService` persists settings in the packaged app's `ApplicationData.Current.LocalSettings`.
  Today it holds **`MapDataFolder`** — the folder mapped to the `mapdata` WebView host that contains the
  ~29 GB `usa_full.pmtiles` basemap (too big to bundle, so it stays external + user-supplied). The getter
  **defaults to a runtime-resolved Desktop** (`ResolveDefaultFolder` walks Desktop / OneDrive-Desktop /
  `%USERPROFILE%\Desktop`, picking the first that actually holds the file) — so there's **no hardcoded
  username path** in source and the basemap still loads on first run. `SettingsDialog` (a `ContentDialog`
  subclass, built to grow) has a folder display + Browse (`FolderPicker`, initialized with the window
  hwnd) + a found/not-found status; saving a new folder persists it and **prompts a restart** (the
  `mapdata` host is mapped once at startup in `InitializeWebViewAsync`; re-mapping live would need a page
  reload that re-runs `OnMapsReadyAsync`'s one-time startup loops, so restart is the clean path). Missing
  file = a dark basemap with overlays still drawing (no crash), not an error.

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
  all **removed** — and the leftover dead data has now been trimmed too: `RegionProvider` lists
  only CONUS, `map.js` no longer parses a `key` URL param (the page is a single map), and the unused
  record fields are gone — `MapRegion` is now just `{ Id, DisplayName, Longitude, Latitude, Zoom }`
  (dropped `InsetZoom` + the `MinZoom`/`MaxZoom`/`West`/`South`/`East`/`North` bounds), and `RadarSite`
  dropped `Zoom`.

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
in the AWS Level II bucket. (`RadarSite.Zoom` was removed — site clicks don't recenter, so the
model no longer carries it.)

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
  Capped at `LiveChunkCap` (**160**) chunks — raised from 90, which froze a SAILS-heavy VCP 212
  volume before its latest sweep's Doppler/velocity cut completed (a stuck partial-velocity wedge).
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
- **Radar site readout (now the right dock's "Selected Site" tool window; was a floating card):**
  shown while a loop is active, ticked 1 s by `RunDebugTickAsync`. A polished site readout — status dot (color from
  `MapViewModel.RadarStatus` / `RadarFreshness` Live/Recent/Stale by newest-frame age, mapped to a
  brush by `MainWindow.RadarDotBrush`), title (`RadarCardTitle`), current frame time + detail
  (`RadarCardTime`/`RadarFrameDetail`), `RadarModeText`/`RadarAgeText`/`RadarLoopSpanText` rows, and a
  **"Next" countdown + `ProgressBar`** toward the next live-frame poll (`RadarNextFrameProgress` 0-100 +
  `RadarNextFrameText`, from `_livePollCycleStart`/`_nextLivePollAt`). All those card props are raised
  together by `RaiseRadarCard()`. (A separate app-lifetime 1 s `RunProgressTickAsync` advances the
  next-update bars — the radar one here and the SPC one in the Outlook tool window — so they fill
  smoothly independent of the loop tick; the SPC schedule is pushed from `MainWindow` via
  `SetOutlookRefreshSchedule` each ~15-min cycle.) The card itself shows no developer
  diagnostics (the old "Diagnostics" `Expander` + the `RadarDebugText` blob were removed); all radar
  forensics now go to the dedicated **`Services.RadarDiagnostics`** service (the structured replacement
  for the deleted `RadarDebugLog`).
- **Radar diagnostics (`Services/RadarDiagnostics.cs`) — the primary radar debugging tool.** A static
  service (same no-DI pattern as the old log) that records every meaningful radar event from all three
  subsystems (`vm`/`svc`/`js`) as a **typed event**, correlated by a per-click **load-session id**
  (`sid`). It writes **per-run, launch-time-stamped** artifacts to a package-local `Diagnostics/`
  folder (`<Level2RadarService.CacheDirectory>\Diagnostics\`), **never auto-deleted or truncated** (the
  developer prunes; read the **newest** by filename):
  - `radar-diag-<yyyyMMdd-HHmmss>.jsonl` — **source of truth**, one JSON event per line (append-only,
    crash-safe). Envelope: `ts, seq, sub, cat, site, sid`. `Init(...)` is called in the `MainWindow`
    ctor; a background task flushes + regenerates the report every ~2 s, and `MainWindow.Closed` does a
    final `FlushAll`.
  - `radar-report-<stamp>.md` — derived rolling summary, **rewritten to be self-explanatory for a
    non-expert** (`RenderReport`): a plain-English **health verdict** (`OK`/`WARNINGS`/`PROBLEMS`) +
    one-line summary at the top, a **glossary** of every term, run aggregates each annotated with a
    plain verdict ("typically 607 ms (looks fine)") alongside the raw `[n/min/p50/p95/max]` quantiles,
    the current session in prose + a per-frame table with a plain **Status** column ("ok" / "ok (live)"
    / "partial sweep" / "empty"), and a **Problems & warnings** section in plain language. The machine
    log also carries a `lvl` (`warn`/`error`) on suspect frames + render errors so the JSONL is
    severity-filterable.
  - `_suspect\<stamp>\` — a **suspect frame's source `.V06` is quarantined** (copied aside) so the cache
    prune can't delete the evidence. Suspect heuristics (in `JsFrame`): empty, refl partial sweep
    (<330°), vel wedge (<300°), zero vel gates, high dealias `hi` ratio.
  - **Event sources:** VM via typed methods (`BeginSession`, `Timing`, `FrameReady`, `LivePoll`,
    `RegisterFrameSource`) + the generic `Log("vm", cat, …)`; `Level2RadarService` via `Log("svc",
    "live"|"sweep"|"extract", …)`; and `radar.js` via **structured WebView messages** routed by
    `MainWindow` — `radarFrame` (per-frame decode metrics: `reflStats`/`velStats` `{rad,azLo,azHi,span,
    gates}`, `velNyq`, `dealias`, decode/build ms → `JsFrame`), `radarRender` (blank/error/recovered/
    ctxlost → `JsRender`), and free-form `radarLog` → `JsLog`. The metrics were already computed by the
    decoder; the JS just forwards them losslessly instead of stringifying one log line.
  - High-value JSONL greps: `cat:"frame"` for per-frame metrics + the `suspect` field; `cat:"sweep"`/
    `"extract"` for live cut selection / archive single-tilt extraction; `cat:"timing"` for load times;
    `cat:"live.poll"`/`"live.apply"` for freshness/cadence. The cached `.V06` are decompressed
    single-tilt buffers, so `grep -a -o DREF|DVEL <file> | wc -l` still counts refl/velocity radials.

## Next steps

**Velocity product — DONE (refl + velocity render across the whole loop).** Base velocity at 0.5°
lives in the Doppler (CD) companion of the split-cut surveillance (CS) reflectivity cut — same
**angle**, next elevation **NUMBER** (verified during the SAILS work). The companion is written AFTER
the CS so the CS stays the lower number and the JS `Math.min(elevations)` still picks it for
reflectivity, while `findVelocityElevation` finds the higher-numbered Doppler for velocity; clear-air
VCPs carry velocity in their single combined cut (no companion). Decode: `radar-decode.js` shares a
`buildGates(radials, …, colorFn)` between `buildReflectivity` and `buildVelocity`; gate colors come
from the shared `Assets/Map/js/radar-ramps.js` (see "Color ramps" below); `decodeAndBuild` returns
`{geom, velGeom}` with baked colors so the host toggles product with no re-decode. `radar-worker.js` posts both geoms zero-copy; `radar.js` stores both per frame,
tracks `product`/`uploadedProduct`, renders the selected moment (re-uploads on product switch — and
only latches `uploadedFrame`/`uploadedProduct` once an upload actually happened, so a frame lacking
the active moment can't leave stale buffers marked current). UI: `window.setRadarProduct` → `RadarLayer.
setProduct` → `IMapService.SetRadarProductAsync` → `MapViewModel.RadarProductIndex` (0=refl, 1=vel,
2=cc), bound to the Radar Loop tool window's **Product** combo.

**Correlation Coefficient (CC / ρHV) product — DONE.** A third product, added via the same path as
velocity but simpler (single dual-pol moment, **no folding/dealiasing**). The cached single-tilt `.V06`
keeps all moment blocks (`DRHO`/`DZDR`/`DPHI` verified present, 720 radials each), so CC is decoded
directly. `radar-decode.js` `buildCorrelation` reads CC from the **surveillance cut** (lowest elevation
NUMBER, alongside reflectivity — NOT the Doppler cut) via `getHighresCorrelationCoefficient`, colored by
`CORRELATION_RAMP`; `decodeAndBuild` returns a third geom `ccGeom`; `radar-worker.js` posts it zero-copy;
`radar.js` stores `cc*` per frame and the render/product-toggle select among refl/vel/**cc**. **CC is
MASKED BY REFLECTIVITY** (`buildCorrelation` keeps a CC gate only where the co-located refl gate is
≥ `minDbz`, aligned by RANGE since CC/refl gate geometry can differ) — without the mask, clear-air
clutter/biological/noise carry real-but-random low CC and paint the whole domain with colorful speckle
(RadarScope masks it identically; verified against RadarScope — our precip cores matched, the speckle was
the only diff). The same refl-mask will apply to ZDR/Differential Phase when those are added. **Products
are being proven one at a time; TILTS come after all products are proven.**

**Color-scale legend ("Color Scale" tool window, right dock) — DONE (was "future").** The active product's
ramp drives an honest, generated legend — never hard-coded. `map.js` eager-imports `radar-ramps.js` and
**pushes the active product's ramp** (`{type:"radarRamp", ramp}`) to the host on load + on every product
switch; `MainWindow` deserializes it to `Models/RadarRampInfo` and `MapViewModel.SetColorScale` exposes it
(`CurrentRamp` + `RampTitle`/`RampMin/Mid/MaxText`, `HasColorScale` = ramp ∧ `HasRadarLoop`). The bar is
`MainWindow.RampBrush(CurrentRamp)` — a `LinearGradientBrush` built from the ramp's exact stops: smooth
for interpolated ramps (velocity/CC), hard NWS bands for discrete (reflectivity, via duplicated stops at
each boundary). Because it's generated from the same stops that color the gates, the legend can't drift
from the pixels; retuning a ramp in `radar-ramps.js` updates the legend automatically.

**Inspector ("read the value under the cursor", RadarScope-style) — DONE.** An **Inspect** toggle
in the Radar Loop tool window (`MapViewModel.IsInspecting` → `IMapService.SetRadarInspectAsync` →
`window.setRadarInspect` → `RadarLayer.setInspect`) puts the map in a crosshair mode where a `mousemove`
reads the active product's value at the pointer and shows it in a DOM tooltip beside the cursor (drawn
in the WebView, so it's instant — no host round-trip per move). The lookup is a pure main-thread array
read: `radar-decode.js` `buildGrid` emits, alongside the baked-color geometry, a compact **polar value
grid** per product (`az` Float32 + an **Int16** quantized `values` array, `scale`/`unit`/`digits`
carried), transferred zero-copy by `radar-worker.js` and stored per frame in `radar.js`
(`frames[i].grids`). `lookupValue` projects the cursor lng/lat back to the site's polar frame with the
SAME equirectangular math `buildGates` uses (so the inspected gate is exactly the painted one), indexes
range→gate + nearest-radial-by-azimuth, and de-quantizes. Grids are the RAW values (NOT
thresholded/masked — refl reports true dBZ below `MIN_DBZ`; CC uses the unmasked ρHV; velocity uses the
DEALIASED radials to match the pixels). **Bonus — live color-scale marker:** `radar.js` also pushes
`{type:"radarInspect", has, value}` to the host (throttled ~14/s); `MainWindow` routes it to
`MapViewModel.SetInspectValue`, which maps the value to a 0-1 fraction along `CurrentRamp` and the Color
Scale tool window draws a tick on the gradient bar (positioned by a 3-column star grid via
`MainWindow.InspectLeftStar/InspectRightStar`) plus the formatted value. (Memory note: the grids add
~Int16 N×G per product per frame, kept for all loop frames so inspecting stays instant during
scrub/playback + product switches; could be made lazy if it ever matters.) Inspect auto-clears when the
loop is cleared. **Velocity dealiasing check (the "let me confirm high velocities" tool):** on the
Velocity product the inspect tooltip shows a second line — `raw <v> · Nyq <N> · <±k> folds` — the SAME
gate's RAW (folded) measurement + Nyquist + fold count, derived from the dealiased value (`velocityFold`
in `radar.js`: dealiasing only ever adds whole 2×Nyquist multiples, so `raw = dealiased − round(dealiased
/(2N))·2N` is exact; `velNyq` is stored per frame). This automates the same-gate raw-vs-dealiased
comparison in ONE hover — no toggling products or re-aligning the cursor — so a suspicious unfold (e.g. a
far-range sign flip) is verifiable at a glance. (For absolute ground-truth, an independent dealiaser like
Py-ART is still the rigorous external check.)

**Both data paths keep the Doppler cut (this was a multi-bug saga — see history below):**
- **Live frame** (`SelectLatestSweep`): selects a surveillance cut + its **paired Doppler companion**,
  matched by **same angle + has velocity** (NOT reflectivity — a short-PRT Doppler's range-folded
  reflectivity can fail the `HasMoment` gate-count check). It **prefers the latest surveillance whose
  Doppler is a COMPLETE, full-circle sweep** (`requireComplete` first pass, then a partial fallback so
  the newest frame never goes blank). Chasing the absolute-freshest SAILS re-scan instead served a
  still-scanning Doppler — a frozen ~90° quarter-circle **wedge** once the chunk cap stopped the volume
  growing. `LiveChunkCap` raised 90→**160** so a SAILS-heavy VCP 12/212/215 volume isn't frozen
  mid-scan before its latest sweep's Doppler completes.
- **Archive frames** (`TryExtractLowestTilt`): keeps the lowest tilt by **ANGLE, not elevation number**
  (a split-cut's CS=elev1 and CD=elev2 share the 0.5° angle; the old "stop at number ≥ 2" dropped the
  CD = no velocity on every archive frame). Robustness: (a) the stop is evaluated only at an
  **elevation-NUMBER increase** so a stray `ElevationAngleOf` float misread mid-cut can't truncate the
  sweep to ~one block (the intermittent ~120-radial blank frames); (b) the base-tilt angle is the
  **MAX over the base cut's radials**, because the first radial of a cut is a settling radial that read
  e.g. 0.27° for a true 0.5° tilt — anchoring on it made the keep-threshold too tight and dropped the
  Doppler. Over-keeping a higher tilt is harmless (decode picks refl from min elev, vel from the first
  velocity cut); dropping the Doppler is not — so the bias is toward keeping.

**Velocity is DEALIASED** in `radar-decode.js` `dealiasSweep` before coloring (base velocity wraps at
±Nyquist). Approach: (1) flood-fill the sweep into connected regions where neighbouring gates differ
by < Nyquist (no fold inside a region); (2) union regions strongest-boundary-first (a max-spanning-tree)
with a relative fold offset, unfolding a boundary only when its jump is genuinely near a multiple of
2·Nyquist (real gradients/shear are left alone) and never folding a tiny noise region; (3) **VAD
absolute anchor** — at each range ring fit the environmental-wind sinusoid `v(az)=c0+c1·cos+c2·sin`
robustly (unfold-toward-fit, iterated), seeded OUTWARD from the unaliased near range, then snap each
region to the whole 2·Nyquist nearest that fit (only when the region's mean is *clearly* a fold off, so
genuinely strong features aren't flattened); falls back to a global-mean anchor on too-sparse sweeps.
The VAD step is what fixed the **far-range sign flips** (strong outbound rendered as strong-inbound
purple) that pure region-continuity couldn't resolve. **Gotcha:** Nyquist comes from the decoder's RAD
block in **cm/s** — divide by 100 (2584 → 25.84 m/s); using it raw made the unfold interval 100× too
big so nothing ever unfolded. Debug log: `deal <N>reg seedMean<x> vad<ringsFit>/<rings> v[min,max]
hi=<#>/<#>` — a healthy frame keeps `v` bounded (~±55) with `hi` (gates >55 m/s) near 0.

**Color ramps — single source of truth** in `Assets/Map/js/radar-ramps.js`, imported by the decoder AND
the legend so they can't drift: `REFLECTIVITY_RAMP` (discrete NWS dBZ bands), `VELOCITY_RAMP` (smooth
diverging m/s — pure green inbound → cyan/blue/magenta when strong; gray at zero; pure red outbound →
pink/white when strong; deliberately **no orange/yellow**), `CORRELATION_RAMP` (smooth ρHV 0.2–1.05;
low CC = cool gray→purple→blue→cyan so clutter/debris stands out, precip = warm green→yellow→orange→red→
pink, above-unity = white), and `rampColor(ramp, v)`. Each ramp carries `{label, unit, min, max,
interpolate, stops}`. **The legend bar IS now built** — the "Color Scale" tool window (see "Color-scale
legend" above) renders each ramp from these exact stops, fed by the WebView pushing the active ramp to
the host, so the bar always matches the rendered pixels.

**⚠️ Velocity dealiasing — "good enough for now"; mark for an eventual real fix (LOW PRIORITY, only if
visibly wrong frames show up in the app).** The self-VAD anchor assumes the wind is ~uniform in azimuth
at each range ring (horizontally homogeneous) — solid for broad/stratiform precip, weaker where flow
varies sharply across the domain (isolated supercells, a front cutting through, strong mesocyclones),
where one sinusoid can't describe a whole ring and a region may still snap to the wrong fold. We are
**satisfied with the current result**; the proper fix is an **external first guess** — HRRR/RAP model
winds (or a sounding, or the previous dealiased volume) as the reference instead of the self-derived
sinusoid — used when a network is available and falling back to self-VAD offline (preserving the
offline-first guarantee). **Storm-relative velocity** (SRM = base − storm-motion vector) is a separate
later feature.

**Radar refl/velocity validation — DONE then the tooling was REMOVED (findings kept).** A dev-only
regression harness (Ctrl+Shift+V; an in-app replay of a committed historical-volume corpus through the
**real** decode/dealias path, scored to a report) was built to answer "are refl/vel robust?", run, and
then **removed at the user's request** (it was `RadarValidationService` + `Assets/radar-corpus.json` +
`window.radarValidate` + a `Ctrl+Shift+V` hatch; all reverted — restore from git if ever wanted). The
**verdict is worth keeping**:
- **Reflectivity: PROVEN (8/8)** across clear-air, broad-stratiform, precip/SAILS, supercell,
  extreme-velocity, and tropical regimes.
- **Velocity: 6/8 — self-VAD at its ceiling.** Pass = complete sweep + bounded + low over-unfold ratio
  (gates > 55 m/s; the ratio is the real fold signal, an absolute ±150 m/s is just a sanity catch).
  **Encouraging:** the classic supercell cases once feared — **Moore 2013 and El Reno 2013 — dealias
  cleanly** (< 0.05% over-unfold); self-VAD is robust on severe-weather couplets. **El Reno–Piedmont
  2011 (the 2011-era `_V03`/AR2V0003 super-res file, refl+vel, no dual-pol) also visually validated**
  in-app against the famous Wikipedia GR2Analyst image: correct couplet location (W of Piedmont near
  Okarche), inbound (green→cyan→blue) and **extreme outbound (pink/white) on the correct sides, NO sign
  flip**, extremes correctly folded+unfolded — confirmed gate-by-gate with the new velocity-fold
  inspector readout. (Color-convention trap when comparing to other apps: in many velocity tables
  **magenta = extreme outbound**, but in our ramp magenta = extreme INBOUND and extreme outbound =
  **pink/white** — compare STRUCTURE, not hue.) **Two genuine failures, distinct root causes:**
  - **KTLX 2024 (modern split-cut SAILS supercell):** the Doppler cut is sparse/range-folded → only ~42%
    of range rings get a VAD wind fit and the sweep shatters into thousands of regions; far-range regions
    have no reliable anchor and drift to a wrong fold (`+130 m/s`). **A VAD gap-fill (interpolate the wind
    across unfitted rings) was tried and REVERTED — it made this ~3× WORSE**: the few fitted rings are
    themselves noisy, so spreading them gave a *confidently-wrong* anchor. Information-poverty, not anchor
    placement — no self-VAD variant fixes it.
  - **KHGX Harvey 2017 (tropical):** good VAD coverage but winds sit **at Nyquist everywhere**, so a fold
    is mathematically ambiguous from self-VAD alone.
- **Both point to the same fix:** an external/temporal first guess (HRRR/RAP winds or the previous
  dealiased volume), now **data-justified**, for when velocity is next prioritized.
- **External ground-truth tool (`tools/radar_reference.py`) — BUILT.** A standalone Python script
  (Py-ART, the scientific-standard radar toolkit) that downloads the SAME volume from the same AWS
  bucket and renders the lowest tilt's reflectivity + **dealiased velocity** to PNGs — an INDEPENDENT
  decoder to check the app against (Py-ART reads both modern Message-31 AND legacy Message-1). Usage:
  `py -3.12 radar_reference.py SITE YYYY-MM-DD HH:MM` (UTC); see `tools/README.md` for the one-time
  setup (`pip install arm_pyart matplotlib`; needs Python ≤3.13 — the science wheels lag the newest
  Python). It does NOT touch the C# app. **Cross-validated Moore 2013 (KTLX, 20:20 UTC): reflectivity
  matched EXACTLY (confirms decode + gate geometry are byte-correct), and the dealiased-velocity field
  matched Py-ART's independent dealiaser** — so velocity dealiasing is validated against the gold
  standard, not just self-consistency. This tool is the **answer key for the legacy (pre-2008
  Message-1) decoder** (now implemented — see "Archive formats" above) — a byte-mapped format can't be
  eyeballed for correctness, so each legacy event (Moore 1999, Greensburg 2007, Picher 2008…) should be
  verified against it.
  - **Known residual (minor):** on a **non-SAILS / clear-air** site, if the *first* live poll lands
    during the single Doppler's ~15 s scan and no earlier complete pair exists, it serves a partial
    wedge that the same-timestamp "not newer" skip in `ApplyLiveFrameAsync` won't upgrade once the
    Doppler completes (shows in the log as `vel=…PARTIAL` then `live skip` at the same `t=`). Rare now
    (complete-preference + bigger cap). Clean fix when it bites: a small "velocity complete" flag on
    `RadarVolume` so a same-timestamp completeness upgrade re-applies (also needs the live cache file
    rewritten on upgrade — `BuildLiveFrameAsync` currently won't overwrite an existing same-`ts` file).
  - **Backfill cost:** carrying the Doppler ~doubles each archive `.V06` (~7→~10 MB), so a full-loop
    backfill is ~28 s vs ~15 s (newest frame still paints in ~3 s). Lever if it matters: build velGeom
    lazily (only when the user is on the Velocity product). **Incremental loop reload — DONE (no more periodic blank).** The ~5-min archive refresh used to call
the full-rebuild path (`beginLoop` → `removeLayer` + re-decode all ~11 frames), which **blanked the
radar for ~1.5-6 s and flashed a stale archive frame every reload** (verified in the diagnostics:
`layer removed`→`layer added` gaps of 1.5-6.4 s, then the ~4-min-old archive-newest shown before the
live frame caught up). `RunRefreshAsync` now calls **`ReloadLoopIncrementalAsync`** instead: it diffs
the new key list against `_loadedKeys`, reindexes the unchanged frames **in place** (reusing their
decoded geometry, the live frame carried over) and decodes only the genuinely-new volume(s). The JS
side is `RadarLayer.remap(map, newCount, mappingJson)` (`window.radarRemap` shim → `IMapService.
RemapRadarFramesAsync`): it bumps `loopToken`, rebuilds `frames[]` from `[from,to]` index pairs **without
removing the layer**, and keeps the on-screen frame up (same geometry, no re-upload) — so a reload is
seamless. The host follows the newest via the existing `pendingFrame` machinery (an undecoded just-
arrived newest is shown only once it decodes; the current frame stays up meanwhile). `IsLoopReady`
isn't reset (most frames stay decoded → scrubber/playback uninterrupted); `_readyCount` = reused count
and the new frames bring it back up. The full-rebuild path still runs on a site click / manual reset.
(Diagnostics nuance: the per-index frame table in the report doesn't re-map reused frames' old metrics,
so reused rows can read one-shift stale until re-decoded — aggregates are unaffected.) **Radar (other):**
a **searchable / decluttered** site picker (160 markers is busy); proper
dual-pol clutter filtering (CC-based) instead of the blunt `MIN_DBZ`. The chunks-bucket live frame is **done** (see "Radar live frame");
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

**Cleanup — DONE.** The `mapdata` Desktop path is de-hardcoded into `ISettingsService`, and all the
dead inset/vestigial data is trimmed: `RegionProvider` lists only CONUS; `MapRegion` dropped
`InsetZoom` + the unused `MinZoom`/`MaxZoom`/`West`/`South`/`East`/`North` bounds; `RadarSite` dropped
`Zoom`; and `map.js` no longer parses the `key` URL param.
