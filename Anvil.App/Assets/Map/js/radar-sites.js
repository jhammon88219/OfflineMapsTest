// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker DOM/state and the pushable-key CSS; map.js's window.showRadarSites / setSelectedRadarSite /
// setRadarSitesStatus / setRadarSitesVisible shims delegate here, passing the map. Posts radarSiteClick
// to the host on a key press. `maplibregl` is the global from the vendored classic script.
//
// These are DOM-overlay markers (maplibregl.Marker), so they auto-reposition on pan/zoom and survive
// basemap switches (no style-layer re-add needed). Structure: a `.radar-site-marker` WRAPPER (which
// MapLibre positions via an inline transform) holds an inner `.radar-site-btn` (free to use its own
// transform for the press/sink effect), which contains a full-height availability SQUARE
// `.radar-site-swatch` on the LEFT + the ID text `.radar-site-label`. The SQUARE shows availability —
// green = available, red (.offline) = no recent data — always, independent of selection; SELECTION is
// the inverted "light" key (dark text on near-white), and the square still shows on the light face.
// (History: this was a small round dot before; the accent status halo + orange-selected + dead-key
// offline styling were removed in an earlier rework.)

let radarMarkers = {};        // id -> inner button element (state ops target the button)
let radarMarkerObjs = [];     // every Marker object (for show/hide + teardown)
let selectedSiteId = null;
let radarSitesVisible = true;
let researchVisible = false;      // research/test radars (e.g. KCRI) are an opt-in extra layer
let researchIds = new Set();      // ids flagged research (site.research) in the current list
let tdwrVisible = false;          // Terminal Doppler Weather Radars (T***) are an opt-in extra layer
let tdwrIds = new Set();          // ids flagged tdwr (site.tdwr) in the current list
let radarSiteOffline = new Set(); // site ids with no recent data in the feed (red availability dot)

// A marker shows only when the global sites layer is on AND each opt-in category it belongs to is on.
// Operational sites (in neither extra set) show whenever the global layer is on; research/TDWR keys
// additionally need their own toggle. So "Show Research Radars" / "Show TDWRs" reveal just those keys,
// and "Hide Sites" still hides everything.
function markerVisible(id) {
    return radarSitesVisible
        && (!researchIds.has(id) || researchVisible)
        && (!tdwrIds.has(id) || tdwrVisible);
}

// Re-apply the visibility rule to every marker (after any toggle changes).
function applyVisibility() {
    radarMarkerObjs.forEach(function (m) {
        const id = m.getElement().dataset.siteId;
        m.getElement().style.display = markerVisible(id) ? '' : 'none';
    });
}

function ensureStyle() {
    if (document.getElementById('radar-site-style')) return;
    const siteStyle = document.createElement('style');
    siteStyle.id = 'radar-site-style';
    siteStyle.textContent = `
        .radar-site-marker { line-height: 0; }

        /* Pushable graphite "key": a full-height status SQUARE on the left + the ID on the face. */
        .radar-site-btn {
            display: inline-flex;
            align-items: stretch;              /* the status square fills the full key height */
            font: 700 12px/1 "Segoe UI", sans-serif;
            letter-spacing: .3px;
            color: #f3f3f3;
            background: linear-gradient(#3b3b3e, #2c2c2f);
            border: 1px solid #5a5a5e;
            border-radius: 6px;
            overflow: hidden;                  /* clip the square's corners to the key radius */
            cursor: pointer;
            white-space: nowrap;
            user-select: none;
            box-shadow: 0 3px 0 #161618, 0 4px 6px rgba(0, 0, 0, .45);
            transition: transform .05s ease, box-shadow .05s ease, filter .1s ease;
        }
        .radar-site-btn:hover { filter: brightness(1.18); }
        .radar-site-btn:active {
            transform: translateY(2px);
            box-shadow: 0 1px 0 #161618, 0 1px 2px rgba(0, 0, 0, .4);
        }

        /* Status square: green = available, red = offline (the staleness-ramp endpoint colors, so the
           palette matches the freshness readout). A full-height block filling the LEFT of the key; always
           shows availability, independent of selection (still reads on the light selected face). */
        .radar-site-swatch {
            flex: 0 0 auto;
            align-self: stretch;
            width: 22px;
            background: #3fb950;
        }
        .radar-site-btn.offline .radar-site-swatch { background: #f85149; }

        /* The ID text sits on the key face to the right of the square. */
        .radar-site-label { padding: 5px 9px; }

        /* Selected = inverted "light" key (dark text on a near-white face). Distinct from BOTH the dark
           unselected keys and the red/green square (orange sat too close to the offline red). Latches down
           onto its edge like a pressed key; the status square still shows availability on the light face.
           The active site's "radar" is also the big geographic range ring + sweep drawn on the MAP (radar.js). */
        .radar-site-btn.selected {
            color: #1a1a1a;
            background: linear-gradient(#ffffff, #e8e8e8);
            border-color: #b9b9b9;
            transform: translateY(2px);
            box-shadow: 0 1px 0 #9a9a9a, 0 1px 3px rgba(0, 0, 0, .4);
        }
        .radar-site-btn.selected:hover { filter: brightness(1.03); }`;
    document.head.appendChild(siteStyle);
}

