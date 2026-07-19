# Anvil

**Severe-weather workstation for Windows.** Decodes NEXRAD Level II from raw base data, live or
replayed from the archive back to 2008, and GPU-renders it over local, fully style-controlled vector
basemaps, with SPC outlooks, watches, and DOW mobile-radar frames.

Anvil reads raw WSR-88D Level II volumes, decodes the Message 31 base data itself, and renders every
gate on the GPU. No server-side rendering, no image tiles. The basemap is a local PMTiles archive
instead of a tile service, so you control the styling and panning costs nothing.

<img width="3840" height="2088" alt="KTLX reflectivity from the May 24 2011 Oklahoma outbreak, replayed from the archive with the Past Event Viewer" src="https://github.com/user-attachments/assets/d91cf04c-e649-4f9b-80c2-3ffc1f314334" />

## Status

Active development, and likely to stay that way for a year or more. There are no tagged releases and
no stability guarantees. The UI is mid-rebuild: several capabilities exist in the view models ahead of
the controls that expose them, so what the app can do is currently ahead of what you can reach on
screen.

**Not for operational use.** This is a personal project for exploring radar data. Use official NWS
products and your local warnings for any safety-of-life decision.

## What it does

### Radar

- **Real Level II decode.** Reads the raw `.V06` volume directly: Message 31 radial headers,
  per-moment data blocks, VCP and elevation tables.
- **Six products.** Reflectivity, Velocity, Correlation Coefficient, Differential Reflectivity,
  Specific Differential Phase, and Spectrum Width. Reflectivity uses the standard NWS discrete dBZ
  band scale. The rest use ramps built for this app (`docs/radar-products-history.md`).
- **Velocity dealiasing.** A port of the Py-ART region-based algorithm, validated against Py-ART
  itself (`docs/velocity-dealias.md`).
- **Tilt selection.** Elevation cuts resolve from the volume's own elevation table, handling SAILS
  re-scans and split-cut Doppler companions (`docs/radar-tilts.md`).
- **Live loop.** Recent volumes from the AWS archive, plus a near-real-time frame assembled from the
  chunks bucket that cuts the usual 10 minute archive latency
  (`docs/radar-live-frame-internals.md`).
- **Past Event Viewer.** Pick a site, a date back to 2008, a start time, and a window from 30 minutes
  to 12 hours. Scrub or play it through the same pipeline as live (`docs/past-event-viewer.md`).
- **Inspector.** Reads the decoded value under the cursor and marks it on the color scale.
- **Site coverage.** The full WSR-88D network, with TDWRs and research radars as optional layers.
- **DOW frames.** Curated Doppler On Wheels mobile-radar frames render through the same path
  (`docs/dow-event-viewer.md`).

### Severe weather overlays

- **SPC outlooks.** Convective and fire weather, Days 1 through 8, with probability fills and per-CIG
  significant-area hatching. Nested groups are clipped so lower hatch does not show through the
  higher group's gaps.
- **Watches.** Tornado and Severe Thunderstorm watch areas, county-aggregated from the NWS
  watch/warning/advisory service.

### Basemap

Five styles ship with the app: Regular, Dark, Data Viz Light, Data Viz Black, and Data Viz Grayscale.
All read from one local PMTiles archive. Because the tiles and the style JSON are both local, a
cartography change is a file edit rather than a service request.

## Requirements

