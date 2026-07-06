// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker DOM/state and the pushable-key CSS; map.js's window.showRadarSites / setSelectedRadarSite /
// setRadarSitesStatus / setRadarSitesVisible shims delegate here, passing the map. Posts radarSiteClick
// to the host on a key press. `maplibregl` is the global from the vendored classic script.
//
// These are DOM-overlay markers (maplibregl.Marker), so they auto-reposition on pan/zoom and survive
// basemap switches (no style-layer re-add needed). Structure: a `.radar-site-marker` WRAPPER (which
// MapLibre positions via an inline transform) holds an inner `.radar-site-btn` (free to use its own
// transform for the press/sink effect). States: available (calm graphite key) / selected (latched
// orange) / down (offline, muted dark-red dead key) + a status halo (::after). The same colors drive
// the site-list rows so the two views match.

let radarMarkers = {};        // id -> inner button element (state ops target the button)
let radarMarkerObjs = [];     // every Marker object (for show/hide + teardown)
let selectedSiteId = null;
let radarSitesVisible = true;
let radarSiteOffline = new Set(); // site ids with no recent data in the feed (rendered "down")

function ensureStyle() {
    if (document.getElementById('radar-site-style')) return;
    const siteStyle = document.createElement('style');
    siteStyle.id = 'radar-site-style';
    siteStyle.textContent = `
        .radar-site-marker { line-height: 0; }

        .radar-site-btn {
            position: relative;
            display: inline-block;
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

        /* Selected = latched down in accent orange. Sits permanently sunk onto its edge. */
        .radar-site-btn.selected {
            color: #2a1300;
            background: linear-gradient(#ffc070, #ff8a1a);
            border-color: #944300;
            transform: translateY(2px);
            box-shadow: 0 1px 0 #944300, 0 1px 3px rgba(0, 0, 0, .4);
        }

        /* Offline (no recent data in the feed): muted dark-red "dead key". Still clickable;
           keeps its thickness so it still reads as a button, just clearly not live. */
        .radar-site-btn.down {
            color: #e89b9b;
            background: linear-gradient(#332629, #241a1c);
            border-color: #5e3a3a;
            box-shadow: 0 2px 0 #1d1214, 0 3px 5px rgba(0, 0, 0, .45);
            opacity: .92;
        }
        .radar-site-btn.down:hover { filter: brightness(1.18); }
        .radar-site-btn.down:active {
            transform: translateY(2px);
            box-shadow: 0 1px 0 #1d1214, 0 1px 2px rgba(0, 0, 0, .4);
        }
        /* Selected overrides the down look (an offline site can still be the active one). */
        .radar-site-btn.down.selected {
            color: #2a1300;
            background: linear-gradient(#ffc070, #ff8a1a);
            border-color: #944300;
            opacity: 1;
            transform: translateY(2px);
            box-shadow: 0 1px 0 #944300, 0 1px 3px rgba(0, 0, 0, .4);
        }

        /* --- Secondary status "halo" (::after) ---------------------------------------
           A thin ring sitting a few px out from the key, glowing the site's status color.
           It's a transparent-interior border ring so it never covers the key face.
           Order matters: 'down' overrides the default green, 'selected' overrides both. */
        .radar-site-btn::after {
            content: "";
            position: absolute;
            inset: -5px;
            border-radius: 9px;
            pointer-events: none;
            /* available = accent-colored status halo, driven by the OS theme accent the host pushes
               via setAccent (--radar-site-halo / --radar-site-halo-glow). Falls back to green until
               the host pushes (e.g. if the accent push hasn't arrived yet). */
            border: 1.5px solid var(--radar-site-halo, #57c75a);
            box-shadow: 0 0 6px var(--radar-site-halo-glow, rgba(87, 199, 90, .55));
            transition: border-color .15s ease, box-shadow .15s ease;
        }
        /* down = red halo */
        .radar-site-btn.down::after {
            border-color: #e05a5a;
            box-shadow: 0 0 6px rgba(224, 90, 90, .55);
        }
        /* selected = NO small halo. The active site's "radar" is the big geographic range ring +
           rotating sweep arm drawn on the MAP (radar.js), so the key carries no ring — only its
           orange pressed-button look marks it as selected. */
        .radar-site-btn.selected::after { display: none; }`;
    document.head.appendChild(siteStyle);
}

// Applies a marker's offline styling + tooltip from the current radarSiteOffline set.
function applySiteStatus(el, id) {
    const down = radarSiteOffline.has(id);
    el.classList.toggle('down', down);
    const name = el.dataset.siteName || '';
    el.title = name + (down ? ' · offline (no recent data)' : '');
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
        btn.textContent = s.id;
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

// Sets the accent color driving the "available" status halo (the ::after ring), pushed by the host
// from the OS theme accent so it matches the OverlayBar's accent drop-shadow (and re-tints live when
// the OS accent/theme changes). `border` is a CSS color for the ring; `glow` a CSS color for its soft
// box-shadow. Set as CSS custom properties on the root, so they apply to all markers (present and
// future) and the down/red + selected/none halos are unaffected. Empty args leave a value unchanged.
export function setAccent(border, glow) {
    const root = document.documentElement;
    if (border) root.style.setProperty('--radar-site-halo', border);
    if (glow) root.style.setProperty('--radar-site-halo-glow', glow);
}

// Show/hide all site buttons. Independent of the radar layer — an active loop keeps rendering while
// the markers are hidden. Iterate the marker objects (every marker), so we never miss a shared id.
export function setVisible(visible) {
    radarSitesVisible = !!visible;
    radarMarkerObjs.forEach(function (m) {
        m.getElement().style.display = radarSitesVisible ? '' : 'none';
    });
}
