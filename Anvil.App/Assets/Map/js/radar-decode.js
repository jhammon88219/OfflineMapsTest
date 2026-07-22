// Shared NEXRAD Level II decode + gate-geometry build. No DOM / GL / host dependencies,
// so it runs inside a Web Worker (radar-worker.js) as well as on the main thread (radar.js
// fallback). The heavy cost here is the pure-JS bzip2 decompression inside Level2Radar
// (~5 s for a full volume), which is exactly why we run it off the UI thread.

import { REFLECTIVITY_RAMP, VELOCITY_RAMP, CORRELATION_RAMP, KDP_RAMP, ZDR_RAMP, SPECTRUM_WIDTH_RAMP, rampColor } from './radar-ramps.js';
import { PRODUCTS, PRODUCT_IDS } from './radar-products.js';
import { metersPerDeg } from './geo.js';

const HALF_BEAM_DEG = 0.5; // half the super-res azimuthal spacing (~1° beam)
const D2R = Math.PI / 180;
const GRID_NODATA = -32768; // sentinel in the inspector value grid (Int16) for "no data here"

// Color scales live in radar-ramps.js (shared with the eventual legend) — see rampColor below.

// Lazily import the vendored decoder once. We use the decoder's OWN Buffer class
// (re-exported from the bundle): its constructor gates on `input instanceof <its Buffer>`,
// so a Buffer from any other module is rejected ("Unknown data provided"). The decoder also
// has a couple of process.* guards; satisfy them before importing.
let decoderPromise = null;
function loadDecoder() {
    if (!decoderPromise) {
        globalThis.process = globalThis.process || { env: {}, browser: true };
        decoderPromise = import('../vendor/nexrad-level-2-data.esm.js').then(function (mod) {
            return { Buffer: mod.Buffer, Level2Radar: mod.Level2Radar };
        });
    }
    return decoderPromise;
}

// Per-elevation radials for one moment, normalized to the { moment_data, first_gate (km),
// gate_size (km) } shape buildGates/buildGrid/dealias expect — bridging the two on-disk formats:
//   • Message 31 (super-res, 2008+): radar.getHighres*() already returns that object per radial,
//     so this just hands those through unchanged.
//   • Message 1 (legacy, pre-2008): the vendored decoder stores the moment as a FLAT value array
//     on the record (record.reflect = dBZ[], record.velocity = m/s[]) with the range geometry in
//     SEPARATE record fields (surveillance_/doppler_range + *_sample_interval, in km). We wrap
//     those into the same object shape so the rest of the pipeline is format-agnostic.
// Azimuth is read identically for both (radar.getAzimuth(i) -> record.azimuth), so callers keep
// using getAzimuth. `moment` is 'reflect' | 'velocity' | 'rho'.
function momentRadials(radar, moment) {
    const elev = radar.elevation;
    const scans = radar.data && radar.data[elev];
    if (!scans || !scans.length) return [];
    return scans.map(function (s) {
        const rec = s && s.record;
        if (!rec) return null;
        const m = rec[moment];
        if (m === null || m === undefined) return null;
        if (Array.isArray(m)) {
            // Legacy Message 1: flat array + per-record range fields. CC/ρHV doesn't exist here
            // (single-pol), so only 'reflect'/'velocity' ever reach this branch.
            const isRefl = moment === 'reflect';
            return {
                moment_data: m,
                first_gate: isRefl ? rec.surveillance_range : rec.doppler_range,
                gate_size: isRefl ? rec.surveillance_range_sample_interval : rec.doppler_range_sample_interval,
            };
        }
        return m; // Message 31 moment object { moment_data, first_gate, gate_size, ... }
    });
}

// Builds gate-quad geometry from a list of radials: mercator x,y per vertex + rgba per vertex
// (2 triangles per gate). `colorFn(value)` returns [r,g,b] for a gate, or null to skip it
// (no-data / below threshold). Returns null if nothing is drawn. Shared by reflectivity and
// velocity so the geometry math stays identical.
function buildGates(radials, getAzimuth, siteLat, siteLon, colorFn) {
    if (!radials || !radials.length) return null;
    const { mPerDegLat, mPerDegLon } = metersPerDeg(siteLat); // canonical projection — see geo.js (per-gate formula stays inline below for perf)
    const positions = [];
    const colors = [];
    const PI = Math.PI;
    function my(lat) {
        return (180 - (180 / PI) * Math.log(Math.tan(PI / 4 + lat * PI / 360))) / 360;
    }

    for (let i = 0; i < radials.length; i++) {
        const d = radials[i];
        if (!d || !d.moment_data) continue;
        const az = getAzimuth(i);
        if (typeof az !== 'number') continue;

        // This decoder reports gate geometry in KILOMETRES; moment_data is already in physical
        // units (dBZ for reflectivity, m/s for velocity; null = no data).
        const firstGate = d.first_gate; // km to first gate
        const gateSize = d.gate_size;   // km per gate
        const data = d.moment_data;
        if (!isFinite(firstGate) || !isFinite(gateSize)) continue;

        const firstGateM = firstGate * 1000;
        const gateSizeM = gateSize * 1000;
        const azL = (az - HALF_BEAM_DEG) * D2R, azR = (az + HALF_BEAM_DEG) * D2R;
        const sinL = Math.sin(azL), cosL = Math.cos(azL);
        const sinR = Math.sin(azR), cosR = Math.cos(azR);

        for (let j = 0; j < data.length; j++) {
            const col = colorFn(data[j]);
            if (!col) continue;

            const rNear = firstGateM + j * gateSizeM;
            const rFar = rNear + gateSizeM;

            // Four corners (near-left, far-left, far-right, near-right) -> mercator x,y.
            const xnL = (180 + siteLon + (rNear * sinL) / mPerDegLon) / 360;
            const ynL = my(siteLat + (rNear * cosL) / mPerDegLat);
            const xfL = (180 + siteLon + (rFar * sinL) / mPerDegLon) / 360;
            const yfL = my(siteLat + (rFar * cosL) / mPerDegLat);
            const xfR = (180 + siteLon + (rFar * sinR) / mPerDegLon) / 360;
            const yfR = my(siteLat + (rFar * cosR) / mPerDegLat);
            const xnR = (180 + siteLon + (rNear * sinR) / mPerDegLon) / 360;
            const ynR = my(siteLat + (rNear * cosR) / mPerDegLat);

            const r = col[0], g = col[1], b = col[2];
            // Two triangles: nL,fL,fR and nL,fR,nR
            positions.push(xnL, ynL, xfL, yfL, xfR, yfR, xnL, ynL, xfR, yfR, xnR, ynR);
            colors.push(r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255);
        }
    }

    if (!positions.length) return null;
    return {
        positions: new Float32Array(positions),
        colors: new Uint8Array(colors),
        count: positions.length / 2,
    };
}

