// Shared NEXRAD Level II decode + gate-geometry build. No DOM / GL / host dependencies,
// so it runs inside a Web Worker (radar-worker.js) as well as on the main thread (radar.js
// fallback). The heavy cost here is the pure-JS bzip2 decompression inside Level2Radar
// (~5 s for a full volume), which is exactly why we run it off the UI thread.

const HALF_BEAM_DEG = 0.5; // half the super-res azimuthal spacing (~1° beam)
const D2R = Math.PI / 180;

// Classic NWS-style reflectivity ramp: [minDbz, r, g, b]. Highest stop <= dBZ wins.
const RAMP = [
    [5, 0x00, 0xec, 0xec], [10, 0x01, 0xa0, 0xf6], [15, 0x00, 0x00, 0xf6],
    [20, 0x00, 0xff, 0x00], [25, 0x00, 0xc8, 0x00], [30, 0x00, 0x90, 0x00],
    [35, 0xff, 0xff, 0x00], [40, 0xe7, 0xc0, 0x00], [45, 0xff, 0x90, 0x00],
    [50, 0xff, 0x00, 0x00], [55, 0xd6, 0x00, 0x00], [60, 0xc0, 0x00, 0x00],
    [65, 0xff, 0x00, 0xff], [70, 0x99, 0x55, 0xc9], [75, 0xff, 0xff, 0xff],
];
function dbzColor(dbz) {
    let c = RAMP[0];
    for (let i = 0; i < RAMP.length; i++) {
        if (dbz >= RAMP[i][0]) c = RAMP[i]; else break;
    }
    return c;
}

// Base-velocity ramp (m/s): negative = toward the radar (greens), positive = away (reds),
// gray near zero. [maxMps, r, g, b]; the LOWEST stop whose maxMps >= v wins. Aliased values
// just wrap into the ends — v1 renders raw (un-dealiased) velocity, as radar apps do at first.
const VEL_RAMP = [
    [-28, 0x00, 0xff, 0x70], [-22, 0x00, 0xe0, 0x00], [-16, 0x00, 0xb0, 0x00],
    [-10, 0x00, 0x80, 0x00], [-5, 0x10, 0x55, 0x30], [-0.5, 0x70, 0x70, 0x70],
    [0.5, 0x80, 0x80, 0x80], [5, 0x55, 0x20, 0x20], [10, 0x90, 0x00, 0x00],
    [16, 0xc0, 0x00, 0x00], [22, 0xff, 0x10, 0x10], [9999, 0xff, 0x80, 0xc0],
];
function velColor(v) {
    for (let i = 0; i < VEL_RAMP.length; i++) {
        if (v <= VEL_RAMP[i][0]) return [VEL_RAMP[i][1], VEL_RAMP[i][2], VEL_RAMP[i][3]];
    }
    return [VEL_RAMP[VEL_RAMP.length - 1][1], VEL_RAMP[VEL_RAMP.length - 1][2], VEL_RAMP[VEL_RAMP.length - 1][3]];
}

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

// Lowest-tilt reflectivity geometry (gates >= minDbz). Reflectivity always lives at the lowest
// elevation NUMBER present (the surveillance cut), which the C# extractor writes first.
function buildReflectivity(radar, siteLat, siteLon, minDbz) {
    const elevations = radar.listElevations();
    if (!elevations || !elevations.length) return null;
    radar.setElevation(Math.min.apply(null, elevations));
    const refl = radar.getHighresReflectivity();
    const radials = Array.isArray(refl) ? refl : [refl];
    return buildGates(radials, function (i) { return radar.getAzimuth(i); }, siteLat, siteLon, function (dbz) {
        if (dbz === null || dbz === undefined || dbz < minDbz) return null;
        const c = dbzColor(dbz);
        return [c[1], c[2], c[3]];
    });
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
            const v = radar.getHighresVelocity();
            const arr = Array.isArray(v) ? v : [v];
            for (let i = 0; i < arr.length; i++) {
                if (arr[i] && arr[i].moment_data) return elevs[k];
            }
        } catch (e) { /* try the next elevation */ }
    }
    return null;
}

// Lowest-tilt base-velocity geometry, or null if the volume carries no velocity.
function buildVelocity(radar, siteLat, siteLon) {
    const elev = findVelocityElevation(radar);
    if (elev === null) return null;
    radar.setElevation(elev);
    const vel = radar.getHighresVelocity();
    const radials = Array.isArray(vel) ? vel : [vel];
    return buildGates(radials, function (i) { return radar.getAzimuth(i); }, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return velColor(v);
    });
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
        const geom = buildReflectivity(radar, siteLat, siteLon, minDbz);
        const velGeom = buildVelocity(radar, siteLat, siteLon);
        const t2 = performance.now();
        // Best-effort diagnostics: radial + gate counts of the lowest-tilt reflectivity, to
        // see whether a slow frame is carrying extra radials/cuts vs. a fast one.
        let radials = 0, gates = 0;
        try {
            const elevs = radar.listElevations();
            if (elevs && elevs.length) {
                radar.setElevation(Math.min.apply(null, elevs));
                const refl = radar.getHighresReflectivity();
                const arr = Array.isArray(refl) ? refl : [refl];
                radials = arr.length;
                for (let i = 0; i < arr.length; i++) {
                    if (arr[i] && arr[i].moment_data) { gates = arr[i].moment_data.length; break; }
                }
            }
        } catch (e) { /* stats only */ }
        return {
            geom: geom, velGeom: velGeom, decodeMs: Math.round(t1 - t0), buildMs: Math.round(t2 - t1),
            radials: radials, gates: gates, bytes: bytes,
        };
    });
}
