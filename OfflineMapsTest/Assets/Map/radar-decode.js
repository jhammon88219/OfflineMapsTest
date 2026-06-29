// Shared NEXRAD Level II decode + gate-geometry build. No DOM / GL / host dependencies,
// so it runs inside a Web Worker (radar-worker.js) as well as on the main thread (radar.js
// fallback). The heavy cost here is the pure-JS bzip2 decompression inside Level2Radar
// (~5 s for a full volume), which is exactly why we run it off the UI thread.

import { REFLECTIVITY_RAMP, VELOCITY_RAMP, CORRELATION_RAMP, rampColor } from './radar-ramps.js';

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
        decoderPromise = import('./vendor/nexrad-level-2-data.esm.js').then(function (mod) {
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
    const mPerDegLat = 111320;
    const mPerDegLon = 111320 * Math.cos(siteLat * D2R);
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
function buildGrid(radials, getAzimuth, scale, unit, digits) {
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
function buildReflectivity(radar, siteLat, siteLon, minDbz) {
    const elevations = radar.listElevations();
    if (!elevations || !elevations.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevations));
    const radials = momentRadials(radar, 'reflect');
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(radials, getAz, siteLat, siteLon, function (dbz) {
        if (dbz === null || dbz === undefined || dbz < minDbz) return null;
        return rampColor(REFLECTIVITY_RAMP, dbz);
    });
    return { geom: geom, grid: buildGrid(radials, getAz, 10, REFLECTIVITY_RAMP.unit, 1) };
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
function nyquistForRadial(radar, i) {
    try {
        const rec = radar.data[radar.elevation][i] && radar.data[radar.elevation][i].record;
        if (!rec) return NaN;
        // Message 31 (super-res) carries Nyquist in the RAD sub-block (cm/s); legacy Message 1 puts
        // it on the record itself, already in m/s (the decoder divides by 100 there). The >100 guard
        // below normalizes whichever path supplied cm/s, and leaves an already-m/s value alone.
        let nv = (rec.radial && typeof rec.radial.nyquist_velocity === 'number')
            ? rec.radial.nyquist_velocity
            : rec.nyquist_velocity;
        if (typeof nv !== 'number' || !(nv > 1)) return NaN;
        if (nv > 100) nv /= 100; // cm/s -> m/s
        return nv;
    } catch (e) { return NaN; }
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

// Region-based velocity DEALIASING (v1). Naive gate-by-gate unfolding lets ONE wrong step smear a
// whole radial (butchered spokes/patches), so instead:
//   1. Flood-fill the sweep (radial j±1 and azimuth r±1, wrapping) into connected regions where
//      neighbouring gates differ by < Nyquist — i.e. no fold occurs INSIDE a region.
//   2. Build the region-adjacency graph, recording the mean velocity jump across each boundary
//      (a real fold reads ~±2·Nyquist; a continuous boundary ~0).
//   3. BFS from a large, near-zero (so ~unaliased) region; unfold each neighbour by the whole
//      number of 2·Nyquist that best matches it across the shared boundary.
// Whole continuous regions move together, so a single noisy gate can't corrupt a radial. Still
// single-sweep (no sounding/VAD/previous volume); a genuinely sharp shear between two regions can
// be mis-stepped, but the broad field stays correct. Input unchanged if no Nyquist is available.
function dealiasSweep(radials, radar) {
    const med = sweepNyquist(radar, radials.length);
    if (!isFinite(med)) return radials;

    const N = radials.length;
    let G = 0;
    for (let r = 0; r < N; r++) {
        const md = radials[r] && radials[r].moment_data;
        if (md && md.length > G) G = md.length;
    }
    if (!N || !G) return radials;

    const nyq2 = 2 * med;
    const sameT = med; // adjacent gates within one Nyquist => same (fold-free) region
    const idx = function (r, j) { return r * G + j; };
    function val(r, j) {
        const md = radials[r] && radials[r].moment_data;
        if (!md || j >= md.length) return NaN;
        const v = md[j];
        return (v === null || v === undefined || !isFinite(v)) ? NaN : v;
    }

    // --- 1. flood-fill regions (label: -1 unseen, -2 no-data, >=0 region id) ---
    const label = new Int32Array(N * G).fill(-1);
    const regionSum = [], regionCnt = [];
    const stack = [];
    for (let r0 = 0; r0 < N; r0++) {
        for (let j0 = 0; j0 < G; j0++) {
            if (label[idx(r0, j0)] !== -1) continue;
            if (!isFinite(val(r0, j0))) { label[idx(r0, j0)] = -2; continue; }
            const rid = regionCnt.length;
            regionSum.push(0); regionCnt.push(0);
            stack.length = 0; stack.push(r0, j0); label[idx(r0, j0)] = rid;
            while (stack.length) {
                const j = stack.pop(), r = stack.pop();
                const v = val(r, j);
                regionSum[rid] += v; regionCnt[rid]++;
                const nb = [[r, j - 1], [r, j + 1], [(r - 1 + N) % N, j], [(r + 1) % N, j]];
                for (let k = 0; k < 4; k++) {
                    const rr = nb[k][0], jj = nb[k][1];
                    if (jj < 0 || jj >= G) continue;
                    const id2 = idx(rr, jj);
                    if (label[id2] !== -1) continue;
                    const v2 = val(rr, jj);
                    if (!isFinite(v2)) { label[id2] = -2; continue; }
                    if (Math.abs(v2 - v) < sameT) { label[id2] = rid; stack.push(rr, jj); }
                }
            }
        }
    }
    const numReg = regionCnt.length;
    if (!numReg) return radials;

    // --- 2. region adjacency: mean (vA - vB) across each boundary ---
    const adj = new Array(numReg);
    for (let i = 0; i < numReg; i++) adj[i] = new Map();
    function addAdj(a, b, diff) {
        let m = adj[a].get(b);
        if (!m) { m = { sum: 0, cnt: 0 }; adj[a].set(b, m); }
        m.sum += diff; m.cnt++;
    }
    for (let r = 0; r < N; r++) {
        for (let j = 0; j < G; j++) {
            const la = label[idx(r, j)];
            if (la < 0) continue;
            const va = val(r, j);
            const nb = [[r, j + 1], [(r + 1) % N, j]]; // right + down(+wrap): each boundary once
            for (let k = 0; k < 2; k++) {
                const rr = nb[k][0], jj = nb[k][1];
                if (jj >= G) continue;
                const lb = label[idx(rr, jj)];
                if (lb < 0 || lb === la) continue;
                const vb = val(rr, jj);
                addAdj(la, lb, va - vb); addAdj(lb, la, vb - va);
            }
        }
    }

    // --- 3. largest region (only for the log's anchor reference; real anchoring is the union below
    // plus the global mean shift) ---
    let seed = 0, seedCnt = -1;
    for (let i = 0; i < numReg; i++) {
        if (regionCnt[i] > seedCnt) { seedCnt = regionCnt[i]; seed = i; }
    }

    // --- 4. resolve per-region fold counts by unfolding the STRONGEST boundaries first. A plain BFS
    // trusts every edge equally, so one weak/noisy boundary mis-folds a whole subtree (values blew up
    // to ±70-90 m/s). Instead union regions strongest-boundary-first (a maximum-spanning-tree over the
    // region graph) with a relative fold offset, so each region anchors through its most reliable
    // boundary. Union-find carries koff[x] = K(x) - K(parent); each component's root has K = 0. ---
    const edges = [];
    for (let a = 0; a < numReg; a++) {
        adj[a].forEach(function (m, b) { if (b > a) edges.push([a, b, m.sum / m.cnt, m.cnt]); });
    }
    edges.sort(function (p, qq) { return qq[3] - p[3]; }); // most boundary gates first

    const parent = new Int32Array(numReg);
    const koff = new Int32Array(numReg);   // K(node) - K(parent)
    const csize = new Int32Array(numReg);
    for (let i = 0; i < numReg; i++) { parent[i] = i; csize[i] = regionCnt[i]; }
    function rootOf(x) { while (parent[x] !== x) x = parent[x]; return x; }
    function kToRoot(x) { let k = 0; while (parent[x] !== x) { k += koff[x]; x = parent[x]; } return k; }
    // Only a jump NEAR a multiple of 2·Nyquist is a real fold. Region boundaries also fall on real
    // velocity gradients (shear lines, the inbound/outbound transition) whose jump (~30-45 m/s) is
    // NOT a fold; rounding those to ±1 and accumulating them down a gradient is what blew the field
    // up to ±140 m/s. So: unfold only when the residual to the nearest fold multiple is small, and
    // never more than one fold per boundary (velocity > 3·Nyquist doesn't happen at these ranges).
    const foldTol = 0.28 * nyq2;
    for (let e = 0; e < edges.length; e++) {
        const a = edges[e][0], b = edges[e][1], meanDiff = edges[e][2];
        const ra = rootOf(a), rb = rootOf(b);
        if (ra === rb) continue; // already joined via a stronger boundary
        let D = Math.round(meanDiff / nyq2); // want K(b) - K(a) = D
        if (D !== 0 && Math.abs(meanDiff - D * nyq2) > foldTol) D = 0; // a gradient, not a fold
        if (Math.min(regionCnt[a], regionCnt[b]) < 25) D = 0; // tiny (noise) region: don't fold it
        if (D > 1) D = 1; else if (D < -1) D = -1;
        const ka = kToRoot(a), kb = kToRoot(b);
        if (csize[ra] >= csize[rb]) {
            koff[rb] = D + ka - kb; parent[rb] = ra; csize[ra] += csize[rb];
        } else {
            koff[ra] = -D - ka + kb; parent[ra] = rb; csize[rb] += csize[ra];
        }
    }
    const kFold = new Int32Array(numReg);
    for (let i = 0; i < numReg; i++) kFold[i] = kToRoot(i);

    // --- 5. VAD ABSOLUTE ANCHOR. The MST fixes folds RELATIVE to neighbours, but a far-range region's
    // ABSOLUTE fold (does it go +1 or -1?) is ambiguous from spatial continuity alone — that's the
    // far-range sign flip (outbound rendered as strong-inbound purple). A roughly-uniform environmental
    // wind makes radial velocity a sinusoid in azimuth at each range ring: v(az) ≈ c0 + c1·cos(az) +
    // c2·sin(az). We fit that ring-by-ring, propagating OUTWARD from the (unaliased) near range so each
    // ring is seeded by the previous one's fit — giving an unaliased reference velocity at every range.
    // Each region is then snapped to the whole 2·Nyquist nearest its fit. A whole region shifts together
    // (local structure preserved); storm-scale couplets average out region-wide; and we only snap when
    // the region's mean is CLEARLY a fold off the fit, so a genuinely strong feature isn't flattened.
    const cosA = new Float64Array(N), sinA = new Float64Array(N);
    for (let i = 0; i < N; i++) {
        const a = radar.getAzimuth(i);
        const ar = (typeof a === 'number' ? a : 0) * Math.PI / 180;
        cosA[i] = Math.cos(ar); sinA[i] = Math.sin(ar);
    }
    // We fit only c1·cos + c2·sin — NO DC term. The DC (mean radial velocity around a full azimuth
    // circle) is ~0 for a uniform horizontal wind, and leaving it free let it drift up a fold, seed
    // the next ring higher, and CASCADE outward to hundreds of m/s (v[-860] in the log → far-range
    // whites). Dropping it anchors the fit; the per-iteration unfold is also clamped to ±2 folds and
    // an implausible wind amplitude is rejected, so a ring can never blow up.
    const ringC1 = new Float64Array(G), ringC2 = new Float64Array(G);
    const ringOk = new Uint8Array(G);
    let c1 = 0, c2 = 0, fitRings = 0; // current fit seeds the next ring (start at 0 near the radar)
    for (let j = 0; j < G; j++) {
        let okThis = false;
        for (let iter = 0; iter < 6; iter++) {
            let scc = 0, scs = 0, sss = 0, qbc = 0, qbs = 0, n = 0;
            for (let i = 0; i < N; i++) {
                const md = radials[i].moment_data;
                if (!md || j >= md.length) continue;
                const v = md[j];
                if (v === null || v === undefined || !isFinite(v)) continue;
                const cs = cosA[i], sn = sinA[i];
                const fit = c1 * cs + c2 * sn; // zero-mean wind sinusoid
                let k = Math.round((fit - v) / nyq2);
                if (k > 2) k = 2; else if (k < -2) k = -2; // a gate is never >2 folds off the wind
                const vu = v + k * nyq2;                   // unfold toward the current fit
                scc += cs * cs; scs += cs * sn; sss += sn * sn;
                qbc += vu * cs; qbs += vu * sn; n++;
            }
            if (n < 8) break; // too few points to fit -> keep the seed (previous ring)
            const det = scc * sss - scs * scs;
            if (!isFinite(det) || Math.abs(det) < 1e-6) break;
            const n1 = (qbc * sss - qbs * scs) / det;
            const n2 = (scc * qbs - scs * qbc) / det;
            if (!isFinite(n1) || !isFinite(n2) || Math.hypot(n1, n2) > 75) break; // implausible -> keep seed
            c1 = n1; c2 = n2; okThis = true;
        }
        ringC1[j] = c1; ringC2[j] = c2; ringOk[j] = okThis ? 1 : 0;
        if (okThis) fitRings++;
    }

    if (fitRings >= 4) {
        // NOTE: a VAD gap-fill (interpolating the wind fit across unfitted rings to anchor far-range
        // regions) was tried here and REVERTED — on the sparse/fragmented sweeps it was meant to help
        // (KTLX-2024, 42% rings fit) it made things ~3x WORSE (over-unfold 4.75%→13.3%): the few fitted
        // rings are themselves noisy, so spreading them gave regions a confidently-wrong anchor. The
        // failure there is information-poverty, not anchor placement — no self-VAD variant fixes it;
        // it needs an external/temporal first guess (see CLAUDE.md). Keep the honest ringOk-only anchor.
        const corrSum = new Float64Array(numReg), corrN = new Int32Array(numReg);
        for (let i = 0; i < N; i++) {
            const md = radials[i].moment_data; if (!md) continue;
            const cs = cosA[i], sn = sinA[i];
            for (let j = 0; j < md.length; j++) {
                if (!ringOk[j]) continue;
                const lid = label[idx(i, j)]; if (lid < 0) continue;
                const v = md[j]; if (v === null || v === undefined || !isFinite(v)) continue;
                const fit = ringC1[j] * cs + ringC2[j] * sn;
                corrSum[lid] += fit - (v + kFold[lid] * nyq2); corrN[lid]++;
            }
        }
        for (let r = 0; r < numReg; r++) {
            if (!corrN[r]) continue;
            const m = corrSum[r] / corrN[r];
            let k = Math.round(m / nyq2);
            if (k !== 0 && Math.abs(m - k * nyq2) < 0.3 * nyq2) {
                if (k > 2) k = 2; else if (k < -2) k = -2; // never snap a region more than 2 folds
                kFold[r] += k;
            }
        }
    } else {
        // Sparse sweep: VAD couldn't fit. Fall back to the global-mean anchor (mean radial ~ 0).
        let vsum = 0, vcnt = 0;
        for (let r = 0; r < N; r++) for (let j = 0; j < G; j++) {
            const lid = label[idx(r, j)]; if (lid < 0) continue;
            vsum += val(r, j) + kFold[lid] * nyq2; vcnt++;
        }
        const gshift = vcnt ? Math.round((vsum / vcnt) / nyq2) : 0;
        for (let r = 0; r < numReg; r++) kFold[r] -= gshift;
    }

    // --- 6. apply per-region fold ---
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
            const dv = v + kFold[lid] * nyq2;
            mdOut[j] = dv;
            tot++;
            if (dv < vmin) vmin = dv;
            if (dv > vmax) vmax = dv;
            if (dv > 55 || dv < -55) hi++; // implausibly fast at 0.5° => over-unfolded / noise
        }
        out[r] = { moment_data: mdOut, first_gate: src.first_gate, gate_size: src.gate_size };
    }
    _dealiasInfo = numReg + 'reg seedMean' + Math.round(regionSum[seed] / regionCnt[seed]) +
        ' vad' + fitRings + '/' + G + ' v[' + (isFinite(vmin) ? Math.round(vmin) : '?') + ',' +
        (isFinite(vmax) ? Math.round(vmax) : '?') + '] hi=' + hi + '/' + tot;
    return out;
}

// Lowest-tilt base-velocity geometry (dealiased) + the inspector value grid (also from the DEALIASED
// radials, so the inspected value matches the rendered pixel). Returns { geom, grid }; both null if
// the volume carries no velocity.
function buildVelocity(radar, siteLat, siteLon) {
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
    return { geom: geom, grid: buildGrid(dealiased, getAz, 10, VELOCITY_RAMP.unit, 1) };
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
function buildCorrelation(radar, siteLat, siteLon, minDbz) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const ccR = momentRadials(radar, 'rho');
    const reflR = momentRadials(radar, 'reflect');
    // Legacy Message-1 (single-pol) volumes have no ρHV at all, so bail before building anything.
    if (!ccR.some(function (c) { return c && c.moment_data; })) return { geom: null, grid: null };

    const masked = new Array(ccR.length);
    for (let i = 0; i < ccR.length; i++) {
        const c = ccR[i];
        if (!c || !c.moment_data) { masked[i] = c; continue; }
        const r = reflR[i];
        const rd = r && r.moment_data;
        const out = new Array(c.moment_data.length);
        for (let j = 0; j < c.moment_data.length; j++) {
            let keep = false;
            if (rd) {
                const range = c.first_gate + j * c.gate_size;       // km
                const rj = Math.round((range - r.first_gate) / r.gate_size);
                const rv = (rj >= 0 && rj < rd.length) ? rd[rj] : null;
                keep = (rv !== null && rv !== undefined && rv >= minDbz);
            }
            out[j] = keep ? c.moment_data[j] : null;
        }
        masked[i] = { moment_data: out, first_gate: c.first_gate, gate_size: c.gate_size };
    }

    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(masked, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(CORRELATION_RAMP, v);
    });
    // The inspector grid uses the UNMASKED ρHV (ccR), so the cursor reads the true value anywhere
    // there's signal — not only where the reflectivity-masked geometry draws.
    return { geom: geom, grid: buildGrid(ccR, getAz, 1000, CORRELATION_RAMP.unit, 2) };
}

// Decodes a normalized DOW frame (the "dow-frame/1" JSON from tools/dow_import.py) into the SAME
// { geom, velGeom, ccGeom, grids, rangeMeters, ... } result decodeAndBuild returns — so the host
// renders a mobile-radar sweep through the identical RadarLayer pipeline (WebGL fill + range ring +
// Inspect + legend). A DOW frame is ONE sweep at the truck's lat/lon: true azimuths per radial +
// Int16-quantized moment arrays. Velocity is ALREADY dealiased by the converter (Py-ART), so we do
// NOT run dealiasSweep here. Synchronous — no vendored decoder is needed (this is our own format).
// `minDbz` thresholds reflectivity (and masks CC) exactly like the NEXRAD path.
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
        const masked = new Array(ccR.length);
        for (let i = 0; i < ccR.length; i++) {
            const c = ccR[i];
            if (!c || !c.moment_data) { masked[i] = c; continue; }
            const r = reflR && reflR[i];
            const rd = r && r.moment_data;
            const out = new Array(c.moment_data.length);
            for (let j = 0; j < c.moment_data.length; j++) {
                let keep = false;
                if (rd) {
                    const range = c.first_gate + j * c.gate_size;
                    const rj = Math.round((range - r.first_gate) / r.gate_size);
                    const rv = (rj >= 0 && rj < rd.length) ? rd[rj] : null;
                    keep = (rv !== null && rv !== undefined && rv >= minDbz);
                }
                out[j] = keep ? c.moment_data[j] : null;
            }
            masked[i] = { moment_data: out, first_gate: c.first_gate, gate_size: c.gate_size };
        }
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
        geom: geom, velGeom: velGeom, ccGeom: ccGeom,
        reflGrid: reflGrid, velGrid: velGrid, ccGrid: ccGrid,
        rangeMeters: rangeMeters,
        decodeMs: Math.round(t1 - t0), buildMs: 0,
        radials: nRad, gates: reflR && reflR[0] ? reflR[0].moment_data.length : 0, bytes: 0,
        elevList: String(json.elevationDeg), velElev: -1, reflStats: null, velStats: null,
        velNyq: json.nyquistMps || 0, dealias: '',
    };
}

