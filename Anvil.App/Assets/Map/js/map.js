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
        attributionControl: false,
        // One world, not an endless horizontal loop of copies. `renderWorldCopies:false` stops the
        // repeat; `minZoom` stops zooming out past a single-globe view (where the copies were the
        // whole point of the complaint). NOT `maxBounds` — the site list is the full WSR-88D network,
        // including Guam / Okinawa / Korea, so panning off CONUS has to stay possible.
        renderWorldCopies: false,
        minZoom: 2
    });


    // Host commands (C# -> JS via RunScriptAsync). Style swap re-applies the offline
    // style; flyTo animates (main map), jumpTo snaps (insets); show/clearOutlook +
    // setOutlookOpacity drive the SPC overlay (main map only).
    window.applyStyle = function (url) {
        map.setStyle(url, { diff: true });
        // setStyle drops our custom sources/layers/images — re-add them once the new
        // style settles. Outlook first so the radar's beforeId can target it and slot in
        // beneath it. Reuse the already-clipped data; only re-fetch if it isn't loaded yet.
        map.once('idle', function () {
            if (Outlook) Outlook.reAdd(map); // re-add the outlook (reuse clipped data, or re-fetch)
            if (Watches) Watches.reAdd(map); // re-add the watch layers (data is still in memory)
            if (Warnings) Warnings.reAdd(map); // re-add the warning polygons (above the watches)
            if (StormReports) StormReports.reAdd(map); // re-add the storm-report dots (top of the stack)
            if (window.RadarLayer) window.RadarLayer.reAdd(map);
        });
    };
    // SPC outlook overlay (probability fills + per-CIG hatching; nested groups clipped) lives in
    // outlook.js — load once and delegate (passing the map). applyStyle calls Outlook.reAdd(map).
    var Outlook = null;
    import('./outlook.js').then(function (m) { Outlook = m; }).catch(function (e) { console.error('outlook.js load failed: ' + e); });
    window.showOutlook = function (url) { if (Outlook) Outlook.show(map, url); };
    window.clearOutlook = function () { if (Outlook) Outlook.clear(map); };
    window.setOutlookOpacity = function (opacity) { if (Outlook) Outlook.setOpacity(map, opacity); };

    // SPC watch boxes live in watches.js — load once and delegate (passing the map). applyStyle calls
    // Watches.reAdd(map) after a basemap switch (setStyle drops the layers; the data stays in memory).
    var Watches = null;
    import('./watches.js').then(function (m) { Watches = m; }).catch(function (e) { console.error('watches.js load failed: ' + e); });
    window.setWatchSource = function (url) { if (Watches) Watches.setSource(map, url); };
    window.setWatchesVisible = function (on) { if (Watches) Watches.setVisible(map, on); };
    window.setWatchesOpacity = function (o) { if (Watches) Watches.setOpacity(map, o); };

    // Storm-based warning polygons live in warnings.js — same lazy-load/delegate pattern as watches.
    // Warnings sit ABOVE the watch boxes (imminent-threat layer). applyStyle calls Warnings.reAdd(map).
    var Warnings = null;
    import('./warnings.js').then(function (m) { Warnings = m; }).catch(function (e) { console.error('warnings.js load failed: ' + e); });
    window.setWarningSource = function (url) { if (Warnings) Warnings.setSource(map, url); };
    window.setWarningsVisible = function (on) { if (Warnings) Warnings.setVisible(map, on); };
    window.setWarningsOpacity = function (o) { if (Warnings) Warnings.setOpacity(map, o); };

    // SPC storm-report dots (Tornado / Wind / Hail verification) live in storm-reports.js — same lazy-load/
    // delegate pattern. They sit on TOP of the stack. applyStyle calls StormReports.reAdd(map).
    var StormReports = null;
    import('./storm-reports.js').then(function (m) { StormReports = m; }).catch(function (e) { console.error('storm-reports.js load failed: ' + e); });
    window.setStormReportsSource = function (url) { if (StormReports) StormReports.setSource(map, url); };
    window.setStormReportKinds = function (torn, wind, hail) { if (StormReports) StormReports.setKinds(map, torn, wind, hail); };
    window.setStormReportsOpacity = function (o) { if (StormReports) StormReports.setOpacity(map, o); };

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
    // Speculatively build velocity in the background (host calls this once reflectivity has rendered),
    // so a later switch to the Velocity product is instant.
    window.prefetchRadarVelocity = function () {
        if (window.RadarLayer) window.RadarLayer.prefetchVelocity();
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
        // Key each exported ramp by its own `id` (matches the product id from radar-products.js), so a
        // new product's ramp is picked up automatically — no per-product edit here.
        radarRamps = {};
        Object.keys(m).forEach(function (k) {
            var r = m[k];
            if (r && r.id && Array.isArray(r.stops)) radarRamps[r.id] = r;
        });
        // Push the WHOLE table once: the Product combo draws EVERY product's ramp next to its name, so the
        // host needs them all — not just the active one (which postRampFor sends for the inspect marker).
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'radarRamps', ramps: radarRamps }));
        }
        postRampFor(radarProduct); // whatever product is active once the ramps are loaded
    }).catch(function (e) { /* legend stays empty if ramps can't load */ });

    // User-location marker (the pulsing blue dot) lives in markers.js — load it once and delegate the
    // shims to it (passing the map). It's only invoked on a "My Location" click, long after this loads,
    // so the cached `Markers` is always ready by then; the guards are belt-and-suspenders.
    var Markers = null;
    import('./markers.js').then(function (m) { Markers = m; }).catch(function (e) { console.error('markers.js load failed: ' + e); });
    window.showUserLocation = function (lng, lat, label) { if (Markers) Markers.show(map, lng, lat, label); };
    window.clearUserLocation = function () { if (Markers) Markers.clear(); };

    // Radar site marker buttons live in radar-sites.js — load once and delegate (passing the map).
    var RadarSites = null;
    var pendingSiteAccent = null; // accent pushed before the module loaded (map-ready can beat the import)
    import('./radar-sites.js').then(function (m) {
        RadarSites = m;
        if (pendingSiteAccent) { m.setAccent(pendingSiteAccent[0], pendingSiteAccent[1]); pendingSiteAccent = null; }
    }).catch(function (e) { console.error('radar-sites.js load failed: ' + e); });
    window.showRadarSites = function (json) { if (RadarSites) RadarSites.show(map, json); };
    window.setSelectedRadarSite = function (id) { if (RadarSites) RadarSites.setSelected(id); };
    window.setRadarSitesStatus = function (json) { if (RadarSites) RadarSites.setStatus(json); };
    window.setRadarSitesVisible = function (visible) { if (RadarSites) RadarSites.setVisible(visible); };
    window.setResearchRadarsVisible = function (visible) { if (RadarSites) RadarSites.setResearchVisible(visible); };
    window.setTdwrsVisible = function (visible) { if (RadarSites) RadarSites.setTdwrVisible(visible); };
    // The OS theme accent for the site-status halo. Cache if the module hasn't loaded yet so the
    // map-ready push (which can beat the dynamic import) isn't dropped.
    window.setRadarSiteAccent = function (border, glow) {
        if (RadarSites) RadarSites.setAccent(border, glow);
        else pendingSiteAccent = [border, glow];
    };

    // Radar sweep pulse. The host (C#) calls pulseRadarSweep() when a genuinely-new frame lands; we
    // delegate to the radar layer, which runs ONE sweep revolution (arm + trailing afterglow) then
    // hides the arm, leaving the range ring. setRadarSweep(period<=0) stops/removes it (clear/replay).
    window.pulseRadarSweep = function () {
        if (window.RadarLayer) window.RadarLayer.pulseSweep(map);
    };
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
