// watches.js — SPC watch boxes (Tornado / Severe Thunderstorm Watch areas). Extracted from map.js.
// Source: the NWS WWA county-aggregated active TO/SV watch polygons (host-filtered; they follow
// county lines, like RadarScope). A faint fill + bold outline colored by the feature's `phenom`
// (TO = red, SV = yellow). Loaded LAZILY — only fetched when first shown. map.js's window.setWatchSource
// / setWatchesVisible shims delegate here; applyStyle calls reAdd(map) after a basemap switch (setStyle
// drops the layers, but the fetched data is still in memory).

let watchUrl = null;
let watchData = null;
let watchesOn = false;

// First symbol (label) layer id, so the watch layers slot beneath place names. Own small copy (the
// outlook concern in map.js keeps its own); shared into one helper if/when that's extracted too.
function firstSymbolLayerId(map) {
    const layers = (map.getStyle() && map.getStyle().layers) || [];
    const symbol = layers.find(function (l) { return l.type === 'symbol'; });
    return symbol ? symbol.id : undefined;
}

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

function addWatchLayers(map) {
    if (!watchData) return;
    removeWatchLayers(map);
    map.addSource('spc-watches', { type: 'geojson', data: watchData });
    const before = firstSymbolLayerId(map); // above the radar/outlook, below the labels
    map.addLayer({
        id: 'spc-watch-fill', type: 'fill', source: 'spc-watches',
        paint: { 'fill-color': watchColor(), 'fill-opacity': 0.08 }
    }, before);
    map.addLayer({
        id: 'spc-watch-line', type: 'line', source: 'spc-watches',
        paint: { 'line-color': watchColor(), 'line-width': 2, 'line-opacity': 0.9 }
    }, before);
}

// Re-render from the current data (after a fetch or a basemap swap).
function refreshWatchLayers(map) {
    if (watchesOn && watchData) addWatchLayers(map); else removeWatchLayers(map);
}

// Fetch the cached watch GeoJSON (no-store: the file is overwritten in place each refresh).
function loadWatches(map) {
    if (!watchUrl) return;
    fetch(watchUrl, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        watchData = gj;
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

// Re-add after a basemap switch (setStyle drops the layers; data is still in memory).
export function reAdd(map) {
    refreshWatchLayers(map);
}