// Builds the INSPECTOR value grid for a sweep: a compact polar lookup the host (radar.js) indexes
// by cursor position to read the moment value under the pointer (RadarScope-style). It's the raw
// per-gate values laid out [radial][gate], so the main thread can do range/azimuth -> value with no
// re-decode and no GL readback. Values are quantized to Int16 (scale carried alongside) so the whole
// grid transfers zero-copy and stays small; GRID_NODATA marks empty gates. `firstGate`/`gateSize` are
// in km (one representative pair — uniform within a sweep). `unit`/`digits` drive the tooltip text.
// NOTE: unlike the rendered geometry this is NOT thresholded/masked — the inspector reports the true
// measured value at any gate that has data (e.g. dBZ below the display threshold).
// wantValues (default true) gates the HEAVY part: the Int16 values + Float32 azimuth arrays (the
// per-gate inspector data, ~Int16 N×G per product per frame). The scalar range metadata
// (firstGate/gateSize/nGates) is always cheap to compute and is what the range ring needs, so it's
// returned regardless; the value arrays are built only when the inspector is on (see decodeAndBuild's
// buildGrids). A metadata-only grid returns az:null / values:null.
function buildGrid(radials, getAzimuth, scale, unit, digits, wantValues) {
    if (wantValues === undefined) wantValues = true;
    if (!radials || !radials.length) return null;
    const N = radials.length;
    let G = 0, fg = NaN, gs = NaN;
    for (let i = 0; i < N; i++) {
        const d = radials[i];
        if (d && d.moment_data) {
            if (d.moment_data.length > G) G = d.moment_data.length;
            if (!isFinite(fg)) { fg = d.first_gate; gs = d.gate_size; }
        }
    }
    if (!G || !isFinite(fg) || !isFinite(gs) || !(gs > 0)) return null;

    // Metadata-only (inspector off): skip the ~Int16 N×G allocation entirely. rangeMeters still works.
    if (!wantValues) {
        return { az: null, firstGate: fg, gateSize: gs, nGates: G, values: null, scale: scale, unit: unit, digits: digits };
    }

    const az = new Float32Array(N);
    const values = new Int16Array(N * G);
    values.fill(GRID_NODATA);
    for (let i = 0; i < N; i++) {
        const a = getAzimuth(i);
        az[i] = (typeof a === 'number') ? a : NaN;
        const md = radials[i] && radials[i].moment_data;
        if (!md) continue;
        for (let j = 0; j < md.length; j++) {
            const v = md[j];
            if (v === null || v === undefined || !isFinite(v)) continue;
            let q = Math.round(v * scale);
            if (q <= GRID_NODATA) q = GRID_NODATA + 1; else if (q > 32767) q = 32767;
            values[i * G + j] = q;
        }
    }
    return { az: az, firstGate: fg, gateSize: gs, nGates: G, values: values, scale: scale, unit: unit, digits: digits };
}

// Lowest-tilt reflectivity geometry (gates >= minDbz) + the inspector value grid. Reflectivity always
// lives at the lowest elevation NUMBER present (the surveillance cut), which the C# extractor writes
// first. Returns { geom, grid }: geom may be null (nothing above threshold) while grid still has data.
function buildReflectivity(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevations = radar.listElevations();
    if (!elevations || !elevations.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevations));
    const radials = momentRadials(radar, 'reflect');
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(radials, getAz, siteLat, siteLon, function (dbz) {
        if (dbz === null || dbz === undefined || dbz < minDbz) return null;
        return rampColor(REFLECTIVITY_RAMP, dbz);
    });
    return { geom: geom, grid: buildGrid(radials, getAz, 10, REFLECTIVITY_RAMP.unit, 1, wantGrid) };
}

// The elevation (number) carrying velocity. In a split-cut precip VCP that's the Doppler
// companion (a higher number than the surveillance cut); in clear-air it's the single combined
// cut. Returns null if no velocity is present (e.g. an older file without the Doppler cut).
function findVelocityElevation(radar) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return null;
    for (let k = 0; k < elevs.length; k++) {
        try {
            radar.setElevation(elevs[k]);
            const arr = momentRadials(radar, 'velocity');
            for (let i = 0; i < arr.length; i++) {
                if (arr[i] && arr[i].moment_data) return elevs[k];
            }
        } catch (e) { /* try the next elevation */ }
    }
    return null;
}

// Azimuth coverage of a sweep: how many radials carry data and the angular span they cover.
// A full sweep spans ~360°; a partial/in-progress Doppler cut shows up here as a small span
// (e.g. ~90° = the "quarter circle" wedge), which is the fastest way to spot a bugged frame.
function sweepStats(radials, getAzimuth) {
    let rad = 0, lo = Infinity, hi = -Infinity, gates = 0;
    for (let i = 0; i < radials.length; i++) {
        const d = radials[i];
        if (!d || !d.moment_data) continue;
        const az = getAzimuth(i);
        if (typeof az !== 'number') continue;
        rad++;
        if (az < lo) lo = az;
        if (az > hi) hi = az;
        if (!gates) gates = d.moment_data.length;
    }
    if (!rad) return { rad: 0, azLo: 0, azHi: 0, span: 0, gates: 0 };
    return { rad: rad, azLo: Math.round(lo), azHi: Math.round(hi), span: Math.round(hi - lo), gates: gates };
}

// Last sweep's dealiasing diagnostics (region count, seed mean, global shift, value range),
// surfaced into the debug log so the unfold can be verified without guessing at the picture.
let _dealiasInfo = '';

// Per-radial Nyquist velocity (m/s) from the decoded RAD block; NaN if unavailable. The unfold
// interval for dealiasing is 2*Nyquist, so this is the one value the whole thing hinges on.
// NOTE: this decoder path reports the field in cm/s (e.g. 2584 = 25.84 m/s), NOT m/s — using it
// raw made 2*Nyquist ~5168, so round((ref-raw)/2Nyq) was always 0 and NOTHING ever unfolded
// (velocity rendered raw/aliased: red where strong inbound should read bright green). Real m/s
// Nyquists are < ~70, so normalize anything above 100 by /100.
// `out` (optional {src}) reports which field the value came from: 'rad' = the RAD per-radial sub-block
// (the CORRECT per-cut Nyquist Py-ART/RadarScope use), 'vol' = the coarser VOLUME-level fallback,
// 'none' = unavailable. Selection is unchanged (RAD when present, else VOL) — this only OBSERVES it.
function nyquistForRadial(radar, i, out) {
    try {
        const rec = radar.data[radar.elevation][i] && radar.data[radar.elevation][i].record;
        if (!rec) { if (out) out.src = 'none'; return NaN; }
        // Message 31 (super-res) carries Nyquist in the RAD sub-block (cm/s); legacy Message 1 puts
        // it on the record itself, already in m/s (the decoder divides by 100 there). The >100 guard
        // below normalizes whichever path supplied cm/s, and leaves an already-m/s value alone.
        // ⚠️ The RAD (per-radial) and VOL (volume-level) blocks can hold DIFFERENT Nyquists — using
        // the VOL fallback (~2 m/s higher) corrupts the fold arithmetic and is the suspected cause of
        // couplet mis-folds in the live path (`out.src` surfaces this to the diagnostics log).
        let nv, src;
        if (rec.radial && typeof rec.radial.nyquist_velocity === 'number') {
            nv = rec.radial.nyquist_velocity; src = 'rad';
        } else {
            nv = rec.nyquist_velocity; src = 'vol';
        }
        if (typeof nv !== 'number' || !(nv > 1)) { if (out) out.src = 'none'; return NaN; }
        if (nv > 100) nv /= 100; // cm/s -> m/s
        if (out) out.src = src;
        return nv;
    } catch (e) { if (out) out.src = 'none'; return NaN; }
}