// Decodes a volume ArrayBuffer and returns { geom, velGeom, decodeMs, buildMs, ... }. geom is
// the reflectivity geometry (null when nothing's above threshold); velGeom is the velocity
// geometry (null when the volume has no Doppler cut). Both have baked-in vertex colors, so the
// host can toggle product instantly without re-decoding.
export function decodeAndBuild(ab, siteLat, siteLon, minDbz) {
    const bytes = ab.byteLength;
    return loadDecoder().then(function (dec) {
        const t0 = performance.now();
        const radar = new dec.Level2Radar(dec.Buffer.from(new Uint8Array(ab)));
        const t1 = performance.now();
        const reflR = buildReflectivity(radar, siteLat, siteLon, minDbz);
        const velR = buildVelocity(radar, siteLat, siteLon);
        const ccR = buildCorrelation(radar, siteLat, siteLon, minDbz);
        const geom = reflR.geom, velGeom = velR.geom, ccGeom = ccR.geom;
        const t2 = performance.now();
        // Diagnostics: per-moment radial/azimuth-span/gate stats + the elevation NUMBERS present and
        // which one velocity came from. This is what surfaces a partial sweep (small az span) or a
        // missing/odd Doppler cut, so intermittent velocity glitches are visible in the log.
        let radials = 0, gates = 0, elevList = '', velElevNum = -1, velNyq = 0;
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
                    const nyq = sweepNyquist(radar, velArr.length);
                    velNyq = isFinite(nyq) ? Math.round(nyq * 10) / 10 : 0;
                }
            }
        } catch (e) { /* stats only */ }
        // Outer data extent (metres) of the lowest tilt = first gate + all gates, from whichever
        // moment grid exists (reflectivity is the widest, ~460 km super-res). This is the radar's
        // REAL maximum range — the radius for the on-map range ring (varies by radar/VCP/product).
        const rangeGrid = reflR.grid || ccR.grid || velR.grid;
        const rangeMeters = rangeGrid && isFinite(rangeGrid.firstGate) && isFinite(rangeGrid.gateSize)
            ? (rangeGrid.firstGate + rangeGrid.nGates * rangeGrid.gateSize) * 1000 : 0;
        return {
            geom: geom, velGeom: velGeom, ccGeom: ccGeom,
            reflGrid: reflR.grid, velGrid: velR.grid, ccGrid: ccR.grid, // inspector value grids
            rangeMeters: rangeMeters,
            decodeMs: Math.round(t1 - t0), buildMs: Math.round(t2 - t1),
            radials: radials, gates: gates, bytes: bytes,
            elevList: elevList, velElev: velElevNum, reflStats: reflStats, velStats: velStats, velNyq: velNyq,
            dealias: velGeom ? _dealiasInfo : '',
        };
    });
}
