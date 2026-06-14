// One reusable page hosting a SINGLE MapLibre map. The host (MainWindow) loads it
// once per map (the full-window main map and the two inset maps), passing the map
// identity, interactivity, initial framing, and basemap style as URL parameters:
//   ?key=main|alaska|hawaii & interactive=true|false & style & lng & lat & zoom
// The page makes no decisions of its own — it renders what the host asks for and
// exposes a few command shims the host drives over the IMapView seam.
const params = new URLSearchParams(location.search);
const key = params.get('key') || 'main';
const interactive = params.get('interactive') === 'true';
const styleUrl = 'https://mapassets/' + (params.get('style') || 'style.json');
const lng = parseFloat(params.get('lng'));
const lat = parseFloat(params.get('lat'));
const zoom = parseFloat(params.get('zoom'));

try {
    // Register the pmtiles:// protocol so MapLibre can read the local file.
    const protocol = new pmtiles.Protocol();
    maplibregl.addProtocol('pmtiles', protocol.tile);

    const map = new maplibregl.Map({
        container: 'map',
        style: styleUrl,
        center: [lng, lat],
        zoom: zoom,
        interactive: interactive,
        attributionControl: false
    });

    // The SPC outlook currently shown on this map (its GeoJSON URL), or null. Tracked
    // so we can re-add it after a basemap switch (setStyle drops custom layers).
    let currentOutlookUrl = null;
    // Fill opacity for the outlook polygons (host-controlled via the opacity slider);
    // remembered so it survives re-adds after a basemap switch.
    let currentOutlookOpacity = 0.05;
    // Radar site markers: DOM button overlays (by id) + their Marker objects + selected id.
    let radarMarkers = {};
    let radarMarkerObjs = [];
    let selectedSiteId = null;

    // SPC marks its "significant"/intensity areas as separate polygons (LABEL like
    // CIG1/CIG2 or SIGN). They should read as hatching over the probability color, not
    // a solid fill, so we split them onto their own fill-pattern layer.
    const SIG_FILTER = ['any', ['in', 'CIG', ['get', 'LABEL']], ['in', 'SIG', ['get', 'LABEL']]];

    // Insert the outlook beneath the basemap's label layers so place names stay
    // readable through the translucent fill.
    function firstSymbolLayerId() {
        const layers = (map.getStyle() && map.getStyle().layers) || [];
        const symbol = layers.find(function (l) { return l.type === 'symbol'; });
        return symbol ? symbol.id : undefined;
    }

    // Builds a small diagonal-hatch image (light lines on transparent) and registers it.
    // setStyle drops registered images, so addOutlookLayers re-ensures it each time.
    function ensureHatchImage() {
        if (map.hasImage('sig-hatch')) return;
        // Hatch-look knobs. TILE = spacing between lines (bigger = sparser, so more of
        // the area below shows through); WIDTH = line thickness; COLOR = line tint/alpha.
        const TILE = 32;
        const WIDTH = 1.0;
        const COLOR = 'rgba(235,235,235,0.8)';
        const c = document.createElement('canvas');
        c.width = c.height = TILE;
        const ctx = c.getContext('2d');
        ctx.strokeStyle = COLOR;
        ctx.lineWidth = WIDTH;
        ctx.beginPath();
        ctx.moveTo(0, TILE); ctx.lineTo(TILE, 0);  // corner-to-corner: tiles into continuous diagonals
        ctx.stroke();
        map.addImage('sig-hatch', ctx.getImageData(0, 0, TILE, TILE));
    }

    function removeOutlookLayers() {
        ['spc-outlook-line', 'spc-outlook-sig', 'spc-outlook-fill'].forEach(function (id) {
            if (map.getLayer(id)) map.removeLayer(id);
        });
        if (map.getSource('spc-outlook')) map.removeSource('spc-outlook');
    }

    function addOutlookLayers(url) {
        removeOutlookLayers();
        ensureHatchImage();
        map.addSource('spc-outlook', { type: 'geojson', data: url });

        const before = firstSymbolLayerId();
        // Solid fill for the probability areas, excluding the significant areas — so the
        // hatch shows the probability color underneath through its gaps. Convective
        // "cake layers" carry their own fill; fire weather falls back to gray.
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
        // Hatch overlay for the significant/intensity areas.
        map.addLayer({
            id: 'spc-outlook-sig',
            type: 'fill',
            source: 'spc-outlook',
            filter: SIG_FILTER,
            paint: { 'fill-pattern': 'sig-hatch' }
        }, before);
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

    // Host commands (C# -> JS via RunScriptAsync). Style swap re-applies the offline
    // style; flyTo animates (main map), jumpTo snaps (insets); show/clearOutlook +
    // setOutlookOpacity drive the SPC overlay (main map only).
    window.applyStyle = function (url) {
        map.setStyle(url, { diff: true });
        // setStyle drops our custom sources/layers/images — re-add them once the new
        // style settles. Outlook first so the radar's beforeId can target it and slot in
        // beneath it.
        map.once('idle', function () {
            if (currentOutlookUrl) addOutlookLayers(currentOutlookUrl);
            if (window.RadarLayer) window.RadarLayer.reAdd(map);
        });
    };
    window.showOutlook = function (url) {
        currentOutlookUrl = url;
        if (map.isStyleLoaded()) addOutlookLayers(url);
        else map.once('idle', function () { addOutlookLayers(url); });
    };
    window.clearOutlook = function () {
        currentOutlookUrl = null;
        removeOutlookLayers();
    };
    window.setOutlookOpacity = function (opacity) {
        currentOutlookOpacity = opacity;
        if (map.getLayer('spc-outlook-fill')) {
            map.setPaintProperty('spc-outlook-fill', 'fill-opacity', opacity);
        }
    };

    window.flyTo = function (lng, lat, zoom) { map.flyTo({ center: [lng, lat], zoom: zoom }); };

    // Level II radar shims — delegate to RadarLayer (radar.js), passing this map instance.
    window.radarBeginLoop = function (lat, lon) {
        if (window.RadarLayer) window.RadarLayer.beginLoop(map, lat, lon);
    };
    window.radarAddFrame = function (url, index) {
        if (window.RadarLayer) window.RadarLayer.addFrame(map, url, index);
    };
    window.radarShowFrame = function (index) {
        if (window.RadarLayer) window.RadarLayer.showFrame(map, index);
    };
    window.clearLevel2Radar = function () {
        if (window.RadarLayer) window.RadarLayer.clear(map);
    };
    window.setRadarOpacity = function (opacity) {
        if (window.RadarLayer) window.RadarLayer.setOpacity(map, opacity);
    };

    // Radar site markers: neon rectangle buttons labeled with the site id at each site's
    // location; click to select. These are DOM overlays (maplibregl.Marker), so they auto-
    // reposition on pan/zoom and survive basemap switches (no style-layer re-add needed).
    if (!document.getElementById('radar-site-style')) {
        var siteStyle = document.createElement('style');
        siteStyle.id = 'radar-site-style';
        siteStyle.textContent =
            '.radar-site-btn{font:700 11px/1 "Segoe UI",sans-serif;color:#0a0a0a;background:#ccff00;' +
            'border:1px solid #0a0a0a;border-radius:4px;padding:3px 7px;cursor:pointer;white-space:nowrap;' +
            'user-select:none;box-shadow:0 0 6px rgba(204,255,0,.7);}' +
            '.radar-site-btn:hover{filter:brightness(1.12);}' +
            '.radar-site-btn.selected{background:#ff7a00;box-shadow:0 0 9px rgba(255,122,0,.95);}';
        document.head.appendChild(siteStyle);
    }

    // Host commands: provide the site list (as buttons), and highlight the selected one.
    window.showRadarSites = function (json) {
        var sites = (typeof json === 'string') ? JSON.parse(json) : json;
        radarMarkerObjs.forEach(function (m) { m.remove(); });
        radarMarkerObjs = [];
        radarMarkers = {};
        sites.forEach(function (s) {
            var el = document.createElement('div');
            el.className = 'radar-site-btn';
            el.textContent = s.id;
            el.title = s.name;
            if (selectedSiteId === s.id) el.classList.add('selected');
            el.addEventListener('click', function (ev) {
                ev.stopPropagation();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({ type: 'radarSiteClick', id: s.id }));
                }
            });
            var marker = new maplibregl.Marker({ element: el }).setLngLat([s.lng, s.lat]).addTo(map);
            radarMarkerObjs.push(marker);
            radarMarkers[s.id] = el;
        });
    };
    window.setSelectedRadarSite = function (id) {
        selectedSiteId = id || null;
        Object.keys(radarMarkers).forEach(function (k) {
            radarMarkers[k].classList.toggle('selected', k === selectedSiteId);
        });
    };

    // Tell the host this map is ready to receive commands, tagged with its key so the
    // host knows which of the three maps reported.
    map.on('load', function () {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'mapReady', key: key }));
        }
    });
} catch (err) {
    document.body.insertAdjacentHTML('beforeend',
        '<div style="position:absolute;top:8px;left:8px;z-index:10;' +
        'font:12px Segoe UI;background:#c00;color:#fff;padding:4px 8px;border-radius:4px;">' +
        'JS error: ' + err.message + '</div>');
}
