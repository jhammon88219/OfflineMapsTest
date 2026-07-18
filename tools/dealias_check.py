#!/usr/bin/env python3
"""
dealias_check.py - regression-check the app's velocity DEALIASING against Py-ART, no JS runtime needed.

The app's dealiaser (`radar-decode.js` `dealiasSweep`, "v2") is a port of Py-ART's region-based
algorithm. This script re-implements that SAME algorithm in Python (a faithful mirror of the JS, with
no scipy/Cython) and, on the same AWS volume the app uses, diffs it against Py-ART's own
`dealias_region_based` -- the scientific-standard answer key. Use it to confirm a change to the JS
dealiaser still matches Py-ART BEFORE shipping (this machine has no Node/deno/bun to run the JS).

It reports, for the lowest velocity tilt:
  * the dealiased value range (m/s) from the mirror vs Py-ART,
  * whole-sweep agreement (fraction of gates within one Nyquist == same fold choice),
  * the value at the COUPLET CORE (Py-ART's peak-inbound gate) from both -- the number that matters
    for a violent tornado (v1 capped it near Nyquist; v2 recovers it).

If you edit the JS `dealiasSweep`, mirror the edit in `dealias_v2()` below and re-run: the couplet
core should stay within a few mph of Py-ART and agreement should stay > ~99%.

Setup (one-time): python -m pip install arm_pyart      (see README.md)
Usage (TIME IS UTC):  python dealias_check.py SITE YYYY MM DD HH MM
Examples:
    python dealias_check.py KTLX 2013 05 20 20 12    # Moore EF5 couplet  (Py-ART core ~ -135 mph)
    python dealias_check.py KTLX 1999 05 03 23 46    # Bridge Creek F5    (Py-ART core ~ -100 mph, legacy)

Does NOT touch the C# app.
"""
import sys, gzip, tempfile, os, datetime, urllib.request
import xml.etree.ElementTree as ET
import numpy as np

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3NS = "{http://s3.amazonaws.com/doc/2006-03-01/}"


def list_keys(site, day):
    prefix = "%04d/%02d/%02d/%s/" % (day.year, day.month, day.day, site)
    with urllib.request.urlopen("%s?list-type=2&max-keys=1000&prefix=%s" % (BUCKET, prefix), timeout=60) as r:
        root = ET.fromstring(r.read())
    return [el.text for el in root.iter(S3NS + "Key")
            if not (el.text[:-3] if el.text.endswith(".gz") else el.text).endswith("_MDM")]


def key_time(k):
    return datetime.datetime.strptime(k.rsplit("/", 1)[-1][4:19], "%Y%m%d_%H%M%S")


def nearest(keys, t):
    return min(keys, key=lambda k: abs((key_time(k) - t).total_seconds()))


def download(key):
    with urllib.request.urlopen(BUCKET + key, timeout=300) as r:
        d = r.read()
    return gzip.decompress(d) if key.endswith(".gz") else d


# --- Python mirror of the JS `dealiasSweep` (v2, Py-ART region-based). Keep in sync with -----------
# --- Anvil.App/Assets/Map/js/radar-decode.js if you change the algorithm. --------------------
def _find_regions(raw, nyq, splits=3):
    """Bin the Nyquist interval into `splits` bands; label connected comps within each band."""
    N, G = raw.shape
    interval = 2.0 * nyq / splits
    finite = raw[np.isfinite(raw)]
    mx = finite.max() if finite.size else nyq
    mn = finite.min() if finite.size else -nyq
    add_s = int(np.ceil((mx - nyq) / interval)) if mx > nyq else 0
    add_e = int(np.ceil(-(mn + nyq) / interval)) if mn < -nyq else 0
    limits = np.linspace(-nyq - add_s * interval, nyq + add_e * interval, splits + 1 + add_s + add_e)
    label = np.full((N, G), -1, np.int32)
    cnt = []
    for b in range(len(limits) - 1):
        lmin, lmax = limits[b], limits[b + 1]
        for r0 in range(N):
            for j0 in range(G):
                if label[r0, j0] != -1:
                    continue
                v = raw[r0, j0]
                if not (np.isfinite(v) and lmin <= v < lmax):
                    continue
                rid = len(cnt); cnt.append(0)
                st = [(r0, j0)]; label[r0, j0] = rid
                while st:
                    r, j = st.pop(); cnt[rid] += 1
                    for rr, jj in ((r, j - 1), (r, j + 1), ((r - 1) % N, j), ((r + 1) % N, j)):
                        if jj < 0 or jj >= G:
                            continue
                        if label[rr, jj] != -1:
                            continue
                        vv = raw[rr, jj]
                        if np.isfinite(vv) and lmin <= vv < lmax:
                            label[rr, jj] = rid; st.append((rr, jj))
    return label, cnt


def _edges_of(label, raw, nyq2):
    N, G = raw.shape
    adj = {}
    def add(a, b, va, vb):
        if a == b:
            return
        key = (a, b) if a < b else (b, a)
        e = adj.get(key)
        if e is None:
            e = [0, 0.0]; adj[key] = e
        e[0] += 1
        e[1] += ((va - vb) if a < b else (vb - va)) / nyq2
    for r in range(N):
        for j in range(G):
            la = label[r, j]
            if la < 0:
                continue
            va = raw[r, j]
            for rr, jj in ((r, j + 1), ((r + 1) % N, j)):
                if jj >= G:
                    continue
                lb = label[rr, jj]
                if lb < 0 or lb == la:
                    continue
                add(la, lb, va, raw[rr, jj])
    return adj


