// storm-reports.js — SPC storm-report dots (the filtered Tornado / Wind / Hail reports SPC verifies its
// outlooks against). One GeoJSON point source, three circle layers (one per type, so each toggles
// independently), colored to SPC's convention: tornado = red, wind = blue, hail = green. Rendered ON TOP of
// everything (no beforeId) so the dots read against the outlook fill + radar beneath them. Loaded LAZILY —
// only fetched when a type is first shown. map.js's window.setStormReports* shims delegate here; applyStyle
// calls reAdd(map) after a basemap switch (setStyle drops the layers; the fetched data stays in memory).

let reportsUrl = null;
let reportsData = null;
// Per-type visibility, driven by the card's Tornado / Wind / Hail checkboxes.
let kinds = { torn: false, wind: false, hail: false };
let reportsOpacity = 0.9;
let popup = null;            // single reusable click popup (created lazily)
let interactionsBound = false; // click/hover handlers are bound once per layer id (survive re-adds)

// SPC storm-report colors + labels (matching the SPC storm-reports pages / verification graphics).
const KIND_LAYERS = [
    { kind: 'torn', id: 'spc-report-torn', color: '#e51919', label: 'Tornado' }, // red
    { kind: 'wind', id: 'spc-report-wind', color: '#1663d8', label: 'Wind' },    // blue
    { kind: 'hail', id: 'spc-report-hail', color: '#18a020', label: 'Hail' },    // green
];
const META = KIND_LAYERS.reduce(function (m, l) { m[l.kind] = l; return m; }, {});

function anyShown() { return kinds.torn || kinds.wind || kinds.hail; }

function removeReportLayers(map) {
    KIND_LAYERS.forEach(function (l) { if (map.getLayer(l.id)) map.removeLayer(l.id); });
    if (map.getSource('spc-reports')) map.removeSource('spc-reports');
}

function layersPresent(map) {
    return !!(map.getSource('spc-reports') && map.getLayer(KIND_LAYERS[0].id));
}

function addReportLayers(map) {
    if (!reportsData) return;
    removeReportLayers(map);
    map.addSource('spc-reports', { type: 'geojson', data: reportsData });
    // One small circle layer per type. Radius scales gently with zoom so dots stay legible zoomed way out
    // (a big outbreak covers CONUS) yet don't blob together zoomed in. A thin dark stroke keeps them crisp
    // over bright outlook fills. Each layer's visibility follows its type toggle.
    KIND_LAYERS.forEach(function (l) {
        map.addLayer({
            id: l.id,
            type: 'circle',
            source: 'spc-reports',
            filter: ['==', ['get', 'kind'], l.kind],
            layout: { visibility: kinds[l.kind] ? 'visible' : 'none' },
            paint: {
                'circle-color': l.color,
                'circle-radius': ['interpolate', ['linear'], ['zoom'], 3, 3, 7, 5, 10, 7],
                'circle-opacity': reportsOpacity,
                'circle-stroke-width': 1,
                'circle-stroke-color': 'rgba(20,20,20,0.85)',
                'circle-stroke-opacity': reportsOpacity
            }
        });
    });
    bindInteractions(map);
}

// --- Click popup (report details) ------------------------------------------------------------------
// Each report feature carries its SPC fields (time, magnitude, place, comments); clicking a dot opens a
// popup showing them. Pure MapLibre — no host round-trip. The layer-scoped click/hover handlers are bound
// ONCE per layer id and survive removeLayer/addLayer (MapLibre resolves them against the live layers each
// event), so re-adding after a basemap switch doesn't double-bind.

function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
}

// SPC magnitude column → readable text (empty when unknown). Hail size is hundredths of an inch; wind is
// mph; tornado is the (E)F rating number.
function magText(kind, mag) {
    if (!mag || mag === 'UNK') return '';
    var n = parseInt(mag, 10);
    if (kind === 'hail') return isNaN(n) ? '' : (n / 100).toFixed(2) + ' in';
    if (kind === 'wind') return isNaN(n) ? String(mag) : (n + ' mph');
    if (kind === 'torn') return isNaN(n) ? String(mag) : ('EF' + n);
    return String(mag);
}

// SPC report times are UTC HHMM over the convective day — show as HH:MM UTC.
function timeText(t) {
    t = String(t == null ? '' : t);
    if (/^\d{3,4}$/.test(t)) { t = ('000' + t).slice(-4); return t.slice(0, 2) + ':' + t.slice(2) + ' UTC'; }
    return t;
}

