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
        const msg = {
            token: d.token, index: d.index, decodeMs: res.decodeMs, buildMs: res.buildMs,
            radials: res.radials, gates: res.gates, bytes: res.bytes,
        };
        const transfer = [];
        if (g) { msg.positions = g.positions; msg.colors = g.colors; msg.count = g.count; transfer.push(g.positions.buffer, g.colors.buffer); }
        if (v) { msg.velPositions = v.positions; msg.velColors = v.colors; msg.velCount = v.count; transfer.push(v.positions.buffer, v.colors.buffer); }
        if (!g && !v) { msg.empty = true; }
        self.postMessage(msg, transfer); // zero-copy transfer of whichever geometries exist
    }).catch(function (err) {
        self.postMessage({ token: d.token, index: d.index, error: String(err && err.message ? err.message : err) });
    });
};
