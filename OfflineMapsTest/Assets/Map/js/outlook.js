// outlook.js — the SPC outlook overlay: translucent probability fills + per-CIG intensity HATCHING,
// with the nested intensity groups clipped into mutually-exclusive rings (so lower hatch doesn't show
// through the higher group's gaps). Extracted from map.js. Owns the state + all helpers + the
// show/clear/setOpacity shims; map.js delegates and calls reAdd(map) after a basemap switch — setStyle
// drops custom sources/layers AND registered IMAGES, so the hatch images are re-ensured on each add.

let currentOutlookUrl = null;    // GeoJSON URL currently shown (re-add after a basemap switch)
let outlookData = null;          // fetched + CLIPPED GeoJSON (reused on re-add — no re-fetch/re-clip)
let currentOutlookOpacity = 0.05;

// SPC marks its "significant"/intensity areas as separate polygons (Conditional Intensity Groups):
// LABEL = "CIG1"/"CIG2"/"CIG3" (tornado & wind go to 3, hail to 2); legacy single-significant "SIGN"
// may appear on older data. They render as HATCHING over the probability color, split onto per-group
// fill-pattern layers (see ensureHatchImages + addOutlookLayers). The groups nest (CIG3 ⊂ CIG2 ⊂ CIG1),
// so before rendering we clip each lower group to exclude the higher ones (clipSigFeatures).
const SIG_FILTER = ['any', ['in', 'CIG', ['get', 'LABEL']], ['in', 'SIG', ['get', 'LABEL']]];

// Significance rank from a LABEL: CIG1/2/3 -> 1/2/3, legacy "SIGN" -> 1, anything else 0.
function sigRank(label) {
    if (!label) return 0;
    if (label.indexOf('CIG') === 0) { const n = parseInt(label.slice(3), 10); return isNaN(n) ? 1 : n; }
    if (label.indexOf('SIG') >= 0) return 1;
    return 0;
}

// First symbol (label) layer id, so the outlook slots beneath place names (readable through the fill).
function firstSymbolLayerId(map) {
    const layers = (map.getStyle() && map.getStyle().layers) || [];
    const symbol = layers.find(function (l) { return l.type === 'symbol'; });
    return symbol ? symbol.id : undefined;
}

// Builds one diagonal-hatch tile (light lines on transparent). `tile` = repeat size (bigger = sparser),
// `width` = line thickness. `opts`: `fwd` draws the SW→NE '/' diagonal, `back` the SE→NW '\' (both =>
// cross-hatch), `dash` (optional [on,off]) makes a line a row of dashes. Corner-to-corner lines tile
// into continuous stripes.
function makeHatchImage(tile, width, opts) {
    opts = opts || {};
    const COLOR = 'rgba(235,235,235,0.9)';
    const c = document.createElement('canvas');
    c.width = c.height = tile;
    const ctx = c.getContext('2d');
    ctx.strokeStyle = COLOR;
    ctx.lineWidth = width;
    ctx.lineCap = 'round';
    if (opts.dash) ctx.setLineDash(opts.dash);
    ctx.beginPath();
    if (opts.fwd) { ctx.moveTo(0, tile); ctx.lineTo(tile, 0); }   // '/'  SW -> NE
    if (opts.back) { ctx.moveTo(0, 0); ctx.lineTo(tile, tile); }  // '\'  SE -> NW
    ctx.stroke();
    return ctx.getImageData(0, 0, tile, tile);
}

// One hatch image per Conditional Intensity Group, mirroring SPC's intensity legend: CIG1 = '/' DASHES,
// CIG2 = '\' solid LINES, CIG3 = solid CROSS-HATCH. setStyle drops registered images, so re-ensure each add.
const HATCH_TILE = 28;
function ensureHatchImages(map) {
    if (!map.hasImage('sig-hatch-1')) map.addImage('sig-hatch-1', makeHatchImage(HATCH_TILE, 1.6, { fwd: true, dash: [2.5, 4] }));
    if (!map.hasImage('sig-hatch-2')) map.addImage('sig-hatch-2', makeHatchImage(HATCH_TILE, 1.6, { back: true }));
    if (!map.hasImage('sig-hatch-3')) map.addImage('sig-hatch-3', makeHatchImage(HATCH_TILE, 1.6, { fwd: true, back: true }));
}