// Applies a marker's availability (dot color via the .offline class) + tooltip from the offline set.
function applySiteStatus(el, id) {
    const off = radarSiteOffline.has(id);
    el.classList.toggle('offline', off);
    const name = el.dataset.siteName || '';
    el.title = name + (off ? ' · offline (no recent data)' : '');
}

// Provide the site list (as buttons). Each wrapper is the marker MapLibre positions; the inner
// button is the styled key. A press posts radarSiteClick to the host.
export function show(map, json) {
    ensureStyle();
    const sites = (typeof json === 'string') ? JSON.parse(json) : json;
    radarMarkerObjs.forEach(function (m) { m.remove(); });
    radarMarkerObjs = [];
    radarMarkers = {};
    researchIds = new Set();
    tdwrIds = new Set();
    sites.forEach(function (s) {
        if (s.research) researchIds.add(s.id);
        if (s.tdwr) tdwrIds.add(s.id);
        const el = document.createElement('div');
        el.className = 'radar-site-marker';
        el.dataset.siteId = s.id; // used by applyVisibility to re-evaluate the per-marker rule
        const btn = document.createElement('div');
        btn.className = 'radar-site-btn';
        const swatch = document.createElement('span');
        swatch.className = 'radar-site-swatch'; // availability: green (available) / red (.offline)
        const label = document.createElement('span');
        label.className = 'radar-site-label';
        label.textContent = s.id;
        btn.appendChild(swatch);
        btn.appendChild(label);
        btn.dataset.siteName = s.name || '';
        el.appendChild(btn);
        if (!markerVisible(s.id)) el.style.display = 'none';
        if (selectedSiteId === s.id) btn.classList.add('selected');
        applySiteStatus(btn, s.id); // sets .down class + tooltip from the current offline set
        btn.addEventListener('click', function (ev) {
            ev.stopPropagation();
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'radarSiteClick', id: s.id }));
            }
        });
        const marker = new maplibregl.Marker({ element: el }).setLngLat([s.lng, s.lat]).addTo(map);
        radarMarkerObjs.push(marker);
        radarMarkers[s.id] = btn; // state ops (selected/down/tooltip) target the inner button
    });
}

export function setSelected(id) {
    selectedSiteId = id || null;
    Object.keys(radarMarkers).forEach(function (k) {
        radarMarkers[k].classList.toggle('selected', k === selectedSiteId);
    });
}

// Which sites are offline (array of ids). Re-styles existing markers.
export function setStatus(json) {
    try { radarSiteOffline = new Set((typeof json === 'string') ? JSON.parse(json) : json); }
    catch (e) { radarSiteOffline = new Set(); }
    Object.keys(radarMarkers).forEach(function (k) { applySiteStatus(radarMarkers[k], k); });
}

// No-op: the on-map markers no longer use the OS accent (the halo was removed — availability is a fixed
// green/red DOT and selection is the inverted-light key). Kept so the host's setRadarSitesAccent shim
// (MapService.SetRadarSiteAccentAsync → map.js) stays valid; the OverlayBar still uses the accent itself.
export function setAccent(border, glow) { /* markers no longer use an accent halo */ }

// Show/hide all site buttons. Independent of the radar layer — an active loop keeps rendering while
// the markers are hidden. Research markers stay subject to their own toggle via markerVisible().
export function setVisible(visible) {
    radarSitesVisible = !!visible;
    applyVisibility();
}

// Show/hide just the research/test radar markers (the "Show Research Radars" toggle). Off by
// default; operational markers are unaffected. An active research loop keeps rendering while hidden.
export function setResearchVisible(visible) {
    researchVisible = !!visible;
    applyVisibility();
}

// Show/hide just the TDWR markers (the "Show TDWRs" toggle). Off by default; operational markers are
// unaffected. An active TDWR loop keeps rendering while hidden.
export function setTdwrVisible(visible) {
    tdwrVisible = !!visible;
    applyVisibility();
}