// The sweep's representative Nyquist (median of valid per-radial values), or NaN if none.
function sweepNyquist(radar, count) {
    const v = [];
    for (let i = 0; i < count; i++) {
        const n = nyquistForRadial(radar, i);
        if (isFinite(n)) v.push(n);
    }
    if (!v.length) return NaN;
    v.sort(function (a, b) { return a - b; });
    return v[v.length >> 1];
}

// Instrumentation companion to sweepNyquist: the same median value PLUS which source it came from and
// the median of each candidate (RAD per-radial vs VOL volume-level). A 'vol' src on a sweep means the
// per-radial cut Nyquist was unavailable and the dealiaser fell back to the coarser volume value — the
// suspected root of live-path couplet fold misses. Cheap; runs once per decoded velocity sweep.
function sweepNyquistDetail(radar, count) {
    const all = [], radV = [], volV = [];
    let radN = 0, volN = 0;
    const out = {};
    for (let i = 0; i < count; i++) {
        const v = nyquistForRadial(radar, i, out);
        if (out.src === 'rad') { radN++; if (isFinite(v)) radV.push(v); }
        else if (out.src === 'vol') { volN++; if (isFinite(v)) volV.push(v); }
        if (isFinite(v)) all.push(v);
    }
    const med = function (a) { if (!a.length) return NaN; a = a.slice().sort(function (x, y) { return x - y; }); return a[a.length >> 1]; };
    const src = radN && volN ? 'mixed' : radN ? 'rad' : volN ? 'vol' : 'none';
    return { med: med(all), src: src, radMed: med(radV), volMed: med(volV), radN: radN, volN: volN };
}

// Region-based velocity DEALIASING (v2 — a port of Py-ART's region_based algorithm, a "dynamic
// network reduction"). The previous v1 (grow regions where adjacent gates differ by < Nyquist, then a
// guarded MST + VAD anchor) COULD NOT UNFOLD A VIOLENT COUPLET: when both sides of a couplet exceed
// Nyquist they FOLD to similar raw values (Moore-2013: true -60 and +40 m/s both land near raw -8..-12
// at Nyquist 26), so the raw field is SMOOTH across the couplet — v1 merged the folded core into the
// ambient wind and rendered it ~raw (capped near Nyquist). Validated gate-for-gate against Py-ART's
// dealiaser, v2 recovers the Moore-2013 (-135 mph) and Bridge Creek-1999 (-100 mph) couplet cores
// exactly. All single-sweep, no external data. Steps:
//   1. SEGMENT: split the Nyquist interval into INTERVAL_SPLITS bands and label connected components
//      WITHIN each band. Binning (vs v1's grow-by-difference) is the crux — a folded couplet core sits
//      in a different band than the ambient wind, so it forms its OWN region instead of merging in.
//   2. EDGES: between adjacent regions accumulate weight (# adjacent gate pairs) + summed velocity
//      difference (in 2·Nyquist units).
//   3. REDUCE: repeatedly merge the two regions joined by the HEAVIEST boundary, unfolding the smaller
//      by the whole number of 2·Nyquist that best fits their shared boundary. Parallel edges to shared
//      neighbours COMBINE as regions merge, so evidence accumulates and one weak/noisy boundary can't
//      mis-fold a subtree — which is why v2 needs NONE of v1's fold cap / foldTol / min-region guards.
//   4. CENTER: shift all fold counts so the gate-weighted mean fold is ~0 (the sweep's absolute anchor).
// Input unchanged if no Nyquist is available.
const INTERVAL_SPLITS = 3; // Py-ART default; splitting the Nyquist interval finer separates folds better
const EDGE_SKIP = 100;     // Py-ART default skip_along_ray/skip_between_rays: bridge gaps of up to this
                           // many masked gates when finding region edges (see the edge loop below)

// Wrapper: on any unexpected error, fall back to the raw (folded) radials rather than blanking the
// whole velocity frame — same graceful degradation as "no Nyquist available".
function dealiasSweep(radials, radar) {
    try { return dealiasSweepCore(radials, radar); }
    catch (e) { _dealiasInfo = 'dealias error: ' + (e && e.message ? e.message : e); return radials; }
}

