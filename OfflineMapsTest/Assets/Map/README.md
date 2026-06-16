# Assets/Map — the web layer (the only non-C# code in this app)

Everything in this folder is served to the app's single **WebView2** under the virtual host
`https://mapassets/` (mapped from this directory by `MainWindow`). It exists because the map
is **[MapLibre GL JS](https://maplibre.org/)** — a JavaScript library that runs in the
browser engine and renders via **WebGL**. That work *cannot* be done in C#: it has to run
inside the WebView against the browser's JS/WebGL/Web Worker APIs. So this folder is, by
design, the project's irreducible non-C# surface. Everything that *can* be C# is C#
(see `../../Services`, `../../ViewModels`, `../../Models`).

## Layout — what's ours vs. borrowed vs. data

| Path | What | Language-stats |
|------|------|----------------|
| `map.html` | Page shell loaded by the WebView (`mapassets/map.html`). | counted |
| `map.js` `radar.js` `radar-decode.js` `radar-worker.js` | **Our authored JS** — the code we had to write in JS. ~34 KB total. | counted |
| `vendor/` | **Third-party libraries**, vendored verbatim (not our code). ~1.2 MB. | `linguist-vendored` (excluded) |
| `style*.json` (basemaps, at root), `fonts/`, `sprites/` | **Data/config**, not code. | n/a |

`vendor/` is marked `linguist-vendored` in the repo-root `.gitattributes`, so the GitHub
language bar reflects the ~34 KB of JS we actually wrote, not the ~1.2 MB of libraries.

## Our authored JS — why each file must be JS

- **`map.js`** — boots the single MapLibre map from URL params, registers the `pmtiles://`
  protocol, and exposes the `window.*` command shims the host drives. Pure WebView/MapLibre.
- **`radar.js`** — the NEXRAD radar **WebGL custom layer** (dBZ ramp) + frame store + render
  loop. Custom GPU rendering ⇒ must be JS. The single heaviest reason this folder exists.
- **`radar-decode.js`** — bzip2-decodes a single-tilt `.V06` and builds the gate geometry.
  Runs in the WebView (and the worker); the host already does the *fetch/extract* in C#.
- **`radar-worker.js`** — a Web Worker that runs the decode off the UI thread. Worker ⇒ JS.

## The C# ↔ JS contract (one seam)

C# never reaches into the page's internals. The boundary is exactly one pair:

- **C# side:** `Services/MapService.cs` builds `window.fn(args…)` strings and dispatches them
  through `IMapView.RunScriptAsync` (implemented by `MainWindow`). Messages come back via
  `CoreWebView2.WebMessageReceived` (`mapReady`, `radarSiteClick`, `radarFrameReady`,
  `radarLog`).
- **JS side:** `map.js` / `radar.js` register the matching `window.*` handlers and `post(...)`
  those messages.

If you add a command, it's one method in `MapService` + one `window.*` handler here — that
1:1 mapping *is* the API.

## Adding new overlays — prefer C#, not new JS

The app is offline-first and we want to keep this folder small. MapLibre is data-driven, so
**most overlays should be built in C# as data** (GeoJSON + a layer/paint spec) and applied
through generic JS primitives — *not* a new bespoke JS function per feature. Only reach for
new JS when a feature needs **custom GPU rendering** (like the radar layer); standard
polygons/lines/points/rasters/heatmaps don't.

## Vendoring the decoder (offline, no build step)

`vendor/nexrad-level-2-data.esm.js` + `vendor/seek-bzip.esm.js` are jsDelivr `+esm` bundles
downloaded with `curl` (no Node/bundler). **Two hand-patches** must be re-applied if either is
re-downloaded (see the repo-root `CLAUDE.md` "Vendoring the radar decoder" for the canonical
notes):
1. the decoder's `import "/npm/seek-bzip@2.0.0/+esm"` is rewritten to `"./seek-bzip.esm.js"`
   (both files live in `vendor/`, so that relative import stays correct);
2. `,_ as Buffer` is appended to its final `export{…}` so `radar.js` can wrap input with the
   decoder's *own* `Buffer` (its constructor rejects any foreign Buffer — "Unknown data
   provided").

`maplibre-gl.js`/`.css` (v5.21.1) and `pmtiles.js` are likewise vendored verbatim.
