// One reusable page hosting a SINGLE MapLibre map. The host (MainWindow) loads it once,
// passing interactivity, initial framing, and basemap style as URL parameters:
//   ?interactive=true|false & style & lng & lat & zoom
// The page makes no decisions of its own — it renders what the host asks for and
// exposes a few command shims the host drives over the IMapView seam.
const params = new URLSearchParams(location.search);
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
    // SPC watch boxes: the cache URL, the fetched watch GeoJSON, and the on/off flag. Kept so a
    // basemap-switch re-add reuses the data without re-fetching. Fetched lazily — only when shown.
    let watchUrl = null;
    let watchData = null;
    let watchesOn = false;
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
            refreshWatchLayers(); // re-add the watch layers (data is still in memory)
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

    // ---- SPC watches (Tornado / Severe Thunderstorm Watch areas) ----------------------------
    // Source: the NWS WWA county-aggregated watch polygons (they follow county lines, like
    // RadarScope), already filtered to active TO/SV watches by the host. A faint fill + bold
    // outline, colored by the feature's `phenom` — TO (tornado watch) red, SV (severe
    // thunderstorm watch) blue. One toggle.
    function watchColor() {
        return ['match', ['to-string', ['get', 'phenom']],
            'TO', '#ff3b30',   // tornado watch — red
            'SV', '#ffd21a',   // severe thunderstorm watch — yellow
            /* other/unknown */ '#cccc40'];
    }
    function removeWatchLayers() {
        ['spc-watch-fill', 'spc-watch-line'].forEach(function (id) {
            if (map.getLayer(id)) map.removeLayer(id);
        });
        if (map.getSource('spc-watches')) map.removeSource('spc-watches');
    }
    function addWatchLayers() {
        if (!watchData) return;
        removeWatchLayers();
        map.addSource('spc-watches', { type: 'geojson', data: watchData });
        const before = firstSymbolLayerId(); // above the radar/outlook, below the labels
        map.addLayer({
            id: 'spc-watch-fill', type: 'fill', source: 'spc-watches',
            paint: { 'fill-color': watchColor(), 'fill-opacity': 0.08 }
        }, before);
        map.addLayer({
            id: 'spc-watch-line', type: 'line', source: 'spc-watches',
            paint: { 'line-color': watchColor(), 'line-width': 2, 'line-opacity': 0.9 }
        }, before);
    }
    // Re-render from the current data (used after a fetch or a basemap swap).
    function refreshWatchLayers() {
        if (watchesOn && watchData) addWatchLayers(); else removeWatchLayers();
    }
    // Fetch the cached watch GeoJSON (no-store: the file is overwritten in place each refresh).
    function loadWatches() {
        if (!watchUrl) return;
        fetch(watchUrl, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
            watchData = gj;
            if (gj && map.getSource('spc-watches')) map.getSource('spc-watches').setData(gj);
            refreshWatchLayers();
        }).catch(function (e) { console.error('watches load failed: ' + e); });
    }
    window.setWatchSource = function (url) {
        watchUrl = url;
        if (watchesOn) loadWatches(); // lazy: only fetch when the layer is shown
    };
    window.setWatchesVisible = function (on) {
        watchesOn = !!on;
        if (on && !watchData) loadWatches(); // first enable → fetch, then refreshWatchLayers runs in .then
        else refreshWatchLayers();
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
    window.radarRemap = function (newCount, mappingJson) {
        if (window.RadarLayer) window.RadarLayer.remap(map, newCount, mappingJson);
    };
    window.clearLevel2Radar = function () {
        if (window.RadarLayer) window.RadarLayer.clear(map);
    };
    window.setRadarOpacity = function (opacity) {
        if (window.RadarLayer) window.RadarLayer.setOpacity(map, opacity);
    };
    // Current rendered product, tracked so the legend can be (re)pushed for it (see below).
    var radarProduct = 'reflectivity';
    window.setRadarProduct = function (product) {
        radarProduct = product;
        if (window.RadarLayer) window.RadarLayer.setProduct(map, product);
        postRampFor(product);
    };
    // Inspect mode: read the value under the cursor (RadarScope-style). Delegates to RadarLayer,
    // which tracks the mouse and posts {type:"radarInspect"} for the color-scale marker.
    window.setRadarInspect = function (on) {
        if (window.RadarLayer) window.RadarLayer.setInspect(map, on);
    };

    // DOW Event Viewer shims — show / clear a single curated mobile-radar frame (a .dow.json from
    // the dowevents host). showDow reuses the whole radar render path; clear tears it down.
    window.showDowFrame = function (url) {
        if (window.RadarLayer) window.RadarLayer.showDow(map, url);
    };
    window.clearDowFrame = function () {
        if (window.RadarLayer) window.RadarLayer.clear(map);
    };

    // Color-scale legend feed. The ramps in radar-ramps.js are the SINGLE source of truth for gate
    // colors, so the host's legend tool window is fed from them (never hard-coded): eager-load the
    // ramps and push the active product's ramp to the host on load + on every product switch. The
    // host renders the bar from these exact stops, so the legend can't drift from the pixels.
    var radarRamps = null;
    function postRampFor(product) {
        var r = radarRamps && radarRamps[product];
        if (r && window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'radarRamp', ramp: r }));
        }
    }
    import('./radar-ramps.js').then(function (m) {
        radarRamps = { reflectivity: m.REFLECTIVITY_RAMP, velocity: m.VELOCITY_RAMP, cc: m.CORRELATION_RAMP };
        postRampFor(radarProduct); // whatever product is active once the ramps are loaded
    }).catch(function (e) { /* legend stays empty if ramps can't load */ });

    // User-location marker: a pulsing blue dot (deliberately unlike the neon radar-site chips),
    // a DOM overlay so it auto-repositions on pan/zoom and survives basemap switches.
    var userLocationMarker = null;
    function ensureUserLocationStyle() {
        if (document.getElementById('user-location-style')) return;
        var s = document.createElement('style');
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
    // The user marker is draggable so the user can refine the (inherently approximate) location.
    // It's a singleton; the host correlates drag/click by this fixed id.
    var USER_MARKER_ID = 'user';
    function postMarker(type, extra) {
        if (!(window.chrome && window.chrome.webview)) return;
        var msg = { type: type, id: USER_MARKER_ID };
        if (extra) { for (var k in extra) msg[k] = extra[k]; }
        window.chrome.webview.postMessage(JSON.stringify(msg));
    }
    window.showUserLocation = function (lng, lat, label) {
        ensureUserLocationStyle();
        if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
        var el = document.createElement('div');
        el.className = 'user-loc';
        el.title = label || 'Your location';
        var pulse = document.createElement('div'); pulse.className = 'user-loc-pulse';
        var dot = document.createElement('div'); dot.className = 'user-loc-dot';
        el.appendChild(pulse); el.appendChild(dot);
        userLocationMarker = new maplibregl.Marker({ element: el, draggable: true }).setLngLat([lng, lat]).addTo(map);
        // Drag to refine → report the new position back to the host (it flags the marker "manual").
        userLocationMarker.on('dragend', function () {
            var p = userLocationMarker.getLngLat();
            postMarker('markerMoved', { lng: p.lng, lat: p.lat });
        });
        // Click selects the marker (re-opens its editor if it was deselected). Don't fire after a drag.
        el.addEventListener('click', function (ev) {
            ev.stopPropagation();
            postMarker('markerClick');
        });
    };
    window.clearUserLocation = function () {
        if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
    };

    // Radar site markers: pushable "key" buttons labeled with the site id at each site's
    // location; click to select. These are DOM overlays (maplibregl.Marker), so they auto-
    // reposition on pan/zoom and survive basemap switches (no style-layer re-add needed).
    //
    // Structure: a `.radar-site-marker` WRAPPER holds an inner `.radar-site-btn`. MapLibre
    // positions a marker by writing `transform` *inline* on the marker element it's given, so
    // it owns the wrapper's transform; the inner button is free to use its own `transform`
    // for the press/sink effect without fighting the map's positioning.
    //
    // The "pushable" look = a solid darker bottom edge (first box-shadow) reads as the key's
    // thickness; a soft drop shadow lifts it off the map. Pressing (or latching "selected")
    // translates the button down onto its edge so it visibly sinks. Four states:
    //   default  available, raised neutral (Fluent dark) key — calm, clearly "not on"
    //   :hover   slight brighten
    //   :active  momentarily sunk (mouse-down)
    //   selected latched: sunk + accent orange (stays pressed while this site is active)
    //   down     offline (no recent data): muted dark-red dead key, still clickable
    //
    // Palette note: the available state is a neutral graphite key (not bright) so it reads as
    // "available / ready", not "already activated". Only the latched-selected key is colored
    // (orange accent). These same colors drive the site-list rows so the two views match.
    if (!document.getElementById('radar-site-style')) {
        var siteStyle = document.createElement('style');
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
               Order matters: 'down' overrides the default green, 'selected' overrides both
               (an offline site can still be the active one), so list them last → first. */
            .radar-site-btn::after {
                content: "";
                position: absolute;
                inset: -5px;
                border-radius: 9px;
                pointer-events: none;
                /* available = green status halo */
                border: 1.5px solid #57c75a;
                box-shadow: 0 0 6px rgba(87, 199, 90, .55);
                transition: border-color .15s ease, box-shadow .15s ease;
            }
            /* down = red halo */
            .radar-site-btn.down::after {
                border-color: #e05a5a;
                box-shadow: 0 0 6px rgba(224, 90, 90, .55);
            }
            /* selected = NO small halo. The active site's "radar" is the big geographic range ring
               + rotating sweep arm drawn on the MAP (radar.js), so the key itself carries no ring —
               only its orange pressed-button look marks it as selected. */
            .radar-site-btn.selected::after { display: none; }`;
        document.head.appendChild(siteStyle);
    }

    // Host commands: provide the site list (as buttons), and highlight the selected one.
    window.showRadarSites = function (json) {
        var sites = (typeof json === 'string') ? JSON.parse(json) : json;
        radarMarkerObjs.forEach(function (m) { m.remove(); });
        radarMarkerObjs = [];
        radarMarkers = {};
        sites.forEach(function (s) {
            // Wrapper = the marker element MapLibre positions; inner btn = the styled key.
            var el = document.createElement('div');
            el.className = 'radar-site-marker';
            var btn = document.createElement('div');
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
            var marker = new maplibregl.Marker({ element: el }).setLngLat([s.lng, s.lat]).addTo(map);
            radarMarkerObjs.push(marker);
            radarMarkers[s.id] = btn; // state ops (selected/down/tooltip) target the inner button
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

    // Radar sweep timing. The host (C#) calls setRadarSweep(periodSeconds) at each live-poll cycle
    // start; we delegate to the radar layer, which rotates the on-map sweep arm so one full
    // revolution equals the time until the next update (phase-locked — the arm completes as new
    // data is due). period <= 0 stops/removes the sweep. Same delegate pattern as the other shims.
    window.setRadarSweep = function (periodSeconds) {
        if (window.RadarLayer) window.RadarLayer.setSweep(map, periodSeconds);
    };

    // Tell the host this map is ready to receive commands.
    map.on('load', function () {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'mapReady' }));
        }
    });
} catch (err) {
    document.body.insertAdjacentHTML('beforeend',
        '<div style="position:absolute;top:8px;left:8px;z-index:10;' +
        'font:12px Segoe UI;background:#c00;color:#fff;padding:4px 8px;border-radius:4px;">' +
        'JS error: ' + err.message + '</div>');
}