def dealias_v2(raw, nyq, splits=3):
    """Mirror of the JS dealiasSweep: segment -> edges -> heaviest-first merge (combine parallels) -> center."""
    N, G = raw.shape
    nyq2 = 2.0 * nyq
    label, cnt = _find_regions(raw, nyq, splits)
    nreg = len(cnt)
    if nreg < 2:
        return raw.copy(), nreg
    adj = _edges_of(label, raw, nyq2)
    g = {i: {} for i in range(nreg)}
    for (a, b), (w, s) in adj.items():
        g[a][b] = [w, s]; g[b][a] = [w, -s]
    size = list(cnt)
    regions_in = {i: [i] for i in range(nreg)}
    unwrap = np.zeros(nreg, np.int64)
    alive = set(range(nreg))

    def do_unwrap(node, nw):
        if nw == 0:
            return
        for reg in regions_in[node]:
            unwrap[reg] += nw
        for nb, e in g[node].items():
            e[1] += e[0] * nw
            g[nb][node][1] -= e[0] * nw

    while True:
        bestw = 0; bpair = None
        for node in alive:
            for nb, e in g[node].items():
                if e[0] > bestw:
                    bestw = e[0]; bpair = (node, nb)
        if bpair is None:
            break
        n1, n2 = bpair
        rdiff = int(round(g[n1][n2][1] / g[n1][n2][0]))
        if size[n1] >= size[n2]:
            base, merge, nw = n1, n2, rdiff
        else:
            base, merge, nw = n2, n1, -rdiff
        do_unwrap(merge, nw)
        g[base].pop(merge, None); g[merge].pop(base, None)
        for nb, e in list(g[merge].items()):
            g[nb].pop(merge, None)
            if nb in g[base]:
                g[base][nb][0] += e[0]; g[base][nb][1] += e[1]
                g[nb][base][0] += e[0]; g[nb][base][1] += -e[1]
            else:
                g[base][nb] = [e[0], e[1]]; g[nb][base] = [e[0], -e[1]]
        g[merge] = {}
        regions_in[base].extend(regions_in[merge]); regions_in[merge] = []
        size[base] += size[merge]
        alive.discard(merge)

    total = sum(cnt)
    off = int(round(sum(cnt[r] * unwrap[r] for r in range(nreg)) / total)) if total else 0
    unwrap -= off

    out = np.full((N, G), np.nan)
    for r in range(N):
        for j in range(G):
            l = label[r, j]
            if l < 0:
                continue
            v = raw[r, j]
            if np.isfinite(v):
                out[r, j] = v + unwrap[l] * nyq2
    return out, nreg


def main():
    if len(sys.argv) != 7:
        print(__doc__)
        sys.exit(1)
    site = sys.argv[1].upper()
    t = datetime.datetime(*[int(x) for x in sys.argv[2:7]])
    key = nearest(list_keys(site, t.date()), t)
    print("Volume:", key.rsplit("/", 1)[-1])
    import pyart
    data = download(key)
    tmp = tempfile.NamedTemporaryFile(suffix=".ar2v", delete=False); tmp.write(data); tmp.close()
    try:
        radar = pyart.io.read_nexrad_archive(tmp.name)
    finally:
        os.unlink(tmp.name)
    vd = radar.fields["velocity"]["data"]
    vs = next(s for s in range(radar.nsweeps) if np.ma.count(vd[radar.get_slice(s)]) > 0)
    sl = radar.get_slice(vs)
    nyq = float(radar.instrument_parameters["nyquist_velocity"]["data"][sl][0])
    raw = np.ma.filled(vd[sl].astype(float), np.nan)
    print("velocity sweep %d  Nyquist %.2f m/s (%.0f mph)" % (vs, nyq, nyq * 2.237))

    mine, nreg = dealias_v2(raw, nyq)
    truth = np.ma.filled(pyart.correct.dealias_region_based(radar, nyquist_vel=nyq)["data"][sl].astype(float), np.nan)
    fin = np.isfinite(mine) & np.isfinite(truth)
    agree = float(np.mean(np.abs(mine[fin] - truth[fin]) < nyq))
    print("app-mirror  regions=%d  range %.0f..%.0f m/s" % (nreg, np.nanmin(mine), np.nanmax(mine)))
    print("Py-ART      range %.0f..%.0f m/s" % (np.nanmin(truth), np.nanmax(truth)))
    print("agreement (same fold, |diff| < Nyquist): %.2f%% of %d gates" % (100 * agree, fin.sum()))

    gx = radar.gate_x["data"][sl] / 1000.0; gy = radar.gate_y["data"][sl] / 1000.0
    rng = np.hypot(gx, gy); box = (rng > 5) & (rng < 60)
    tb = np.ma.masked_where(~box | ~np.isfinite(truth), truth)
    ci = np.unravel_index(np.ma.argmin(tb), tb.shape)
    print("couplet core @ %.0f km:  Py-ART %+.0f mph | app-mirror %+.0f mph | raw %+.0f m/s"
          % (rng[ci], truth[ci] * 2.237, mine[ci] * 2.237, raw[ci]))


if __name__ == "__main__":
    main()
