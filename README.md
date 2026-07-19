# Anvil

**Severe-weather workstation for Windows** .Decodes NEXRAD Level II from raw base data, live or
replayed from the archive back to 2008, and GPU-renders it over local, fully style-controlled vector
basemaps, with SPC outlooks, watches, and DOW mobile-radar frames.

Anvil reads raw WSR-88D Level II volumes, decodes the Message 31 base data itself, and renders every
gate on the GPU. No server-side rendering, no image tiles. The basemap is a local PMTiles archive
instead of a tile service, so you control the styling and panning costs nothing.
