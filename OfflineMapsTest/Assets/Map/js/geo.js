// geo.js — the ONE canonical definition of the radar's site-relative coordinate projection, shared by
// the decoder (radar-decode.js, static ES-module import) and the renderer (radar.js, async dynamic
// import + cache, since it's a classic-script IIFE). Equirectangular ("flat-earth") approximation
// around the radar site: at these ranges (≤ ~460 km) it matches the painted gate geometry exactly, and
// it's what every overlay MUST agree with — the range ring, the sweep arm, and the inspector all
// project through here so they line up with the gates. Pure + stateless; callers pass the site position.
//
// PERF NOTE: buildGates (radar-decode) projects MILLIONS of gates per sweep in a hot loop, so it only
// borrows metersPerDeg() (computed once per sweep) and keeps its per-gate formula inline. The non-hot
// callers (ring = 128 pts, sweep = 1 line/frame, inspector = 1/mousemove) use the helpers below.

export const D2R = Math.PI / 180;

// Metres per degree at a latitude (equirectangular). Latitude is ~constant; longitude shrinks by cos.
export function metersPerDeg(lat) {
    return { mPerDegLat: 111320, mPerDegLon: 111320 * Math.cos(lat * D2R) };
}

// Site-relative polar (range in METRES, azimuth in RADIANS clockwise from north) -> [lng, lat].
export function siteToLngLat(siteLat, siteLon, rangeMeters, azRad) {
    const { mPerDegLat, mPerDegLon } = metersPerDeg(siteLat);
    return [
        siteLon + (rangeMeters * Math.sin(azRad)) / mPerDegLon,
        siteLat + (rangeMeters * Math.cos(azRad)) / mPerDegLat,
    ];
}

// [lng, lat] -> site-relative polar { rangeMeters, azDeg } (azimuth clockwise from north, 0..360).
export function lngLatToPolar(siteLat, siteLon, lng, lat) {
    const { mPerDegLat, mPerDegLon } = metersPerDeg(siteLat);
    const dx = (lng - siteLon) * mPerDegLon, dy = (lat - siteLat) * mPerDegLat;
    let azDeg = Math.atan2(dx, dy) / D2R;
    if (azDeg < 0) azDeg += 360;
    return { rangeMeters: Math.sqrt(dx * dx + dy * dy), azDeg: azDeg };
}
