// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker DOM/state and the pushable-key CSS; map.js's window.showRadarSites / setSelectedRadarSite /
// setRadarSitesStatus / setRadarSitesVisible shims delegate here, passing the map. Posts radarSiteClick
// to the host on a key press. `maplibregl` is the global from the vendored classic script.
//
// These are DOM-overlay markers (maplibregl.Marker), so they auto-reposition on pan/zoom and survive
// basemap switches (no style-layer re-add needed). Structure: a `.radar-site-marker` WRAPPER (which
// MapLibre positions via an inline transform) holds an inner `.radar-site-btn` (free to use its own
// transform for the press/sink effect), which contains an availability `.radar-site-dot` + the ID
// text (e.g. "• KTLX"). The DOT shows availability — green = available, red (.offline) = no recent data
// — always, independent of selection; SELECTION is the inverted "light" key (dark text on near-white).
// (The old accent status halo + orange-selected + dead-key offline styling were removed in this rework.)

let radarMarkers = {};        // id -> inner button element (state ops target the button)
let radarMarkerObjs = [];     // every Marker object (for show/hide + teardown)
let selectedSiteId = null;
let radarSitesVisible = true;
let radarSiteOffline = new Set(); // site ids with no recent data in the feed (red availability dot)

function ensureStyle() {
    if (document.getElementById('radar-site-style')) return;
    const siteStyle = document.createElement('style');
    siteStyle.id = 'radar-site-style';
    siteStyle.textContent = `
        .radar-site-marker { line-height: 0; }

        /* Pushable graphite "key" with an availability DOT before the ID (no halo). */
        .radar-site-btn {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            font: 700 12px/1 "Segoe UI", sans-serif;
            letter-spacing: .3px;
            color: #f3f3f3;
            background: linear-gradient(#3b3b3e, #2c2c2f);
            border: 1px solid #5a5a5e;
            border-radius: 6px;
            padding: 5px 9px;
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

        /* Availability dot: green = available, red = offline (the staleness-ramp endpoint colors, so the
           palette matches the freshness readout). Always shows availability, independent of selection.
           The 1px inner ring gives it definition on both the dark key and the light selected key. */
        .radar-site-dot {
            flex: 0 0 auto;
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: #3fb950;
            box-shadow: 0 0 0 1px rgba(0, 0, 0, .28);
        }
        .radar-site-btn.offline .radar-site-dot { background: #f85149; }

        /* Selected = inverted "light" key (dark text on a near-white face). Distinct from BOTH the dark
           unselected keys and the red/green dots (orange sat too close to the offline red). Latches down
           onto its edge like a pressed key; the dot still shows availability on the light face. The active
           site's "radar" is also the big geographic range ring + sweep drawn on the MAP (radar.js). */
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
    sites.forEach(function (s) {
        const el = document.createElement('div');
        el.className = 'radar-site-marker';
        const btn = document.createElement('div');
        btn.className = 'radar-site-btn';
        const dot = document.createElement('span');
        dot.className = 'radar-site-dot'; // availability: green (available) / red (.offline)
        btn.appendChild(dot);
        btn.appendChild(document.createTextNode(s.id));
        btn.dataset.siteName = s.name || '';
        el.appendChild(btn);
        if (!radarSitesVisible) el.style.display = 'none';
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
// the markers are hidden. Iterate the marker objects (every marker), so we never miss a shared id.
export function setVisible(visible) {
    radarSitesVisible = !!visible;
    radarMarkerObjs.forEach(function (m) {
        m.getElement().style.display = radarSitesVisible ? '' : 'none';
    });
}
