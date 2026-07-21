// warnings.js — storm-based NWS warning polygons (active Tornado / Severe Thunderstorm Warnings).
// Sibling of watches.js. Source: the NWS WWA warning polygons (host-filtered to sig='W', phenom TO/SV)
// — the actual forecaster-drawn storm-based polygon (a handful of vertices), i.e. the modern warning
// shape RadarScope/NWS show, NOT the county area watches use. A bold outline + very faint fill, colored
// by the feature's `phenom` (TO = red, SV = yellow). Warnings are the imminent-threat layer, so they
// sit ABOVE the watch boxes. Loaded LAZILY — only fetched when first shown. map.js's
// window.setWarningSource / setWarningsVisible / setWarningsOpacity shims delegate here; applyStyle calls
// reAdd(map) after a basemap switch (setStyle drops the layers, but the fetched data is still in memory).

import { firstBoundaryLayerId } from './layers.js';

let warnUrl = null;
let warnData = null;
let warningsOn = false;

// Overall opacity multiplier (0..1) for the warning polygons, driven by the NowCast card's slider.
// The fill is very faint and the outline bold; the multiplier scales BOTH from their base values, so the
// slider fades the whole overlay together (1 = the default look). See setOpacity.
let warnOpacity = 1;
const FILL_BASE = 0.05;
const LINE_BASE = 1.0;

function warnColor() {
    return ['match', ['to-string', ['get', 'phenom']],
        'TO', '#ff2a2a',   // tornado warning — bright red
        'SV', '#ffd21a',   // severe thunderstorm warning — yellow
        /* other/unknown */ '#ff8c1a'];
}

function removeWarnLayers(map) {
    ['nws-warning-fill', 'nws-warning-line'].forEach(function (id) {
        if (map.getLayer(id)) map.removeLayer(id);
    });
    if (map.getSource('nws-warnings')) map.removeSource('nws-warnings');
}

function layersPresent(map) {
    return !!(map.getLayer('nws-warning-fill') && map.getLayer('nws-warning-line') && map.getSource('nws-warnings'));
}

function addWarnLayers(map) {
    if (!warnData) return;
    removeWarnLayers(map);
    map.addSource('nws-warnings', { type: 'geojson', data: warnData });
    // Beneath the state/country lines (so borders read through) — and ABOVE the watch boxes, which
    // target the same beforeId but are added first (warnings are the more urgent, imminent-threat layer).
    // Radar targets our fill layer in its own beforeId chain, so it stays under warnings.
    const before = firstBoundaryLayerId(map);
    map.addLayer({
        id: 'nws-warning-fill', type: 'fill', source: 'nws-warnings',
        paint: { 'fill-color': warnColor(), 'fill-opacity': FILL_BASE * warnOpacity }
    }, before);
    map.addLayer({
        id: 'nws-warning-line', type: 'line', source: 'nws-warnings',
        paint: { 'line-color': warnColor(), 'line-width': 2.5, 'line-opacity': LINE_BASE * warnOpacity }
    }, before);
}

// Bring the layers in line with the current state. ⚠️ Add ONLY when they're actually missing (first
// show / basemap swap): the ~1-min refresh calls through here, and an unconditional remove-then-add
// would flicker. New data reaches live layers via setData in loadWarnings instead.
function refreshWarnLayers(map) {
    if (!warningsOn || !warnData) { removeWarnLayers(map); return; }
    if (!layersPresent(map)) addWarnLayers(map);
}

// Fetch the cached warning GeoJSON (no-store: the file is overwritten in place each refresh). A failed
// fetch keeps the last known good data on screen rather than blanking the overlay.
function loadWarnings(map) {
    if (!warnUrl) return;
    fetch(warnUrl, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (gj) warnData = gj;
        if (gj && map.getSource('nws-warnings')) map.getSource('nws-warnings').setData(gj);
        refreshWarnLayers(map);
    }).catch(function (e) { console.error('warnings load failed: ' + e); });
}

export function setSource(map, url) {
    warnUrl = url;
    if (warningsOn) loadWarnings(map); // lazy: only fetch when the layer is shown
}

export function setVisible(map, on) {
    warningsOn = !!on;
    if (on && !warnData) loadWarnings(map); // first enable → fetch, then refreshWarnLayers runs in .then
    else refreshWarnLayers(map);
}

// Set the overall opacity multiplier (0..1). Updates the live layers in place if present; otherwise
// it's picked up the next time the layers are added (basemap switch / first show).
export function setOpacity(map, o) {
    warnOpacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer('nws-warning-fill')) map.setPaintProperty('nws-warning-fill', 'fill-opacity', FILL_BASE * warnOpacity);
    if (map.getLayer('nws-warning-line')) map.setPaintProperty('nws-warning-line', 'line-opacity', LINE_BASE * warnOpacity);
}

// Re-add after a basemap switch (setStyle drops the layers; data is still in memory).
export function reAdd(map) {
    refreshWarnLayers(map);
}
