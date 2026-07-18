// watches.js — SPC watch boxes (Tornado / Severe Thunderstorm Watch areas). Extracted from map.js.
// Source: the NWS WWA county-aggregated active TO/SV watch polygons (host-filtered; they follow
// county lines, like RadarScope). A faint fill + bold outline colored by the feature's `phenom`
// (TO = red, SV = yellow). Loaded LAZILY — only fetched when first shown. map.js's window.setWatchSource
// / setWatchesVisible shims delegate here; applyStyle calls reAdd(map) after a basemap switch (setStyle
// drops the layers, but the fetched data is still in memory).

import { firstBoundaryLayerId } from './layers.js';

let watchUrl = null;
let watchData = null;
let watchesOn = false;

// Overall opacity multiplier (0..1) for the watch polygons, driven by the NowCast card's slider.
// The fill is faint and the outline bold; the multiplier scales BOTH from their base values, so the
// slider fades the whole overlay together (1 = the default look). See setOpacity.
let watchOpacity = 1;
const FILL_BASE = 0.08;
const LINE_BASE = 0.9;

function watchColor() {
    return ['match', ['to-string', ['get', 'phenom']],
        'TO', '#ff3b30',   // tornado watch — red
        'SV', '#ffd21a',   // severe thunderstorm watch — yellow
        /* other/unknown */ '#cccc40'];
}

function removeWatchLayers(map) {
    ['spc-watch-fill', 'spc-watch-line'].forEach(function (id) {
        if (map.getLayer(id)) map.removeLayer(id);
    });
    if (map.getSource('spc-watches')) map.removeSource('spc-watches');
}

function layersPresent(map) {
    return !!(map.getLayer('spc-watch-fill') && map.getLayer('spc-watch-line') && map.getSource('spc-watches'));
}

function addWatchLayers(map) {
    if (!watchData) return;
    removeWatchLayers(map);
    map.addSource('spc-watches', { type: 'geojson', data: watchData });
    // Beneath the state/country lines (so borders read through the fill) — and above radar, which
    // targets our fill layer in its own beforeId chain.
    const before = firstBoundaryLayerId(map);
    map.addLayer({
        id: 'spc-watch-fill', type: 'fill', source: 'spc-watches',
        paint: { 'fill-color': watchColor(), 'fill-opacity': FILL_BASE * watchOpacity }
    }, before);
    map.addLayer({
        id: 'spc-watch-line', type: 'line', source: 'spc-watches',
        paint: { 'line-color': watchColor(), 'line-width': 2, 'line-opacity': LINE_BASE * watchOpacity }
    }, before);
}

// Bring the layers in line with the current state. ⚠️ Add ONLY when they're actually missing (first
// show / basemap swap): the ~2-min refresh calls through here, and an unconditional remove-then-add
// showed up as a periodic flicker. New data reaches live layers via setData in loadWatches instead.
function refreshWatchLayers(map) {
    if (!watchesOn || !watchData) { removeWatchLayers(map); return; }
    if (!layersPresent(map)) addWatchLayers(map);
}

// Fetch the cached watch GeoJSON (no-store: the file is overwritten in place each refresh). A failed
// fetch keeps the last known good data on screen rather than blanking the overlay.
function loadWatches(map) {
    if (!watchUrl) return;
    fetch(watchUrl, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (gj) watchData = gj;
        if (gj && map.getSource('spc-watches')) map.getSource('spc-watches').setData(gj);
        refreshWatchLayers(map);
    }).catch(function (e) { console.error('watches load failed: ' + e); });
}

export function setSource(map, url) {
    watchUrl = url;
    if (watchesOn) loadWatches(map); // lazy: only fetch when the layer is shown
}

export function setVisible(map, on) {
    watchesOn = !!on;
    if (on && !watchData) loadWatches(map); // first enable → fetch, then refreshWatchLayers runs in .then
    else refreshWatchLayers(map);
}

// Set the overall opacity multiplier (0..1). Updates the live layers in place if present; otherwise
// it's picked up the next time the layers are added (basemap switch / first show).
export function setOpacity(map, o) {
    watchOpacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer('spc-watch-fill')) map.setPaintProperty('spc-watch-fill', 'fill-opacity', FILL_BASE * watchOpacity);
    if (map.getLayer('spc-watch-line')) map.setPaintProperty('spc-watch-line', 'line-opacity', LINE_BASE * watchOpacity);
}

// Re-add after a basemap switch (setStyle drops the layers; data is still in memory).
export function reAdd(map) {
    refreshWatchLayers(map);
}
