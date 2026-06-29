// Radar color scales — the SINGLE SOURCE OF TRUTH for how moment values map to colors.
//
// Imported by BOTH the decoder (radar-decode.js, which bakes per-gate colors) AND, eventually, a
// legend/color-bar UI (which can sample `rampColor` across [min, max] to draw a bar that is exactly
// what's on the map — no hand-maintained copy that can drift). Pure data + math: no DOM, GL, worker
// or decoder dependency, so it loads in the worker, the main thread, or a test harness alike.
//
// A ramp is:
//   { id, label, unit, min, max, interpolate, stops: [{ v, color: [r,g,b] }, ...] }   (stops ascending by v)
//   - interpolate=false (DISCRETE bands, NWS reflectivity style): a value in [stops[i].v, stops[i+1].v)
//     takes stops[i].color. The legend draws hard-edged bands.
//   - interpolate=true (smooth GRADIENT, base-velocity style): linearly blend between adjacent stops.
//     The legend draws a continuous gradient.
//   - min/max are the legend's display bounds (values clamp to the end stops).

// Returns [r,g,b] (0-255 ints) for `value` under `ramp`. Values past the ends clamp.
export function rampColor(ramp, value) {
    const s = ramp.stops;
    if (value <= s[0].v) return s[0].color;
    if (value >= s[s.length - 1].v) return s[s.length - 1].color;
    let i = 0;
    while (i + 1 < s.length && value >= s[i + 1].v) i++; // s[i].v <= value < s[i+1].v
    if (!ramp.interpolate) return s[i].color;            // discrete: lower-bound band
    const lo = s[i], hi = s[i + 1];
    const t = (value - lo.v) / (hi.v - lo.v);
    return [
        (lo.color[0] + (hi.color[0] - lo.color[0]) * t) | 0,
        (lo.color[1] + (hi.color[1] - lo.color[1]) * t) | 0,
        (lo.color[2] + (hi.color[2] - lo.color[2]) * t) | 0,
    ];
}

// Reflectivity (dBZ) — classic NWS discrete bands. Unchanged from the original ramp; kept here so
// reflectivity and velocity share one definition + one legend path. (Clear-air/below-threshold
// gates are dropped by MIN_DBZ in radar-decode.js before this is consulted.)
export const REFLECTIVITY_RAMP = {
    id: 'reflectivity', label: 'Reflectivity', unit: 'dBZ', min: 5, max: 75, interpolate: false,
    stops: [
        { v: 5, color: [0x00, 0xec, 0xec] }, { v: 10, color: [0x01, 0xa0, 0xf6] }, { v: 15, color: [0x00, 0x00, 0xf6] },
        { v: 20, color: [0x00, 0xff, 0x00] }, { v: 25, color: [0x00, 0xc8, 0x00] }, { v: 30, color: [0x00, 0x90, 0x00] },
        { v: 35, color: [0xff, 0xff, 0x00] }, { v: 40, color: [0xe7, 0xc0, 0x00] }, { v: 45, color: [0xff, 0x90, 0x00] },
        { v: 50, color: [0xff, 0x00, 0x00] }, { v: 55, color: [0xd6, 0x00, 0x00] }, { v: 60, color: [0xc0, 0x00, 0x00] },
        { v: 65, color: [0xff, 0x00, 0xff] }, { v: 70, color: [0x99, 0x55, 0xc9] }, { v: 75, color: [0xff, 0xff, 0xff] },
    ],
};

