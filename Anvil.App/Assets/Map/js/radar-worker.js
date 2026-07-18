// Off-main-thread Level II decode. Receives a volume ArrayBuffer + frame index, decodes +
// builds gate geometry via the shared radar-decode module, and transfers the typed arrays
// back so the bzip2 decode never freezes the UI. Classic worker using dynamic import().
self.onmessage = function (e) {
    const d = e.data;
    import('./radar-decode.js').then(function (m) {
        return m.decodeAndBuild(d.ab, d.siteLat, d.siteLon, d.minDbz, d.buildLazy, d.buildGrids);
    }).then(function (res) {
        // Product geometry + inspector grids are keyed by product id (radar-products.js); we forward them
        // as maps, transferring each product's typed arrays zero-copy. Adding a product needs no change here.
        const msg = {
            token: d.token, index: d.index, url: d.url, built: res.built, gridsBuilt: res.gridsBuilt,
            decodeMs: res.decodeMs, buildMs: res.buildMs,
            radials: res.radials, gates: res.gates, bytes: res.bytes, rangeMeters: res.rangeMeters,
            elevList: res.elevList, velElev: res.velElev, reflStats: res.reflStats, velStats: res.velStats,
            velNyq: res.velNyq, dealias: res.dealias,
            moments: {}, grids: {},
        };
        const transfer = [];
        let any = false;
        Object.keys(res.moments).forEach(function (id) {
            const g = res.moments[id];
            if (g) { msg.moments[id] = { positions: g.positions, colors: g.colors, count: g.count }; transfer.push(g.positions.buffer, g.colors.buffer); any = true; }
            else { msg.moments[id] = null; }
        });
        if (!any) msg.empty = true;
        // Inspector value grids (radar-decode buildGrid): each carries az + an Int16 value array; only
        // present when Inspect was on. Forward zero-copy so the host reads values without re-decoding.
        Object.keys(res.grids).forEach(function (id) {
            const gr = res.grids[id];
            if (gr && gr.az && gr.values) { msg.grids[id] = gr; transfer.push(gr.az.buffer, gr.values.buffer); }
            else { msg.grids[id] = null; }
        });
        self.postMessage(msg, transfer); // zero-copy transfer of whichever geometries exist
    }).catch(function (err) {
        self.postMessage({ token: d.token, index: d.index, url: d.url, error: String(err && err.message ? err.message : err) });
    });
};