function dealiasSweepCore(radials, radar) {
    const med = sweepNyquist(radar, radials.length);
    if (!isFinite(med)) return radials;

    const N = radials.length;
    let G = 0;
    for (let r = 0; r < N; r++) {
        const md = radials[r] && radials[r].moment_data;
        if (md && md.length > G) G = md.length;
    }
    if (!N || !G) return radials;

    const nyq = med, nyq2 = 2 * med;
    const idx = function (r, j) { return r * G + j; };
    function val(r, j) {
        const md = radials[r] && radials[r].moment_data;
        if (!md || j >= md.length) return NaN;
        const v = md[j];
        return (v === null || v === undefined || !isFinite(v)) ? NaN : v;
    }

    // --- 1. segment into bands, connected-component per band (label: <0 = masked/unlabeled) ---
    // Band edges cover [-Nyq, Nyq]; extend outward if any gate reads slightly past Nyquist so every
    // valid gate lands in a band.
    let vLo = Infinity, vHi = -Infinity;
    for (let r = 0; r < N; r++) {
        const md = radials[r] && radials[r].moment_data; if (!md) continue;
        for (let j = 0; j < md.length; j++) {
            const v = md[j];
            if (v === null || v === undefined || !isFinite(v)) continue;
            if (v < vLo) vLo = v; if (v > vHi) vHi = v;
        }
    }
    const interval = nyq2 / INTERVAL_SPLITS;
    const addStart = vHi > nyq ? Math.ceil((vHi - nyq) / interval) : 0;
    const addEnd = vLo < -nyq ? Math.ceil((-(vLo + nyq)) / interval) : 0;
    const bandStart = -nyq - addEnd * interval;
    const nBands = INTERVAL_SPLITS + addStart + addEnd;

    const label = new Int32Array(N * G).fill(-1);
    const regionCnt = [];
    const stack = [];
    for (let b = 0; b < nBands; b++) {
        const lmin = bandStart + b * interval, lmax = lmin + interval;
        for (let r0 = 0; r0 < N; r0++) {
            for (let j0 = 0; j0 < G; j0++) {
                if (label[idx(r0, j0)] !== -1) continue;
                const v0 = val(r0, j0);
                if (!isFinite(v0) || v0 < lmin || v0 >= lmax) continue;
                const rid = regionCnt.length; regionCnt.push(0);
                stack.length = 0; stack.push(r0, j0); label[idx(r0, j0)] = rid;
                while (stack.length) {
                    const j = stack.pop(), r = stack.pop();
                    regionCnt[rid]++;
                    const nb = [[r, j - 1], [r, j + 1], [(r - 1 + N) % N, j], [(r + 1) % N, j]];
                    for (let k = 0; k < 4; k++) {
                        const rr = nb[k][0], jj = nb[k][1];
                        if (jj < 0 || jj >= G) continue;
                        const id2 = idx(rr, jj);
                        if (label[id2] !== -1) continue;
                        const vv = val(rr, jj);
                        if (isFinite(vv) && vv >= lmin && vv < lmax) { label[id2] = rid; stack.push(rr, jj); }
                    }
                }
            }
        }
    }
    const numReg = regionCnt.length;
    if (numReg < 2) return radials;

    // --- 2. edges: g[a] = Map(b -> [weight, sumdiff]) where sumdiff = Σ (v_a - v_b)/nyq2 over the
    // shared boundary (kept symmetric: g[b].get(a) mirrors it with negated sumdiff). ---
    const g = new Array(numReg);
    for (let i = 0; i < numReg; i++) g[i] = new Map();
    function addEdge(a, b, dv) { // dv = v_a - v_b (raw m/s)
        let e = g[a].get(b);
        if (!e) { e = [0, 0]; g[a].set(b, e); const f = [0, 0]; g[b].set(a, f); e.mate = f; f.mate = e; }
        e[0]++; e[1] += dv / nyq2;
        const f = e.mate; f[0]++; f[1] -= dv / nyq2;
    }
    // ⚠️ GAP-SKIPPING (Py-ART skip_along_ray/skip_between_rays, default 100): when the next gate is
    // masked, look PAST up to EDGE_SKIP masked gates to the next labelled region and connect to it. Without
    // this, sparse FAR-RANGE regions (separated from the main body by data gaps) stay disconnected, get no
    // edge, and after centering land on the wrong absolute fold — measured KLVX 2026-07-21: only 87%
    // agreement with Py-ART (a uniform −1 fold beyond ~120 km), fixed to 100% by bridging the gaps. Dense
    // data is unaffected: the scan finds the adjacent gate immediately (no skip), so directly-touching
    // regions edge exactly as before (Moore-2013 couplet unchanged, core −135 mph).
    for (let r = 0; r < N; r++) {
        for (let j = 0; j < G; j++) {
            const la = label[idx(r, j)];
            if (la < 0) continue;
            const va = val(r, j);
            // Along the ray: next labelled gate to the right, skipping up to EDGE_SKIP masked gates.
            let jj = j + 1, s = 0;
            while (jj < G && label[idx(r, jj)] < 0 && s < EDGE_SKIP) { jj++; s++; }
            if (jj < G) { const lb = label[idx(r, jj)]; if (lb >= 0 && lb !== la) addEdge(la, lb, va - val(r, jj)); }
            // Across rays: next labelled gate downward (+wrap), skipping up to EDGE_SKIP masked rays.
            let rr = (r + 1) % N; s = 0;
            while (rr !== r && label[idx(rr, j)] < 0 && s < EDGE_SKIP) { rr = (rr + 1) % N; s++; }
            if (rr !== r) { const lb = label[idx(rr, j)]; if (lb >= 0 && lb !== la) addEdge(la, lb, va - val(rr, j)); }
        }
    }

    // --- 3. dynamic network reduction: merge the heaviest boundary first, combining parallel edges. A
    // binary max-heap orders merges by (current) weight; entries are lazily validated on pop (an entry
    // is stale if either node died or the live edge weight no longer matches). ---
    const heap = []; // array of [weight, a, b]
    function heapPush(w, a, b) {
        heap.push([w, a, b]); let i = heap.length - 1;
        while (i > 0) { const p = (i - 1) >> 1; if (heap[p][0] >= heap[i][0]) break; const t = heap[p]; heap[p] = heap[i]; heap[i] = t; i = p; }
    }
    function heapPop() {
        if (!heap.length) return null;
        const top = heap[0], last = heap.pop();
        if (heap.length) { heap[0] = last; let i = 0; const n = heap.length;
            for (;;) { let l = 2 * i + 1, rr = l + 1, m = i;
                if (l < n && heap[l][0] > heap[m][0]) m = l;
                if (rr < n && heap[rr][0] > heap[m][0]) m = rr;
                if (m === i) break; const t = heap[m]; heap[m] = heap[i]; heap[i] = t; i = m; } }
        return top;
    }
    for (let a = 0; a < numReg; a++) g[a].forEach(function (e, b) { if (b > a) heapPush(e[0], a, b); });

    const alive = new Uint8Array(numReg).fill(1);
    const size = Int32Array.from(regionCnt);
    const unwrap = new Int32Array(numReg);            // fold count applied to each ORIGINAL region
    const regionsIn = new Array(numReg);              // original regions currently inside each node
    for (let i = 0; i < numReg; i++) regionsIn[i] = [i];

    function doUnwrap(node, nw) {
        if (!nw) return;
        const mem = regionsIn[node];
        for (let i = 0; i < mem.length; i++) unwrap[mem[i]] += nw;
        g[node].forEach(function (e, nb) { e[1] += e[0] * nw; e.mate[1] -= e[0] * nw; });
    }

    while (heap.length) {
        const top = heapPop();
        const w = top[0], a = top[1], b = top[2];
        if (!alive[a] || !alive[b]) continue;
        const e = g[a].get(b);
        if (!e || e[0] !== w) continue; // stale heap entry (weight changed by a combine)
        const rdiff = Math.round(e[1] / e[0]);
        let base, mrg, nw;
        if (size[a] >= size[b]) { base = a; mrg = b; nw = rdiff; }
        else { base = b; mrg = a; nw = -rdiff; }
        doUnwrap(mrg, nw);
        // detach the base<->mrg edge, then fold mrg's other edges into base (combining parallels)
        g[base].delete(mrg); g[mrg].delete(base);
        g[mrg].forEach(function (e2, nb) {
            g[nb].delete(mrg);
            const be = g[base].get(nb);
            if (be) { be[0] += e2[0]; be[1] += e2[1]; be.mate[0] += e2[0]; be.mate[1] -= e2[1]; heapPush(be[0], base, nb); }
            else {
                const f = [e2[0], -e2[1]]; const ne = [e2[0], e2[1]];
                ne.mate = f; f.mate = ne; g[base].set(nb, ne); g[nb].set(base, f); heapPush(ne[0], base, nb);
            }
        });
        g[mrg] = new Map();
        const bm = regionsIn[base], mm = regionsIn[mrg];
        for (let i = 0; i < mm.length; i++) bm.push(mm[i]);
        regionsIn[mrg] = null;
        size[base] += size[mrg]; alive[mrg] = 0;
    }

    // --- 4. center: shift every fold so the gate-weighted mean fold is ~0 (the absolute anchor) ---
    let totalGates = 0, totalFolds = 0;
    for (let i = 0; i < numReg; i++) { totalGates += regionCnt[i]; totalFolds += regionCnt[i] * unwrap[i]; }
    const off = totalGates ? Math.round(totalFolds / totalGates) : 0;
    if (off) for (let i = 0; i < numReg; i++) unwrap[i] -= off;

    // --- 5. apply per-region fold ---
    let vmin = Infinity, vmax = -Infinity, hi = 0, tot = 0;
    const out = new Array(N);
    for (let r = 0; r < N; r++) {
        const src = radials[r];
        if (!src || !src.moment_data) { out[r] = src; continue; }
        const data = src.moment_data;
        const mdOut = new Array(data.length);
        for (let j = 0; j < data.length; j++) {
            const lid = label[idx(r, j)];
            const v = val(r, j);
            if (lid < 0 || !isFinite(v)) { mdOut[j] = null; continue; }
            const dv = v + unwrap[lid] * nyq2;
            mdOut[j] = dv;
            tot++;
            if (dv < vmin) vmin = dv;
            if (dv > vmax) vmax = dv;
            if (dv > 55 || dv < -55) hi++; // implausibly fast at 0.5° => over-unfolded / noise
        }
        out[r] = { moment_data: mdOut, first_gate: src.first_gate, gate_size: src.gate_size };
    }
    _dealiasInfo = numReg + 'reg splits' + INTERVAL_SPLITS +
        ' v[' + (isFinite(vmin) ? Math.round(vmin) : '?') + ',' +
        (isFinite(vmax) ? Math.round(vmax) : '?') + '] hi=' + hi + '/' + tot;
    return out;
}

