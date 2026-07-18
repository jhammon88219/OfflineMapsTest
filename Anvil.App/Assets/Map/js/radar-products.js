// radar-products.js — the single source of truth for the radar product REGISTRY: which moments the app
// renders and their per-product traits. Both the decoder (radar-decode.js, static import) and the
// render/upgrade layer (radar.js, dynamic cached import) read this, so adding a product is ONE entry
// here (+ a build fn in radar-decode's BUILDERS + a ramp in radar-ramps.js) instead of editing the
// flat per-moment fields (velPositions/ccPositions/…) that used to be scattered across the
// decode → transfer → render path.
//
// `lazy: true` marks a product whose geometry is EXPENSIVE to build — today only velocity, because it's
// the one moment that must dealias (dealiasSweep, ~1.5 s/frame). Lazy products are built on demand /
// prefetched rather than eagerly (see radar.js's upgrade queue). Non-lazy products (reflectivity, CC,
// and the future ZDR / spectrum width / ΦDP) are cheap and always built.
export const PRODUCTS = {
    reflectivity: { lazy: false },
    velocity:     { lazy: true },
    cc:           { lazy: false },
    kdp:          { lazy: false }, // derived from ΦDP (½·dΦDP/dr); cheap windowed slope, no dealias
    zdr:          { lazy: false }, // direct dual-pol moment read (drop shape/size); cheap, no dealias
    sw:           { lazy: false }, // direct Doppler moment read (velocity spread); cheap, no dealias
};

// Registry / iteration order. Reflectivity is first deliberately: it's the widest sweep, so the on-map
// range ring is taken from whichever built product comes first (see decodeAndBuild).
export const PRODUCT_IDS = Object.keys(PRODUCTS);

// Is this product's geometry expensive/lazy? Safe for unknown ids (returns false).
export function isLazy(id) { return !!(PRODUCTS[id] && PRODUCTS[id].lazy); }