| | |
|---|---|
| OS | Windows 10 version 1809 (build 17763) or later. Windows 11 recommended. |
| SDK | .NET 8 and Windows App SDK 2.1.3 |
| IDE | Visual Studio 2022 with the Windows App SDK workload |
| Runtime | WebView2. Preinstalled on Windows 11; on Windows 10 install the Evergreen runtime. |
| Basemap | A PMTiles archive. See [Basemap data](#basemap-data) below. |

Visual Studio is effectively required to run Anvil, not just to build it. The app is packaged as MSIX
and depends on package identity for its local caches, so it has to be deployed rather than launched
from a loose executable. Building from the command line works. Running that way does not.

## Basemap data

Anvil ships without a basemap. The archive runs to tens of gigabytes, so it is not in the repo. Set
this up before the first run.

Without it the app still launches and every weather overlay still draws, but the map underneath is
black. That looks like a broken build rather than missing data.

Anvil reads a single [PMTiles](https://protomaps.com/docs/pmtiles) archive built from the
[Protomaps basemaps](https://github.com/protomaps/basemaps), which use OpenStreetMap data. All five
bundled styles point at the same file:

    pmtiles://https://mapdata/usa_full.pmtiles

### 1. Build an archive

Protomaps publishes daily planet builds. Extract only the region you need with the
[`pmtiles` CLI](https://github.com/protomaps/go-pmtiles):

```sh
pmtiles extract https://build.protomaps.com/<YYYYMMDD>.pmtiles usa_full.pmtiles \
  --bbox=-125.0,24.0,-66.5,49.5 --maxzoom=12
```

Max zoom drives file size far more than the bounding box does. A CONUS extract is a few gigabytes at
zoom 12 and tens of gigabytes at full detail. Check the Protomaps documentation for the current build
URL, which has changed over time.

### 2. Name it `usa_full.pmtiles` and put it on your Desktop

Both parts matter right now.

The filename is hard-coded in all five bundled styles and in `SettingsService.MapDataFileName`.

The location matters because the in-app folder picker is one of the controls not yet wired back into
the current UI. Until it returns, Anvil resolves the folder itself, checking in order:

1. Your Desktop
2. `%USERPROFILE%\OneDrive\Desktop`
3. `%USERPROFILE%\Desktop`

Put the archive in any of those and Anvil finds it with no configuration.

To keep it somewhere else, edit `ResolveDefaultFolder` in `Anvil.App/Services/SettingsService.cs`. To
use a different filename, change `MapDataFileName` in the same file and the `url` field in each
`style*.json` under `Anvil.App/Assets/Map/`.

## Build and run

```sh
git clone https://github.com/jhammon88219/Anvil.git
cd Anvil
dotnet build Anvil.App/Anvil.App.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

The platform must be explicit. Plain AnyCPU fails during MSIX packaging.

To run Anvil, open `Anvil.slnx` in Visual Studio and press F5, which deploys the package. Running the
built `.exe` directly throws. It has no package identity that way, and the app keeps its caches in
package-local storage.

`Anvil.Core`, `Anvil.Tests` and `tools/TiltCheck` target plain `net8.0` and take no platform
arguments, so a change confined to Core can be checked without paying for MSIX packaging:

```sh
dotnet build Anvil.Core/Anvil.Core.csproj
```

The solution also builds as a whole with `dotnet build Anvil.slnx -c Debug -p:Platform=x64`. x86 and
ARM64 configurations exist, but x64 is what the build recipes use.

If the map is black on first launch, the basemap archive is missing or misnamed. See
[Basemap data](#basemap-data).

## How it's put together

Four projects. The boundary between them is enforced by the target framework rather than by
convention:

| Project | Target | Contents |
|---|---|---|
| `Anvil.Core` | `net8.0` | Models, services, view models. No Windows dependency. |
| `Anvil.App` | `net8.0-windows` | WinUI 3 shell, XAML, the web layer, three platform-bound services |
| `Anvil.Tests` | `net8.0` | xunit suite over the decoder |
| `tools/TiltCheck` | `net8.0` | Runs tilt extraction against a real volume from the archive |

`Anvil.Core` stays free of Windows APIs deliberately. Its only runtime dependency is SharpCompress.
That keeps the console tools able to reference it directly, and keeps the test suite running in about
a second without deploying anything. Three implementations genuinely need WinRT, and all three live in
`Anvil.App` behind interfaces declared in Core: geolocation, settings storage, and UI thread dispatch.

Both projects use `Anvil` as their root namespace rather than `Anvil.Core` and `Anvil.App`, so moving
a file between them is a move and nothing else.

### The C# and JavaScript split

The map is [MapLibre GL JS](https://maplibre.org/) in a single WebView2, because WebGL rendering
cannot happen in C#. That web layer is the only non-C# surface in the project and it is kept narrow:
an orchestrator plus one module per overlay, with radar decoding split into a worker. Everything that
can be C# is C#.

Traffic across the boundary runs through two seams:

- **C# to JavaScript.** `MapService` builds command strings and dispatches them through `IMapView`.
  `MainWindow` is the only class that touches WebView2 at all.
- **JavaScript to C#.** `WebMessageRouter` receives posted messages and routes them to the view
  models.

Services never know WebView2 exists. Details in
[`Anvil.App/Assets/Map/README.md`](Anvil.App/Assets/Map/README.md).

### MVVM

Hand-wired in the `MainWindow` constructor. No DI container. `MapViewModel` owns basemap style and
region, and builds the subsystem view models beneath it: radar, outlooks, watches, markers, and the
site explorer. The radar view model is the large one and is split into partial files by facet.

## Tests

```sh
dotnet test Anvil.Tests/Anvil.Tests.csproj
```

About a second, with no deployment. Level II volumes are synthesized in memory: a file header, bzip2
LDM records, and hand-built Message 31 radial headers. The suite needs no multi-gigabyte fixture and
no network.

Coverage centers on the decoder, where mistakes are quiet and expensive: cut-angle matching, Doppler
companion attachment, completion semantics, and the byte scanners underneath them. It is a starting
point rather than broad coverage.

Regression tests are validated by mutation. After writing one, the code it guards is temporarily
broken to confirm the test actually goes red. The median-angle test was checked that way: reverting
the cut-angle rule to the original bug turned that one test red and left every other test green. A
regression test never observed failing is not evidence of anything.

`tools/TiltCheck` covers what unit tests cannot. It runs the same extraction against a real volume
pulled from the archive and reports what each designed tilt actually yielded:

```sh
cd tools/TiltCheck
dotnet run -- KTLX 2025/07/15 18
```

## Documentation

`docs/` holds one file per area: radar tilts, velocity dealiasing, the live frame path, product
history, decode validation, the past event and DOW viewers, radar site lists, and a running notes
file. Each is the deep reference for the code it describes rather than a summary of it.

`CLAUDE.md` at the repo root is the working map of the codebase: where things live, how to build, and
the gotchas worth knowing before changing anything.

## Data sources

| Data | Source |
|---|---|
| NEXRAD Level II | Unidata's archive and chunks buckets on AWS |
| Convective outlooks | [NOAA/NWS Storm Prediction Center](https://www.spc.noaa.gov/) |
| Fire weather outlooks | NWS map services |
| Watches | NWS watch/warning/advisory map service |
| Basemap | [Protomaps](https://github.com/protomaps/basemaps), © [OpenStreetMap](https://openstreetmap.org) contributors |

The bundled DOW sample (`dow8_2021-10-11.dow.json`) comes from NSF NCAR EOL by way of the
open-radar-data archive. Any `.dow.json` you add redistributes research data, so use openly licensed
archives and carry the citation.

Built with [MapLibre GL JS](https://maplibre.org/), [PMTiles](https://github.com/protomaps/PMTiles),
the `nexrad-level-2-data` decoder, seek-bzip, and SharpCompress.

Anvil is an independent project. It is not affiliated with, endorsed by, or supported by NOAA, the
National Weather Service, Unidata, or NSF NCAR.

## About this project

Anvil is self-taught work. I have no formal background in meteorology, radar engineering, or
software development, so expect some non-standard choices. Corrections are welcome, particularly on
the meteorology and the Level II decoding.

## License

[AGPL-3.0](LICENSE.txt). Derivative work must be released under the same license.