// Lowest-tilt base-velocity geometry (dealiased) + the inspector value grid (also from the DEALIASED
// radials, so the inspected value matches the rendered pixel). Returns { geom, grid }; both null if
// the volume carries no velocity.
// minDbz is accepted (unused) so every builder shares ONE signature and decodeAndBuild can call them
// uniformly through the BUILDERS map — velocity isn't reflectivity-masked (unlike CC/DOW-velocity).
function buildVelocity(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elev = findVelocityElevation(radar);
    if (elev === null) return { geom: null, grid: null };
    radar.setElevation(elev);
    const radials = momentRadials(radar, 'velocity');
    const dealiased = dealiasSweep(radials, radar);
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(dealiased, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(VELOCITY_RAMP, v);
    });
    return { geom: geom, grid: buildGrid(dealiased, getAz, 10, VELOCITY_RAMP.unit, 1, wantGrid) };
}

// Lowest-tilt SPECTRUM WIDTH (m/s) — the spread of velocities within a gate (turbulence / shear). It's a
// Doppler moment, so it lives in the SAME cut as velocity (found via findVelocityElevation), NOT the
// surveillance cut. Unlike velocity there is NO dealiasing (width is a magnitude, not a folded velocity),
// and no reflectivity mask — like velocity it's shown wherever the Doppler cut has data (the cut itself
// restricts it to real returns). Null if the volume has no Doppler cut / no spectrum-width moment.
function buildSpectrumWidth(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elev = findVelocityElevation(radar); // spectrum width rides the Doppler (velocity) cut
    if (elev === null) return { geom: null, grid: null };
    radar.setElevation(elev);
    const radials = momentRadials(radar, 'spectrum');
    if (!radials.some(function (s) { return s && s.moment_data; })) return { geom: null, grid: null };
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(radials, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(SPECTRUM_WIDTH_RAMP, v);
    });
    return { geom: geom, grid: buildGrid(radials, getAz, 100, SPECTRUM_WIDTH_RAMP.unit, 1, wantGrid) };
}

// Lowest-tilt correlation-coefficient (ρHV) geometry. CC is a dual-pol moment collected in the
// long-PRT SURVEILLANCE cut alongside reflectivity, so it lives at the lowest elevation NUMBER (like
// reflectivity), NOT the Doppler cut. Null if the volume carries no CC (legacy single-pol file).
//
// CC is MASKED BY REFLECTIVITY: it's only meaningful where there's actual precip signal. Without the
// mask, clear-air ground clutter / biological / noise returns carry real-but-random low CC and paint
// the whole domain with colorful speckle (RadarScope masks it the same way). We keep a CC gate only
// where the co-located reflectivity gate is >= minDbz — aligned by RANGE, since CC and reflectivity
// can use different gate geometry. The result shows CC exactly where the reflectivity product draws.
function buildCorrelation(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const ccR = momentRadials(radar, 'rho');
    const reflR = momentRadials(radar, 'reflect');
    // Legacy Message-1 (single-pol) volumes have no ρHV at all, so bail before building anything.
    if (!ccR.some(function (c) { return c && c.moment_data; })) return { geom: null, grid: null };

    // CC is only meaningful where there's precip — mask it to reflectivity >= minDbz (shared with the
    // DOW velocity mask). Without it, clear-air / clutter ρHV speckles the whole domain.
    const masked = maskByReflectivity(ccR, reflR, minDbz);

    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(masked, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(CORRELATION_RAMP, v);
    });
    // The inspector grid uses the UNMASKED ρHV (ccR), so the cursor reads the true value anywhere
    // there's signal — not only where the reflectivity-masked geometry draws.
    return { geom: geom, grid: buildGrid(ccR, getAz, 1000, CORRELATION_RAMP.unit, 2, wantGrid) };
}