// --- Clipping the nested CIG areas into exclusive rings --------------------------------------------
// The groups nest (CIG3 ⊂ CIG2 ⊂ CIG1). We make each lower group exclude the next-higher one by punching
// the higher polygons in as HOLES — valid because they're strictly nested (containment + winding fix).
function signedArea(ring) {
    let s = 0;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        s += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
    }
    return s / 2;
}
function pointInRing(pt, ring) {
    const x = pt[0], y = pt[1];
    let inside = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        const xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
        if (((yi > y) !== (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) inside = !inside;
    }
    return inside;
}
function eachPolygon(geom, cb) {
    if (!geom) return;
    if (geom.type === 'Polygon') cb(geom.coordinates);
    else if (geom.type === 'MultiPolygon') geom.coordinates.forEach(cb);
}
// Append every higher-group exterior ring that falls inside a lower polygon part as a hole (reversed
// when needed so MapLibre's winding-based ring classifier treats it as a hole).
function punchHoles(lower, highers) {
    eachPolygon(lower.geometry, function (poly) {
        const ext = poly[0];
        const extSign = signedArea(ext) >= 0 ? 1 : -1;
        highers.forEach(function (h) {
            eachPolygon(h.geometry, function (hpoly) {
                const hext = hpoly[0];
                if (!pointInRing(hext[0], ext)) return;
                const hole = hext.slice();
                if ((signedArea(hole) >= 0 ? 1 : -1) === extSign) hole.reverse();
                poly.push(hole);
            });
        });
    });
}
// For each significance group, subtract the next-higher group (nested, so it contains all above it).
// Mutates the features' geometries in place.
function clipSigFeatures(geojson) {
    const sigs = (geojson.features || []).filter(function (f) {
        return sigRank(f.properties && f.properties.LABEL) > 0;
    });
    sigs.forEach(function (lower) {
        const lowerRank = sigRank(lower.properties.LABEL);
        const higherRanks = sigs.map(function (s) { return sigRank(s.properties.LABEL); })
            .filter(function (r) { return r > lowerRank; });
        if (higherRanks.length === 0) return;
        const nextRank = Math.min.apply(null, higherRanks);
        const toPunch = sigs.filter(function (s) { return sigRank(s.properties.LABEL) === nextRank; });
        punchHoles(lower, toPunch);
    });
}

function removeOutlookLayers(map) {
    ['spc-outlook-line', 'spc-outlook-sig1', 'spc-outlook-sig2', 'spc-outlook-sig3', 'spc-outlook-fill'].forEach(function (id) {
        if (map.getLayer(id)) map.removeLayer(id);
    });
    if (map.getSource('spc-outlook')) map.removeSource('spc-outlook');
}

function addOutlookLayers(map) {
    if (!outlookData) return;
    removeOutlookLayers(map);
    ensureHatchImages(map);
    map.addSource('spc-outlook', { type: 'geojson', data: outlookData });

    const before = firstSymbolLayerId(map);
    // Solid fill for the probability areas, excluding the significant areas — so the hatch shows the
    // probability color underneath through its gaps. Convective "cake layers" carry their own fill;
    // fire weather falls back to gray.
    map.addLayer({
        id: 'spc-outlook-fill',
        type: 'fill',
        source: 'spc-outlook',
        filter: ['!', SIG_FILTER],
        paint: {
            'fill-color': ['coalesce', ['get', 'fill'], '#888888'],
            'fill-opacity': currentOutlookOpacity
        }
    }, before);
    // One hatch layer per Conditional Intensity Group, each a CONSTANT fill-pattern with a LABEL filter.
    // Geometries are already clipped mutually exclusive (clipSigFeatures). CIG1 also catches legacy "SIGN".
    [
        { id: 'spc-outlook-sig1', img: 'sig-hatch-1', filter: ['any', ['==', ['get', 'LABEL'], 'CIG1'], ['in', 'SIG', ['get', 'LABEL']]] },
        { id: 'spc-outlook-sig2', img: 'sig-hatch-2', filter: ['==', ['get', 'LABEL'], 'CIG2'] },
        { id: 'spc-outlook-sig3', img: 'sig-hatch-3', filter: ['==', ['get', 'LABEL'], 'CIG3'] }
    ].forEach(function (s) {
        map.addLayer({
            id: s.id,
            type: 'fill',
            source: 'spc-outlook',
            filter: s.filter,
            paint: { 'fill-pattern': s.img }
        }, before);
    });
    // Outlines for every area (the significant areas keep their black SPC outline).
    map.addLayer({
        id: 'spc-outlook-line',
        type: 'line',
        source: 'spc-outlook',
        paint: {
            'line-color': ['coalesce', ['get', 'stroke'], '#555555'],
            'line-width': 1.5
        }
    }, before);
}

// Fetch the outlook GeoJSON ourselves, clip the nested CIG areas into exclusive rings, then render.
// (We don't hand the URL to the source directly because the clip has to run on the parsed geometry
// first.) Guards against an out-of-order response when the user switches products quickly.
function loadOutlook(map, url) {
    fetch(url).then(function (r) { return r.json(); }).then(function (gj) {
        if (currentOutlookUrl !== url) return; // a newer selection won
        clipSigFeatures(gj);
        outlookData = gj;
        if (map.isStyleLoaded()) addOutlookLayers(map);
        else map.once('idle', function () { addOutlookLayers(map); });
    }).catch(function (e) {
        console.error('outlook load failed: ' + e);
    });
}

export function show(map, url) {
    currentOutlookUrl = url;
    outlookData = null;
    loadOutlook(map, url);
}

export function clear(map) {
    currentOutlookUrl = null;
    outlookData = null;
    removeOutlookLayers(map);
}

export function setOpacity(map, opacity) {
    currentOutlookOpacity = opacity;
    if (map.getLayer('spc-outlook-fill')) {
        map.setPaintProperty('spc-outlook-fill', 'fill-opacity', opacity);
    }
}

// Re-add after a basemap switch: reuse the already-clipped data, or re-fetch if it isn't loaded yet.
export function reAdd(map) {
    if (outlookData) addOutlookLayers(map);
    else if (currentOutlookUrl) loadOutlook(map, currentOutlookUrl);
}
