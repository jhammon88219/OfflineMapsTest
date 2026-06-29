// Off-main-thread Level II decode. Receives a volume ArrayBuffer + frame index, decodes +
// builds gate geometry via the shared radar-decode module, and transfers the typed arrays
// back so the bzip2 decode never freezes the UI. Classic worker using dynamic import().
self.onmessage = function (e) {
    const d = e.data;
    import('./radar-decode.js').then(function (m) {
        return m.decodeAndBuild(d.ab, d.siteLat, d.siteLon, d.minDbz);
    }).then(function (res) {
        const g = res.geom;      // reflectivity
        const v = res.velGeom;   // velocity
        const cc = res.ccGeom;   // correlation coefficient (ρHV)
        const msg = {
            token: d.token, index: d.index, decodeMs: res.decodeMs, buildMs: res.buildMs,
            radials: res.radials, gates: res.gates, bytes: res.bytes, rangeMeters: res.rangeMeters,
            elevList: res.elevList, velElev: res.velElev, reflStats: res.reflStats, velStats: res.velStats,
            velNyq: res.velNyq, dealias: res.dealias,
        };
        const transfer = [];
        if (g) { msg.positions = g.positions; msg.colors = g.colors; msg.count = g.count; transfer.push(g.positions.buffer, g.colors.buffer); }
        if (v) { msg.velPositions = v.positions; msg.velColors = v.colors; msg.velCount = v.count; transfer.push(v.positions.buffer, v.colors.buffer); }
        if (cc) { msg.ccPositions = cc.positions; msg.ccColors = cc.colors; msg.ccCount = cc.count; transfer.push(cc.positions.buffer, cc.colors.buffer); }
        if (!g && !v && !cc) { msg.empty = true; }
        // Inspector value grids (radar-decode buildGrid): forward zero-copy so the host can read the
        // value under the cursor without re-decoding. Each carries az + an Int16 value array.
        function addGrid(name, gr) { if (gr) { msg[name] = gr; transfer.push(gr.az.buffer, gr.values.buffer); } }
        addGrid('reflGrid', res.reflGrid); addGrid('velGrid', res.velGrid); addGrid('ccGrid', res.ccGrid);
        self.postMessage(msg, transfer); // zero-copy transfer of whichever geometries exist
    }).catch(function (err) {
        self.postMessage({ token: d.token, index: d.index, error: String(err && err.message ? err.message : err) });
    });
};