// Lowest-tilt DIFFERENTIAL REFLECTIVITY (ZDR, dB) — dual-pol, collected in the SURVEILLANCE cut
// alongside reflectivity/CC (lowest elevation NUMBER). A DIRECT moment read (not derived, unlike KDP),
// so this is an exact analog of buildCorrelation: read `zdr`, mask the DISPLAY to reflectivity >= minDbz
// (clear-air ZDR is meaningless noise), color by ZDR_RAMP; the inspector grid keeps the UNMASKED value.
// Null on a legacy single-pol volume (no ZDR).
function buildZdr(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const zdrR = momentRadials(radar, 'zdr');
    const reflR = momentRadials(radar, 'reflect');
    // Legacy single-pol volumes carry no ZDR — bail before building anything.
    if (!zdrR.some(function (z) { return z && z.moment_data; })) return { geom: null, grid: null };

    const masked = maskByReflectivity(zdrR, reflR, minDbz);
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(masked, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(ZDR_RAMP, v);
    });
    // Inspector reads the UNMASKED ZDR (dB); scale 100 → 0.01 dB quantization.
    return { geom: geom, grid: buildGrid(zdrR, getAz, 100, ZDR_RAMP.unit, 2, wantGrid) };
}

// Gate index in `to`'s geometry that co-locates (by RANGE, km) with gate j of `from` — the alignment
// used to read one moment's value at another moment's gate (they can have different first_gate/gate_size).
function rangeIndexOf(from, to, j) {
    return Math.round((from.first_gate + j * from.gate_size - to.first_gate) / to.gate_size);
}

// KDP quality/tuning constants (see buildKdp). Fixed 3 km least-squares window is a robust v1; an
// adaptive-by-Z window is a later refinement.
const KDP_RHO_MIN = 0.85;   // gates below this ρHV have too-noisy differential phase to trust
const KDP_WINDOW_KM = 3.0;  // half-length (km) of the ΦDP least-squares range window
const KDP_MIN_VALID = 5;    // min valid samples in a window to estimate a slope

// KDP (°/km) along ONE radial, derived from its ΦDP samples: unwrap the ~360° fold, drop low-quality
// gates (ρHV < KDP_RHO_MIN, or reflectivity < minDbz — aligned by range), then a fixed-window
// least-squares slope of ΦDP vs range; KDP = ½·slope. Returns a value array (null where KDP can't be
// estimated), the same length/geometry as ΦDP so buildGates/buildGrid consume it like any moment.
function kdpFromPhi(phi, refl, rho, minDbz) {
    const pd = phi.moment_data, n = pd.length, gateKm = phi.gate_size;
    const ph = new Float64Array(n);     // unwrapped ΦDP (deg) at valid gates
    const valid = new Uint8Array(n);
    let prev = null, accum = 0;
    for (let j = 0; j < n; j++) {
        let v = pd[j];
        let ok = (v !== null && v !== undefined);
        if (ok && rho && rho.moment_data) {
            const rj = rangeIndexOf(phi, rho, j);
            const rv = (rj >= 0 && rj < rho.moment_data.length) ? rho.moment_data[rj] : null;
            if (rv === null || rv === undefined || rv < KDP_RHO_MIN) ok = false;
        }
        if (ok && refl && refl.moment_data) {
            const zj = rangeIndexOf(phi, refl, j);
            const zv = (zj >= 0 && zj < refl.moment_data.length) ? refl.moment_data[zj] : null;
            if (zv === null || zv === undefined || zv < minDbz) ok = false;
        }
        if (ok) {
            if (prev !== null) { // cumulative unwrap of the ~360° ΦDP fold (compare to last RAW valid value)
                const d = v - prev;
                if (d > 180) accum -= 360; else if (d < -180) accum += 360;
            }
            prev = v;
            ph[j] = v + accum;
            valid[j] = 1;
        } else {
            ph[j] = NaN;
            // keep `prev`/`accum` across isolated dropouts so the unwrap stays continuous
        }
    }
    const w = Math.max(1, Math.round(KDP_WINDOW_KM / gateKm));
    const out = new Array(n);
    for (let i = 0; i < n; i++) {
        if (!valid[i]) { out[i] = null; continue; }
        let lo = i - w, hi = i + w;
        if (lo < 0) lo = 0;
        if (hi >= n) hi = n - 1;
        // Least-squares slope of ΦDP vs range over the window's valid gates (x offset cancels).
        let sx = 0, sy = 0, sxx = 0, sxy = 0, cnt = 0;
        for (let k = lo; k <= hi; k++) {
            if (!valid[k]) continue;
            const x = k * gateKm, y = ph[k];
            sx += x; sy += y; sxx += x * x; sxy += x * y; cnt++;
        }
        if (cnt < KDP_MIN_VALID) { out[i] = null; continue; }
        const denom = cnt * sxx - sx * sx;
        out[i] = denom === 0 ? null : 0.5 * (cnt * sxy - sx * sy) / denom; // ½·(deg/km)
    }
    return out;
}

// Lowest-tilt SPECIFIC DIFFERENTIAL PHASE (KDP, °/km) — dual-pol, DERIVED from the ΦDP moment (not a
// direct read; see kdpFromPhi). ΦDP is collected in the SURVEILLANCE cut alongside reflectivity/CC
// (lowest elevation NUMBER), so we read it there. Per radial we turn ΦDP into a KDP value array with the
// SAME gate geometry, so it flows through buildGates/buildGrid like any moment. Null on a legacy
// single-pol volume (no ΦDP). The per-gate ρHV/reflectivity QC already restricts KDP to precip, so no
// separate reflectivity mask is needed (unlike CC, whose raw grid is unmasked).
function buildKdp(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const phiR = momentRadials(radar, 'phi');
    const reflR = momentRadials(radar, 'reflect');
    const rhoR = momentRadials(radar, 'rho');
    // Legacy single-pol volumes carry no differential phase — bail before building anything.
    if (!phiR.some(function (p) { return p && p.moment_data; })) return { geom: null, grid: null };

    const kdpR = phiR.map(function (p, i) {
        if (!p || !p.moment_data) return p;
        return {
            moment_data: kdpFromPhi(p, reflR && reflR[i], rhoR && rhoR[i], minDbz),
            first_gate: p.first_gate, gate_size: p.gate_size,
        };
    });

    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(kdpR, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(KDP_RAMP, v);
    });
    // Inspector reads KDP directly; scale 100 → 0.01 °/km quantization.
    return { geom: geom, grid: buildGrid(kdpR, getAz, 100, KDP_RAMP.unit, 2, wantGrid) };
}

// Masks a moment's radials to only where the co-located reflectivity gate is >= minDbz (aligned by
// RANGE). DOW velocity exists at EVERY gate — including clear-air / biological (insect) returns that
// carry a real-but-meaningless velocity — so without this the whole domain renders as velocity speckle.
// Keeping velocity only where there's actual precip makes it match the (dBZ-thresholded) reflectivity.
// Same idea as the CC reflectivity mask. Returns new radials; input unchanged.
function maskByReflectivity(radials, reflRadials, minDbz) {
    const out = new Array(radials.length);
    for (let i = 0; i < radials.length; i++) {
        const d = radials[i];
        if (!d || !d.moment_data) { out[i] = d; continue; }
        const r = reflRadials && reflRadials[i];
        const rd = r && r.moment_data;
        const md = new Array(d.moment_data.length);
        for (let j = 0; j < d.moment_data.length; j++) {
            let keep = false;
            if (rd) {
                const range = d.first_gate + j * d.gate_size; // km
                const rj = Math.round((range - r.first_gate) / r.gate_size);
                const rv = (rj >= 0 && rj < rd.length) ? rd[rj] : null;
                keep = (rv !== null && rv !== undefined && rv >= minDbz);
            }
            md[j] = keep ? d.moment_data[j] : null;
        }
        out[i] = { moment_data: md, first_gate: d.first_gate, gate_size: d.gate_size };
    }
    return out;
}

