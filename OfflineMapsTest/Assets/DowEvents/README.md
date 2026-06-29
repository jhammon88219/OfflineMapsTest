# DOW event frames

Drop curated **`.dow.json`** frames here (produced by [`tools/dow_import.py`](../../../tools/dow_import.py))
and they appear in the app's **DOW Event Viewer** tool window.

- One file = one mobile-radar (Doppler on Wheels) sweep, decoded + rendered through the normal radar
  pipeline (reflectivity / velocity / CC + Inspect + the color-scale legend), centered on the truck's
  position for that deployment.
- The file name becomes the picker label (e.g. `goshen_2009-06-05_DOW7.dow.json` →
  "goshen 2009-06-05 DOW7"). Name them clearly.
- These ship with the app (`Content` / `PreserveNewest`), so only add **openly-licensed** data and
  carry the source's required citation/acknowledgment (CSWR / FARM). See `tools/README.md`.

This folder is intentionally kept in source (with this README) so the `dowevents` virtual host always
has a folder to map, even before any events are added.
