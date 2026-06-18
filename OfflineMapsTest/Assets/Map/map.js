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
    // The fetched + clipped GeoJSON for currentOutlookUrl. We fetch it ourselves (rather than
    // letting the source load the URL) so we can clip the nested CIG areas into exclusive
    // rings before rendering; kept so a basemap-switch re-add reuses it without re-fetching.
    let outlookData = null;
    // Fill opacity for the outlook polygons (host-controlled via the opacity slider);
    // remembered so it survives re-adds after a basemap switch.
    let currentOutlookOpacity = 0.05;
    // Radar site markers: DOM button overlays (by id) + their Marker objects + selected id.
    let radarMarkers = {};
    let radarMarkerObjs = [];
    let selectedSiteId = null;
    let radarSitesVisible = true;
    let radarSiteOffline = new Set(); // site ids with no recent data in the feed (rendered "down")

    // SPC marks its "significant"/intensity areas as separate polygons. The current scheme
    // is Conditional Intensity Groups: LABEL = "CIG1"/"CIG2"/"CIG3" (tornado & wind go to 3,
    // hail to 2); the legacy single-significant label "SIGN" may still appear on older data.
    // They read as hatching over the probability color (not a solid fill), so we split them
    // onto their own per-group fill-pattern layers (see ensureHatchImages + addOutlookLayers),
    // each group a distinct pattern like spc.noaa.gov's intensity legend. The groups nest
    // (CIG3 ⊂ CIG2 ⊂ CIG1), so before rendering we clip each lower group to exclude the higher
    // ones (clipSigFeatures) — otherwise the lower hatch shows through the higher one's gaps.
    const SIG_FILTER = ['any', ['in', 'CIG', ['get', 'LABEL']], ['in', 'SIG', ['get', 'LABEL']]];

    // Significance rank from a LABEL: CIG1/2/3 -> 1/2/3, legacy "SIGN" -> 1, anything else 0.
    function sigRank(label) {
        if (!label) return 0;
        if (label.indexOf('CIG') === 0) { const n = parseInt(label.slice(3), 10); return isNaN(n) ? 1 : n; }
        if (label.indexOf('SIG') >= 0) return 1;
        return 0;
    }

    // Insert the outlook beneath the basemap's label layers so place names stay
    // readable through the translucent fill.
    function firstSymbolLayerId() {
        const layers = (map.getStyle() && map.getStyle().layers) || [];
        const symbol = layers.find(function (l) { return l.type === 'symbol'; });
        return symbol ? symbol.id : undefined;
    }

    // Builds one diagonal-hatch tile (light lines on transparent). `tile` = repeat size
    // (bigger = sparser/fewer lines), `width` = line thickness. `opts`: `fwd` draws the
    // SW→NE '/' diagonal, `back` draws the SE→NW '\' diagonal (both => cross-hatch), `dash`
    // (optional [on,off]) makes a line a row of dashes. Corner-to-corner lines tile into
    // continuous stripes across polygon-sized areas.
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

    // One hatch image per Conditional Intensity Group, mirroring SPC's intensity legend, with
    // the single-direction groups rotated 90° from each other so adjacent zones read distinctly:
    // CIG1 = '/' (SW→NE) DASHES, CIG2 = '\' (SE→NW) solid LINES, CIG3 = solid CROSS-HATCH.
    // TILE is the line spacing (bigger = fewer lines). setStyle drops registered images, so
    // addOutlookLayers re-ensures these each time.
    const HATCH_TILE = 28; // ~half the line density of the first pass (was 14)
    function ensureHatchImages() {
        if (!map.hasImage('sig-hatch-1')) map.addImage('sig-hatch-1', makeHatchImage(HATCH_TILE, 1.6, { fwd: true, dash: [2.5, 4] }));
        if (!map.hasImage('sig-hatch-2')) map.addImage('sig-hatch-2', makeHatchImage(HATCH_TILE, 1.6, { back: true }));
        if (!map.hasImage('sig-hatch-3')) map.addImage('sig-hatch-3', makeHatchImage(HATCH_TILE, 1.6, { fwd: true, back: true }));
    }

    // --- Clipping the nested CIG areas into exclusive rings ---------------------------------
    // SPC's intensity groups nest (CIG3 ⊂ CIG2 ⊂ CIG1). Rendered as-is, the lower group's hatch
    // fills the whole area and shows through the higher group's gaps. We make each lower group
    // exclude the next-higher one by punching the higher polygons in as HOLES — valid because
    // they're strictly nested, so a containment + winding fix is enough (no clipping library).
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
    // Append every higher-group exterior ring that falls inside a lower polygon part as a hole
    // (reversed when needed so MapLibre's winding-based ring classifier treats it as a hole).
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
    // For each significance group, subtract the next-higher group (which, being nested,
    // already contains all groups above it). Mutates the features' geometries in place.
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

    function removeOutlookLayers() {
        ['spc-outlook-line', 'spc-outlook-sig1', 'spc-outlook-sig2', 'spc-outlook-sig3', 'spc-outlook-fill'].forEach(function (id) {
            if (map.getLayer(id)) map.removeLayer(id);
        });
        if (map.getSource('spc-outlook')) map.removeSource('spc-outlook');
    }

    function addOutlookLayers() {
        if (!outlookData) return;
        removeOutlookLayers();
        ensureHatchImages();
        map.addSource('spc-outlook', { type: 'geojson', data: outlookData });

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
        // One hatch layer per Conditional Intensity Group, each a CONSTANT fill-pattern with a
        // LABEL filter — so there's no ambiguity about which pattern lands on which polygon.
        // The geometries are already clipped to be mutually exclusive (clipSigFeatures), so no
        // hatch shows through another. CIG1 also catches the legacy single-"SIGN" label.
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

    // Host commands (C# -> JS via RunScriptAsync). Style swap re-applies the offline
    // style; flyTo animates (main map), jumpTo snaps (insets); show/clearOutlook +
    // setOutlookOpacity drive the SPC overlay (main map only).
    window.applyStyle = function (url) {
        map.setStyle(url, { diff: true });
        // setStyle drops our custom sources/layers/images — re-add them once the new
        // style settles. Outlook first so the radar's beforeId can target it and slot in
        // beneath it. Reuse the already-clipped data; only re-fetch if it isn't loaded yet.
        map.once('idle', function () {
            if (outlookData) addOutlookLayers();
            else if (currentOutlookUrl) loadOutlook(currentOutlookUrl);
            if (window.RadarLayer) window.RadarLayer.reAdd(map);
        });
    };
    // Fetch the outlook GeoJSON ourselves, clip the nested CIG areas into exclusive rings,
    // then render. (We don't hand the URL to the source directly because the clip has to run
    // on the parsed geometry first.) Guards against an out-of-order response when the user
    // switches products quickly.
    function loadOutlook(url) {
        fetch(url).then(function (r) { return r.json(); }).then(function (gj) {
            if (currentOutlookUrl !== url) return; // a newer selection won
            clipSigFeatures(gj);
            outlookData = gj;
            if (map.isStyleLoaded()) addOutlookLayers();
            else map.once('idle', addOutlookLayers);
        }).catch(function (e) {
            console.error('outlook load failed: ' + e);
        });
    }
    window.showOutlook = function (url) {
        currentOutlookUrl = url;
        outlookData = null;
        loadOutlook(url);
    };
    window.clearOutlook = function () {
        currentOutlookUrl = null;
        outlookData = null;
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
    window.setRadarProduct = function (product) {
        if (window.RadarLayer) window.RadarLayer.setProduct(map, product);
    };

    // Radar site markers: neon rectangle buttons labeled with the site id at each site's
    // location; click to select. These are DOM overlays (maplibregl.Marker), so they auto-
    // reposition on pan/zoom and survive basemap switches (no style-layer re-add needed).
    if (!document.getElementById('radar-site-style')) {
        var siteStyle = document.createElement('style');
        siteStyle.id = 'radar-site-style';
        siteStyle.textContent =
            '.radar-site-btn{font:700 13px/1 "Segoe UI",sans-serif;color:#0a0a0a;background:#ccff00;' +
            'border:1px solid #0a0a0a;border-radius:5px;padding:5px 9px;cursor:pointer;white-space:nowrap;' +
            'user-select:none;box-shadow:0 0 6px rgba(204,255,0,.7);}' +
            '.radar-site-btn:hover{filter:brightness(1.12);}' +
            '.radar-site-btn.selected{background:#ff7a00;box-shadow:0 0 9px rgba(255,122,0,.95);}' +
            // Offline (no recent data in the feed): muted dark chip + soft red glow, dimmed so
            // the live neon sites read as active. Still clickable. Selected overrides.
            '.radar-site-btn.down{background:#2a2d31;color:#d56b6b;border-color:#8a3a3a;' +
            'box-shadow:0 0 5px rgba(220,70,70,.55);opacity:.78;}' +
            '.radar-site-btn.down.selected{background:#ff7a00;color:#0a0a0a;opacity:1;}';
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
            el.dataset.siteName = s.name || '';
            if (!radarSitesVisible) el.style.display = 'none';
            if (selectedSiteId === s.id) el.classList.add('selected');
            applySiteStatus(el, s.id); // sets .down class + tooltip from the current offline set
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
    // Applies a marker's offline styling + tooltip from the current radarSiteOffline set.
    function applySiteStatus(el, id) {
        var down = radarSiteOffline.has(id);
        el.classList.toggle('down', down);
        var name = el.dataset.siteName || '';
        el.title = name + (down ? ' · offline (no recent data)' : '');
    }
    // Host command: which sites are offline (array of ids). Re-styles existing markers.
    window.setRadarSitesStatus = function (json) {
        try { radarSiteOffline = new Set((typeof json === 'string') ? JSON.parse(json) : json); }
        catch (e) { radarSiteOffline = new Set(); }
        Object.keys(radarMarkers).forEach(function (k) { applySiteStatus(radarMarkers[k], k); });
    };
    // Show/hide all site buttons. Independent of the radar layer — an active loop keeps
    // rendering while the markers are hidden. Iterate the marker objects (every marker)
    // rather than the id-keyed map, so we never miss one if two sites share an id.
    window.setRadarSitesVisible = function (visible) {
        radarSitesVisible = !!visible;
        radarMarkerObjs.forEach(function (m) {
            m.getElement().style.display = radarSitesVisible ? '' : 'none';
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