// Decodes a normalized DOW frame (the "dow-frame/1" JSON from tools/dow_import.py) into the SAME
// { moments, grids, built, rangeMeters, ... } result decodeAndBuild returns — so the host
// renders a mobile-radar sweep through the identical RadarLayer pipeline (WebGL fill + range ring +
// Inspect + legend). A DOW frame is ONE sweep at the truck's lat/lon: true azimuths per radial +
// Int16-quantized moment arrays. Velocity is ALREADY dealiased by the converter (Py-ART), so we do
// NOT run dealiasSweep here. Synchronous — no vendored decoder is needed (this is our own format).
// `minDbz` thresholds reflectivity (and masks CC) exactly like the NEXRAD path.
export function decodeDowFrame(json, minDbz) {
    const t0 = (typeof performance !== 'undefined') ? performance.now() : 0;
    const az = json.azimuth || [];
    const nRad = json.nRadials || az.length;
    const nodata = (typeof json.nodata === 'number') ? json.nodata : -32768;
    const lat = json.lat, lon = json.lon;
    const getAz = function (i) { return az[i]; };

    // Build {moment_data, first_gate(km), gate_size(km)} radials for a named moment (dequantizing the
    // Int16 values), or null if the frame doesn't carry it. Same shape buildGates/buildGrid consume.
    function radialsFor(name) {
        const m = json.moments && json.moments[name];
        if (!m || !m.values) return null;
        const ng = m.nGates, scale = m.scale || 1, vals = m.values;
        const out = new Array(nRad);
        for (let i = 0; i < nRad; i++) {
            const md = new Array(ng);
            const base = i * ng;
            for (let j = 0; j < ng; j++) {
                const q = vals[base + j];
                md[j] = (q === nodata) ? null : q / scale;
            }
            out[i] = { moment_data: md, first_gate: m.firstGateKm, gate_size: m.gateSizeKm };
        }
        return out;
    }

    const reflR = radialsFor('reflectivity');
    const velR = radialsFor('velocity');
    const ccR = radialsFor('rho');

    let geom = null, reflGrid = null;
    if (reflR) {
        geom = buildGates(reflR, getAz, lat, lon, function (dbz) {
            if (dbz === null || dbz === undefined || dbz < minDbz) return null;
            return rampColor(REFLECTIVITY_RAMP, dbz);
        });
        reflGrid = buildGrid(reflR, getAz, 10, REFLECTIVITY_RAMP.unit, 1);
    }

    let velGeom = null, velGrid = null;
    if (velR) {
        // Mask velocity to where reflectivity is meaningful — DOW velocity fills the whole domain
        // (incl. clear-air/bio scatter) otherwise. With no reflectivity present, leave it unmasked.
        const velMasked = reflR ? maskByReflectivity(velR, reflR, minDbz) : velR;
        velGeom = buildGates(velMasked, getAz, lat, lon, function (v) {
            if (v === null || v === undefined) return null;
            return rampColor(VELOCITY_RAMP, v);
        });
        velGrid = buildGrid(velMasked, getAz, 10, VELOCITY_RAMP.unit, 1);
    }

    // CC (dual-pol DOW only), masked by reflectivity — aligned by RANGE, same as the NEXRAD path.
    let ccGeom = null, ccGrid = null;
    if (ccR) {
        const masked = maskByReflectivity(ccR, reflR, minDbz);
        ccGeom = buildGates(masked, getAz, lat, lon, function (v) {
            if (v === null || v === undefined) return null;
            return rampColor(CORRELATION_RAMP, v);
        });
        ccGrid = buildGrid(ccR, getAz, 1000, CORRELATION_RAMP.unit, 2);
    }

    const rangeGrid = reflGrid || velGrid || ccGrid;
    const rangeMeters = rangeGrid && isFinite(rangeGrid.firstGate) && isFinite(rangeGrid.gateSize)
        ? (rangeGrid.firstGate + rangeGrid.nGates * rangeGrid.gateSize) * 1000 : 0;
    const t1 = (typeof performance !== 'undefined') ? performance.now() : 0;
    return {
        // Same keyed shape decodeAndBuild returns (see there). A DOW frame carries refl/vel/cc; velocity
        // is pre-dealiased by the converter so it's always "built" (built.velocity=true, no lazy re-decode).
        moments: { reflectivity: geom, velocity: velGeom, cc: ccGeom },
        grids: { reflectivity: reflGrid, velocity: velGrid, cc: ccGrid },
        built: { reflectivity: true, velocity: true, cc: true },
        gridsBuilt: true,
        rangeMeters: rangeMeters,
        decodeMs: Math.round(t1 - t0), buildMs: 0,
        radials: nRad, gates: reflR && reflR[0] ? reflR[0].moment_data.length : 0, bytes: 0,
        elevList: String(json.elevationDeg), velElev: -1, reflStats: null, velStats: null,
        velNyq: json.nyquistMps || 0, dealias: '',
    };
}

// Decodes a volume ArrayBuffer and returns { moments, grids, built, decodeMs, buildMs, ... }. moments is
// a per-product-id map (radar-products.js) of gate geometry with baked-in vertex colors, each null when
// that product has nothing to draw (e.g. reflectivity below threshold, or no Doppler cut for velocity),
// so the host can toggle product instantly without re-decoding. built[id] reports which builds ran.
//
// The per-product geometry builders, keyed by product id (radar-products.js). Adding a product = add a
// build fn here + a PRODUCTS entry + a ramp; decodeAndBuild then loops over the registry automatically.
// Every builder shares the (radar, siteLat, siteLon, minDbz, wantGrid) signature (buildVelocity ignores
// minDbz — it isn't reflectivity-masked) so the loop can call them uniformly.
const BUILDERS = {
    reflectivity: buildReflectivity,
    velocity: buildVelocity,
    cc: buildCorrelation,
    kdp: buildKdp,
    zdr: buildZdr,
    sw: buildSpectrumWidth,
};

