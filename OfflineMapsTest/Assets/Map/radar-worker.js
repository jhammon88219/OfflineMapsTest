// Off-main-thread Level II decode. Receives a volume ArrayBuffer + frame index, decodes +
// builds gate geometry via the shared radar-decode module, and transfers the typed arrays
// back so the bzip2 decode never freezes the UI. Classic worker using dynamic import().
self.onmessage = function (e) {
    const d = e.data;
    import('./radar-decode.js').then(function (m) {
        return m.decodeAndBuild(d.ab, d.siteLat, d.siteLon, d.minDbz);
    }).then(function (res) {
        const g = res.geom;
        if (!g) {
            self.postMessage({ token: d.token, index: d.index, empty: true, decodeMs: res.decodeMs, buildMs: res.buildMs });
            return;
        }
        self.postMessage(
            {
                token: d.token, index: d.index, positions: g.positions, colors: g.colors,
                count: g.count, decodeMs: res.decodeMs, buildMs: res.buildMs,
            },
            [g.positions.buffer, g.colors.buffer] // zero-copy transfer
        );
    }).catch(function (err) {
        self.postMessage({ token: d.token, index: d.index, error: String(err && err.message ? err.message : err) });
    });
};
