# Anvil

**Severe-weather workstation for Windows** .Decodes NEXRAD Level II from raw base data, live or
replayed from the archive back to 2008, and GPU-renders it over local, fully style-controlled vector
basemaps, with SPC outlooks, watches, and DOW mobile-radar frames.

Anvil reads raw WSR-88D Level II volumes, decodes the Message 31 base data itself, and renders every
gate on the GPU. No server-side rendering, no image tiles. The basemap is a local PMTiles archive
instead of a tile service, so you control the styling and panning costs nothing.

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