// Base velocity (m/s, toward-radar negative) — a deliberately designed diverging scale.
// Design:
//   • INBOUND stays TRUE GREEN through the common range — R held at 0 with a slight blue bias so
//     the bright greens read emerald, not lime/yellow. Only at strong inbound does it cool toward
//     cyan and then near-white, so velocity couplets stand out.
//   • OUTBOUND stays PURE RED through the common range — green AND blue held at 0 (no orange). It
//     brightens by raising G and B *together*, which desaturates red → pink → white rather than
//     rotating the hue toward orange/yellow.
//   • ZERO is neutral gray; lightness rises monotonically away from zero, and the two wings are
//     mirrored in lightness so neither side visually dominates.
//   • Range ±50 m/s covers dealiased values; out-of-range clamps to the end stops. Smooth gradient
//     so the (future) legend is a continuous bar that exactly matches the pixels.
export const VELOCITY_RAMP = {
    id: 'velocity', label: 'Base Velocity', unit: 'm/s', min: -50, max: 50, interpolate: true,
    stops: [
        { v: -50, color: [0xff, 0x40, 0xff] }, // extreme inbound — magenta
        { v: -42, color: [0xb0, 0x40, 0xff] }, // purple
        { v: -34, color: [0x60, 0x70, 0xff] }, // blue-violet
        { v: -27, color: [0x00, 0xb0, 0xff] }, // blue
        { v: -21, color: [0x00, 0xe0, 0xd0] }, // cyan
        { v: -15, color: [0x00, 0xe8, 0x70] }, // bright green (clean, slight cyan)
        { v: -9, color: [0x00, 0xc8, 0x3c] },  // green
        { v: -4, color: [0x00, 0x90, 0x34] },  // medium green
        { v: -1, color: [0x0a, 0x4e, 0x26] },  // near-zero inbound — dark green
        { v: 0, color: [0x5a, 0x5a, 0x5a] },   // zero — neutral gray
        { v: 1, color: [0x52, 0x16, 0x16] },   // near-zero outbound — dark red
        { v: 4, color: [0x96, 0x12, 0x12] },   // deeper red
        { v: 9, color: [0xd8, 0x00, 0x00] },   // red
        { v: 15, color: [0xff, 0x14, 0x14] },  // bright red
        { v: 21, color: [0xff, 0x52, 0x6a] },  // light red — brightening via pink (not orange)
        { v: 27, color: [0xff, 0x86, 0xa6] },  // pink
        { v: 34, color: [0xff, 0xb4, 0xd0] },  // light pink
        { v: 42, color: [0xff, 0xdc, 0xec] },  // pale pink
        { v: 50, color: [0xff, 0xff, 0xff] },  // extreme outbound — white
    ],
};

// Correlation coefficient (ρHV, dimensionless 0–1.05) — dual-pol. Uniform precip reads near 1.0;
// mixed/melting/hail dips to ~0.85–0.95; non-meteorological returns (tornado debris, ground clutter,
// biological, chaff) drop below ~0.8. The ramp spends its detail on 0.8–1.0 (where precip lives) and
// makes LOW values stand out cool (so a debris ball / clutter reads blue–purple–gray against the
// warm precip), with above-unity noise going white. Smooth gradient so a legend matches the pixels.
export const CORRELATION_RAMP = {
    id: 'cc', label: 'Correlation Coeff', unit: 'ρHV', min: 0.2, max: 1.05, interpolate: true,
    stops: [
        { v: 0.20, color: [0x6b, 0x6b, 0x6b] }, // very low — clutter / noise (gray)
        { v: 0.45, color: [0x90, 0x00, 0xc0] }, // purple
        { v: 0.65, color: [0x00, 0x00, 0xf0] }, // blue
        { v: 0.75, color: [0x00, 0xc0, 0xff] }, // light blue
        { v: 0.82, color: [0x00, 0xe0, 0xc8] }, // cyan
        { v: 0.88, color: [0x00, 0xc8, 0x00] }, // green
        { v: 0.92, color: [0xc0, 0xe0, 0x00] }, // yellow-green
        { v: 0.95, color: [0xff, 0xd0, 0x00] }, // gold
        { v: 0.97, color: [0xff, 0x80, 0x00] }, // orange
        { v: 0.99, color: [0xff, 0x00, 0x00] }, // red
        { v: 1.00, color: [0xff, 0x60, 0xd0] }, // pink (unity)
        { v: 1.05, color: [0xff, 0xff, 0xff] }, // above unity — white (artifacts)
    ],
};