// buildLazy (default true) gates the LAZY products (radar-products.js `lazy:true` — today only velocity,
// the ONLY product that must dealias via dealiasSweep, by far the priciest step per frame): when the user
// isn't on a lazy product the host passes buildLazy=false and those builds are skipped. The result's
// built[id] tells the host a refl-only frame must be re-decoded before it can show velocity (see radar.js
// setProduct). Non-lazy products (reflectivity, CC) are cheap and always built.
export function decodeAndBuild(ab, siteLat, siteLon, minDbz, buildLazy, buildGrids) {
    if (buildLazy === undefined) buildLazy = true; // decode everything (incl. lazy velocity) unless told otherwise
    if (buildGrids === undefined) buildGrids = true;
    const bytes = ab.byteLength;
    return loadDecoder().then(function (dec) {
        const t0 = performance.now();
        const radar = new dec.Level2Radar(dec.Buffer.from(new Uint8Array(ab)));
        const t1 = performance.now();
        // buildGrids (default true) gates the per-gate inspector VALUE arrays — the host passes false
        // when Inspect is off (the common case) so long loops don't retain ~Int16 N×G per product per
        // frame. The range ring uses only the grid's scalar metadata, which is always computed, so it's
        // unaffected. Re-decoded on demand when Inspect is toggled on (see radar.js setInspect).
        // Build each product's geometry through the registry (radar-products.js). Non-lazy products
        // (reflectivity, CC) always build; lazy products (velocity — the only one that dealiases) build
        // only when buildLazy is set (the active product is lazy, or velocity prefetch is on). Every
        // BUILDERS entry shares the (radar, lat, lon, minDbz, wantGrid) signature so this stays a
        // data-driven loop; results[] keeps the full {geom,grid} for the range-ring extent below.
        const results = {}, moments = {}, grids = {}, built = {};
        for (let pi = 0; pi < PRODUCT_IDS.length; pi++) {
            const id = PRODUCT_IDS[pi];
            if (PRODUCTS[id].lazy && !buildLazy) { moments[id] = null; grids[id] = null; built[id] = false; continue; }
            const r = BUILDERS[id](radar, siteLat, siteLon, minDbz, buildGrids);
            results[id] = r;
            moments[id] = r.geom || null;
            grids[id] = buildGrids ? (r.grid || null) : null;
            built[id] = true;
        }
        const t2 = performance.now();
        // Diagnostics: per-moment radial/azimuth-span/gate stats + the elevation NUMBERS present and
        // which one velocity came from. This is what surfaces a partial sweep (small az span) or a
        // missing/odd Doppler cut, so intermittent velocity glitches are visible in the log.
        let radials = 0, gates = 0, elevList = '', velElevNum = -1, velNyq = 0;
        let velNyqSrc = '', velNyqRad = 0, velNyqVol = 0; // instrumentation: which Nyquist field was used
        let reflStats = null, velStats = null;
        try {
            const elevs = radar.listElevations();
            elevList = (elevs || []).join(',');
            if (elevs && elevs.length) {
                radar.setElevation(Math.min.apply(null, elevs));
                const reflArr = momentRadials(radar, 'reflect');
                reflStats = sweepStats(reflArr, function (i) { return radar.getAzimuth(i); });
                radials = reflStats.rad; gates = reflStats.gates;

                const ve = findVelocityElevation(radar);
                if (ve !== null) {
                    velElevNum = ve;
                    radar.setElevation(ve);
                    const velArr = momentRadials(radar, 'velocity');
                    velStats = sweepStats(velArr, function (i) { return radar.getAzimuth(i); });
                    const det = sweepNyquistDetail(radar, velArr.length);
                    velNyq = isFinite(det.med) ? Math.round(det.med * 10) / 10 : 0;
                    velNyqSrc = det.src; // 'rad' (correct) | 'vol' (fallback, suspect) | 'mixed' | 'none'
                    velNyqRad = isFinite(det.radMed) ? Math.round(det.radMed * 10) / 10 : 0;
                    velNyqVol = isFinite(det.volMed) ? Math.round(det.volMed * 10) / 10 : 0;
                }
            }
        } catch (e) { /* stats only */ }
        // Outer data extent (metres) of the lowest tilt = first gate + all gates, from whichever
        // moment grid exists (reflectivity is the widest, ~460 km super-res). This is the radar's
        // REAL maximum range — the radius for the on-map range ring (varies by radar/VCP/product).
        // Range ring radius = outer data extent of the first built product's grid (reflectivity is first
        // in the registry and the widest sweep). The grid's scalar metadata (firstGate/gateSize/nGates)
        // is present even when buildGrids is false, so the ring works whether or not the inspector value
        // grids were shipped.
        let rangeGrid = null;
        for (let pi = 0; pi < PRODUCT_IDS.length && !rangeGrid; pi++) {
            const rr = results[PRODUCT_IDS[pi]];
            if (rr && rr.grid) rangeGrid = rr.grid;
        }
        const rangeMeters = rangeGrid && isFinite(rangeGrid.firstGate) && isFinite(rangeGrid.gateSize)
            ? (rangeGrid.firstGate + rangeGrid.nGates * rangeGrid.gateSize) * 1000 : 0;
        return {
            // Keyed by product id (radar-products.js): the host stores frames[i].moments/built/grids and
            // renders/upgrades via a map lookup instead of the old flat velPositions/ccPositions fields.
            // grids are only shipped when buildGrids (inspector on); rangeMeters above already captured
            // the extent, so a null grid here doesn't affect the range ring.
            moments: moments, grids: grids, built: built, gridsBuilt: !!buildGrids,
            rangeMeters: rangeMeters,
            decodeMs: Math.round(t1 - t0), buildMs: Math.round(t2 - t1),
            radials: radials, gates: gates, bytes: bytes,
            elevList: elevList, velElev: velElevNum, reflStats: reflStats, velStats: velStats, velNyq: velNyq,
            velNyqSrc: velNyqSrc, velNyqRad: velNyqRad, velNyqVol: velNyqVol,
            dealias: moments.velocity ? _dealiasInfo : '',
        };
    });
}

// Grids-only fast path for the inspector. Parses the volume and builds ONLY the requested product's value
// grid (reusing that product's builder, discarding its geometry), so turning Inspect on doesn't pay a full
// decodeAndBuild — all six products' geometry PLUS a redundant velocity dealias — just to read one product's
// values. Non-velocity grids are cheap (no dealias); velocity's grid still runs its dealias (the inspector
// shows the dealiased value), but only for velocity, not the whole registry. The host MERGES the returned
// grid into the existing frame, leaving its geometry untouched. Returns { grids: { [productId]: grid|null } }.
export function decodeGridOnly(ab, siteLat, siteLon, minDbz, productId) {
    return loadDecoder().then(function (dec) {
        const radar = new dec.Level2Radar(dec.Buffer.from(new Uint8Array(ab)));
        const builder = BUILDERS[productId];
        const grids = {};
        grids[productId] = builder ? (builder(radar, siteLat, siteLon, minDbz, true).grid || null) : null;
        return { grids: grids, gridProduct: productId };
    });
}
