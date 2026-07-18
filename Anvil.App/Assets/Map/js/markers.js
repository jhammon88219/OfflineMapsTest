// markers.js — the user-location marker (a pulsing blue dot, draggable so the user can refine the
// inherently-approximate location). Extracted from map.js. Self-contained ES module: it owns the
// marker DOM + state and posts drag/click back to the host; map.js's window.showUserLocation /
// clearUserLocation shims delegate here, passing the map instance. `maplibregl` is the global set by
// the vendored classic script (visible to modules via the global scope). A DOM-overlay marker auto-
// repositions on pan/zoom and survives basemap switches, so there's no style-layer re-add to do.

let userLocationMarker = null;
const USER_MARKER_ID = 'user'; // singleton; the host correlates drag/click by this fixed id

function ensureUserLocationStyle() {
    if (document.getElementById('user-location-style')) return;
    const s = document.createElement('style');
    s.id = 'user-location-style';
    s.textContent =
        '.user-loc{position:relative;width:36px;height:36px;}' +
        '.user-loc-dot{position:absolute;left:6px;top:6px;width:24px;height:24px;border-radius:50%;' +
        'background:#2f8fff;border:3px solid #fff;box-shadow:0 0 6px rgba(0,0,0,.6);box-sizing:border-box;}' +
        '.user-loc-pulse{position:absolute;left:0;top:0;width:36px;height:36px;border-radius:50%;' +
        'background:rgba(47,143,255,.45);animation:user-loc-pulse 1.8s ease-out infinite;}' +
        '@keyframes user-loc-pulse{0%{transform:scale(.5);opacity:.8;}100%{transform:scale(2.4);opacity:0;}}';
    document.head.appendChild(s);
}

function postMarker(type, extra) {
    if (!(window.chrome && window.chrome.webview)) return;
    const msg = { type: type, id: USER_MARKER_ID };
    if (extra) { for (const k in extra) msg[k] = extra[k]; }
    window.chrome.webview.postMessage(JSON.stringify(msg));
}

// Place (or replace) the user-location marker at [lng, lat]. Draggable to refine: a dragend reports
// the new position (host flags it "manual"); a click selects it (re-opens its editor if deselected).
export function show(map, lng, lat, label) {
    ensureUserLocationStyle();
    if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
    const el = document.createElement('div');
    el.className = 'user-loc';
    el.title = label || 'Your location';
    const pulse = document.createElement('div'); pulse.className = 'user-loc-pulse';
    const dot = document.createElement('div'); dot.className = 'user-loc-dot';
    el.appendChild(pulse); el.appendChild(dot);
    userLocationMarker = new maplibregl.Marker({ element: el, draggable: true }).setLngLat([lng, lat]).addTo(map);
    userLocationMarker.on('dragend', function () {
        const p = userLocationMarker.getLngLat();
        postMarker('markerMoved', { lng: p.lng, lat: p.lat });
    });
    el.addEventListener('click', function (ev) {
        ev.stopPropagation();
        postMarker('markerClick');
    });
}

export function clear() {
    if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
}