function ensurePopupStyle() {
    if (document.getElementById('spc-report-popup-style')) return;
    var css = [
        '.spc-report-popup .maplibregl-popup-content{background:rgba(22,24,28,0.96);color:#eaeaea;',
        'font:12px/1.45 "Segoe UI",system-ui,sans-serif;border-radius:8px;padding:9px 12px;',
        'box-shadow:0 4px 16px rgba(0,0,0,0.5);max-width:280px;}',
        '.spc-report-popup .maplibregl-popup-tip{border-top-color:rgba(22,24,28,0.96);',
        'border-bottom-color:rgba(22,24,28,0.96);}',
        '.spc-report-popup .maplibregl-popup-close-button{color:#aaa;font-size:15px;padding:0 4px;}',
        '.spc-report-title{font-weight:600;margin-bottom:3px;}',
        '.spc-report-meta{color:#b6b6b6;margin-bottom:4px;}',
        '.spc-report-com{color:#d2d2d2;}'
    ].join('');
    var st = document.createElement('style');
    st.id = 'spc-report-popup-style';
    st.textContent = css;
    document.head.appendChild(st);
}

function popupHtml(p) {
    var meta = META[p.kind] || { label: p.kind || 'Report', color: '#888' };
    var mag = magText(p.kind, p.mag);
    var title = meta.label + (mag ? ' · ' + mag : '');
    var place = [p.loc, p.county ? p.county + ' Co.' : '', p.st].filter(Boolean).join(', ');
    var mea = [];
    if (place) mea.push(esc(place));
    if (p.time) mea.push(esc(timeText(p.time)));
    var html = '<div class="spc-report-title" style="color:' + meta.color + '">' + esc(title) + '</div>';
    if (mea.length) html += '<div class="spc-report-meta">' + mea.join(' · ') + '</div>';
    if (p.com) html += '<div class="spc-report-com">' + esc(p.com) + '</div>';
    return html;
}

function bindInteractions(map) {
    if (interactionsBound) return;
    interactionsBound = true;
    KIND_LAYERS.forEach(function (l) {
        map.on('click', l.id, function (e) {
            var f = e.features && e.features[0];
            if (!f) return;
            ensurePopupStyle();
            if (!popup) popup = new maplibregl.Popup({ className: 'spc-report-popup', maxWidth: '280px' });
            popup.setLngLat(f.geometry.coordinates.slice()).setHTML(popupHtml(f.properties)).addTo(map);
        });
        map.on('mouseenter', l.id, function () { map.getCanvas().style.cursor = 'pointer'; });
        map.on('mouseleave', l.id, function () { map.getCanvas().style.cursor = ''; });
    });
}

function closePopup() { if (popup) popup.remove(); }

// Bring the layers in line with the current state: drop them entirely when nothing is shown; otherwise add
// them if missing and set each type's visibility.
function refreshReportLayers(map) {
    if (!reportsData || !anyShown()) { removeReportLayers(map); return; }
    if (!layersPresent(map)) addReportLayers(map);
    KIND_LAYERS.forEach(function (l) {
        if (map.getLayer(l.id)) map.setLayoutProperty(l.id, 'visibility', kinds[l.kind] ? 'visible' : 'none');
    });
}

// Fetch the cached report GeoJSON (no-store: today's file is overwritten in place as reports come in). A
// failed fetch keeps the last known good data on screen rather than blanking the overlay.
function loadReports(map) {
    if (!reportsUrl) return;
    var url = reportsUrl;
    fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (reportsUrl !== url) return; // a newer day/selection won
        if (gj) reportsData = gj;
        if (gj && map.getSource('spc-reports')) map.getSource('spc-reports').setData(gj);
        refreshReportLayers(map);
    }).catch(function (e) { console.error('storm reports load failed: ' + e); });
}

export function setSource(map, url) {
    reportsUrl = url;
    reportsData = null; // a new day → drop the old points until the new file loads
    closePopup();       // a popup from the previous day would be stranded
    if (anyShown()) loadReports(map); // lazy: only fetch when a type is shown
}

export function setKinds(map, torn, wind, hail) {
    kinds = { torn: !!torn, wind: !!wind, hail: !!hail };
    if (anyShown() && !reportsData) loadReports(map); // first enable → fetch, then refresh runs in .then
    else refreshReportLayers(map);
}

export function setOpacity(map, o) {
    reportsOpacity = Math.max(0, Math.min(1, +o || 0));
    KIND_LAYERS.forEach(function (l) {
        if (map.getLayer(l.id)) {
            map.setPaintProperty(l.id, 'circle-opacity', reportsOpacity);
            map.setPaintProperty(l.id, 'circle-stroke-opacity', reportsOpacity);
        }
    });
}

export function clear(map) {
    reportsUrl = null;
    reportsData = null;
    kinds = { torn: false, wind: false, hail: false };
    closePopup();
    removeReportLayers(map);
}

// Re-add after a basemap switch (setStyle drops the layers; data is still in memory).
export function reAdd(map) {
    refreshReportLayers(map);
}
