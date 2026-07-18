// NEXRAD Level II radar rendering for the single MapLibre map.
//
// Holds a loop of decoded frames (one per volume) and renders the current frame via a
// MapLibre WebGL custom layer beneath the boundary lines / outlook / labels. Heavy work
// (bzip2 decode + gate geometry) runs off the UI thread in radar-worker.js -> radar-decode.js;
// this file owns the GL layer, the frame store, and the host command shims. The host (C#)
// fetches each volume to the "radarlevel2" virtual host and drives the loop:
//   radarBeginLoop(lat,lon) -> radarAddFrame(url,index) xN -> radarShowFrame(index)
// Each built frame posts {type:'radarFrameReady', index, hasData} back to the host.
(function () {
    'use strict';

    // radar.js's own URL, captured at load time (document.currentScript is valid during this
    // synchronous IIFE). The worker is resolved relative to THIS file rather than the page, so it
    // keeps working when radar.js lives in a subfolder — a Worker URL otherwise resolves against the
    // document's base URL (the page), not the calling script, which would 404 → silent main-thread fallback.
    const SELF_SCRIPT = (document.currentScript && document.currentScript.src) || location.href;

    const LAYER_ID = 'level2-radar';
    const MIN_DBZ = 10;
    const GRID_NODATA = -32768; // matches radar-decode.js buildGrid sentinel

    // Shared site projection (geo.js — the SAME math radar-decode's buildGates uses, so overlays line
    // up with the painted gates). radar.js is a classic-script IIFE so it can't statically import; load
    // the module once at startup and cache it in `Geo`. Until it resolves, the geo-dependent overlays
    // (range ring, sweep, inspector) skip drawing and re-draw on the next frame/tick/mousemove — geo.js
    // is a tiny same-origin file, loaded long before any radar frame can decode.
    let Geo = null;
    import('./geo.js').then(function (m) { Geo = m; }).catch(function (e) { hostLog('geo.js load failed: ' + (e && e.message ? e.message : e)); });

    // Product registry (radar-products.js — the single source of truth shared with radar-decode.js).
    // Same tiny-module dynamic-import pattern as geo.js: loaded once at startup, cached in `Products`,
    // resolved long before the user can switch products / the first frame upgrades. productLazy() tells
    // the render/upgrade paths whether the active product is built lazily (velocity today); it defaults
    // to non-lazy for an unknown/not-yet-loaded id, which is safe (the default reflectivity isn't lazy).
    let Products = null;
    import('./radar-products.js').then(function (m) { Products = m.PRODUCTS; }).catch(function (e) { hostLog('radar-products.js load failed: ' + (e && e.message ? e.message : e)); });
    function productLazy(p) { return !!(Products && Products[p] && Products[p].lazy); }
    function productKnown(p) { return !Products || !!Products[p]; } // permissive until the registry loads
    // Whether every lazy product's geometry was already built in a decode result — used to reject a cache
    // hit / decide upgrades. True when the registry hasn't loaded yet (nothing known to be lazy).
    function lazyBuiltIn(r) {
        if (!Products) return true;
        for (var id in Products) { if (Products[id].lazy && !(r && r.built && r.built[id])) return false; }
        return true;
    }

    // frames[index] = { moments: { id: { positions, colors, count } | null }, grids, built, ... }:
    // per-product gate geometry (baked colors) keyed by product id (radar-products.js), so switching
    // product is a map lookup + upload — instant for eagerly-built products (no re-decode). A null/absent
    // moment means that product has nothing to draw on this frame. currentFrame is the index rendered.
    let frames = [];
    let currentFrame = -1;

    // ---- Decoded-frame cache (instant site revisits / replay toggles) ----
    // Decoding a volume (bzip2 + gate geometry + dealias) is the expensive part of a site load, and
    // beginLoop() wipes frames[] on every (re)selection — so revisiting a site, or toggling replay,
    // used to re-fetch + re-decode volumes we'd just built. This keeps the decoded result keyed by its
    // stable volume URL (radarlevel2/{site}_{yyyyMMdd_HHmmss}.V06 — deterministic per volume), so a
    // revisit reuses the geometry SYNCHRONOUSLY on the main thread (no fetch, no worker decode). The
    // geometry is immutable, so sharing the typed arrays between the cache and frames[] is safe; LRU-
    // capped to bound memory (the cached res also carries the inspector value-grids, so inspect stays
    // instant on revisit too). Survives beginLoop/clear on purpose — only the cap evicts.
    const decodedCache = new Map(); // url -> stored applyFrameResult-shaped result (arrays shared with frames[])
    const DECODE_CACHE_MAX = 96;    // ~a handful of sites' worth of loop frames; tune down if memory bites
    function cacheGet(url) {
        if (!url) return null;
        const v = decodedCache.get(url);
        if (v) { decodedCache.delete(url); decodedCache.set(url, v); } // re-insert = move to most-recently-used
        return v || null;
    }
    function cachePut(url, res) {
        if (!url || res.empty || res.error) return; // don't cache empties/failures — let them re-fetch
        decodedCache.delete(url);
        decodedCache.set(url, res);
        while (decodedCache.size > DECODE_CACHE_MAX) decodedCache.delete(decodedCache.keys().next().value);
    }

    // ---- Lazy-upgrade queue (bounded, current-frame-first) ----
    // Switching to Velocity (or turning Inspect on) needs the loaded frames re-decoded to add the
    // geometry they were built without (velocity/dealias, or the inspector grids). Firing all of them
    // at once floods the decode pool — on big dual-pol volumes (10-44 MB) the re-decodes can't keep up
    // and frames flash blank as playback runs over them. Instead we QUEUE the upgrades and run at most
    // UPGRADE_CONCURRENCY at a time, always picking the frame nearest the one on screen (preferring the
    // forward/playback direction), so velocity/grids fill in around what the user is watching. Re-pumped
    // as each upgrade finishes; the queue re-checks need at pump time, so a product switch mid-flight
    // just drains harmlessly. State is reset whenever the loop generation changes (beginLoop/remap/clear).
    var upgradeQueue = [];        // frame indices wanting an upgrade decode, not yet started
    var upgradeInFlight = {};     // idx -> true while its upgrade decode is outstanding
    var upgradeInFlightN = 0;
    var pumpingUpgrades = false;  // re-entrancy guard (a cache-hit upgrade completes synchronously)
    var UPGRADE_CONCURRENCY = 3;  // leave a worker free of the pool (size 4) for the current frame / loads
    function resetUpgrades() { upgradeQueue = []; upgradeInFlight = {}; upgradeInFlightN = 0; }
    function needsUpgrade(idx) {
        var f = frames[idx];
        if (!f || !f.url) return false;
        if (inspecting && !f.gridsBuilt) return true;             // no value grids, Inspect on
        // A lazy product (velocity) needs (re)building when it's the ACTIVE product OR being prefetched
        // and this frame lacks it. built[id] tracks whether the build RAN, so a frame with genuinely no
        // velocity (built.velocity=true, geometry null) won't re-decode forever.
        if (Products) {
            for (var id in Products) {
                if (Products[id].lazy && (product === id || velPrefetch) && !(f.built && f.built[id])) return true;
            }
        }
        return false;
    }
    function upgradePriority(idx) {
        if (currentFrame < 0) return idx;
        if (idx >= currentFrame) return idx - currentFrame;       // current (0) + ahead, in play order
        return (currentFrame - idx) + frames.length;              // behind the playhead: lowest priority
    }
    function queueUpgrade(idx) {
        if (!needsUpgrade(idx) || upgradeInFlight[idx]) return;
        if (upgradeQueue.indexOf(idx) < 0) upgradeQueue.push(idx);
        pumpUpgrades();
    }
    function queueAllUpgrades() { for (var i = 0; i < frames.length; i++) queueUpgrade(i); }
    function pumpUpgrades() {
        if (pumpingUpgrades) return; // a sync completion re-entered us; the outer loop keeps draining
        pumpingUpgrades = true;
        try {
            while (upgradeInFlightN < UPGRADE_CONCURRENCY && upgradeQueue.length) {
                var bestPos = -1, bestPri = Infinity;
                for (var p = 0; p < upgradeQueue.length; p++) {
                    var idx = upgradeQueue[p];
                    if (!needsUpgrade(idx)) continue;             // stale (already built / product changed)
                    var pri = upgradePriority(idx);
                    if (pri < bestPri) { bestPri = pri; bestPos = p; }
                }
                if (bestPos < 0) { upgradeQueue = upgradeQueue.filter(needsUpgrade); break; }
                var chosen = upgradeQueue.splice(bestPos, 1)[0];
                upgradeInFlight[chosen] = true;
                upgradeInFlightN++;
                decodeFrame(frames[chosen].url, chosen); // async, or sync on a cache hit (re-enters pump)
            }
        } finally {
            pumpingUpgrades = false;
        }
    }
    function upgradeDone(idx) { // an upgrade decode for idx settled (ok or error) — free its slot, pump next
        if (!upgradeInFlight[idx]) return;
        delete upgradeInFlight[idx];
        upgradeInFlightN--;
        pumpUpgrades();
    }
    // Tell the host how much of the loop is ready for the ACTIVE product, so the UI can show a
    // "Building velocity N/M" readout and playback can hold at the built frontier instead of stuttering
    // into a frame whose velocity is still being dealiased (~1.5 s each on big super-res volumes). Only
    // Velocity is lazily built; reflectivity/CC are always present, so for them every frame reads ready.
    function postBuildProgress() {
        var total = frames.length;
        if (!total) { post({ type: 'radarBuildProgress', product: product, built: 0, total: 0, ready: [] }); return; }
        var built = 0, ready = new Array(total);
        var lazy = productLazy(product); // non-lazy products are always ready (built eagerly)
        for (var i = 0; i < total; i++) {
            var r = !lazy || !!(frames[i] && frames[i].built && frames[i].built[product]);
            ready[i] = r;
            if (r) built++;
        }
        post({ type: 'radarBuildProgress', product: product, built: built, total: total, ready: ready });
    }

    let product = 'reflectivity'; // 'reflectivity' | 'velocity' — which moment to render
    let velPrefetch = false; // speculatively build velocity for every frame even when it's NOT the active
                             // product — armed by the host (prefetchVelocity) once reflectivity has
                             // rendered, so switching to Velocity is instant. Reset per new loop.
    let pendingFrame = -1;  // a frame requested via showFrame before it finished decoding; the
                            // decode that satisfies it promotes it to currentFrame (so showFrame
                            // never pins currentFrame to an undecoded index and blanks the layer).
    let uploadedFrame = -1; // which frame's geometry is currently in the GL buffers
    let uploadedProduct = ''; // which product's geometry is uploaded (re-upload on a product switch)
    let siteLat = 0, siteLon = 0;
    let opacity = 0.85;
    let loopToken = 0;      // bumped per loop so stale async frames are dropped
    let currentMap = null;
    // Range ring: a thin circle at the radar's REAL outer data extent (rangeMeters from the
    // decode), centred on the site — RadarScope-style. Its own GeoJSON line layer (survives
    // basemap switches via reAdd). currentRangeMeters = the radius currently drawn (0 = none).
    const RANGE_SRC = 'level2-range', RANGE_LAYER = 'level2-range';
    let currentRangeMeters = 0;
    // Sweep pulse: a one-shot rotating arm + trailing afterglow, drawn ON THE MAP (scaled to the real
    // coverage, RadarScope-style) — not a DOM decoration. The range ring is always shown; the arm only
    // appears to do ONE revolution when the host reports a genuinely-new frame (pulseSweep), then hides.
    // (Replaced the old continuous, phase-locked rotation.) Its own GeoJSON line layer, per-feature
    // opacity so the trail fades leading→tail; updated each animation frame while a pulse is running.
    const SWEEP_SRC = 'level2-sweep', SWEEP_LAYER = 'level2-sweep';
    const SWEEP_MS = 1300;       // duration of one revolution
    const SWEEP_FADE_MS = 400;   // brief fade-out of the trail once the revolution completes
    const SWEEP_TRAIL_DEG = 70;  // angular length of the trailing afterglow behind the leading arm
    const SWEEP_TRAIL_N = 18;    // trail segments (leading = brightest, tail = transparent)
    let sweepAnimStart = 0, sweepRaf = 0;

    // Render-path diagnostics: render() runs every frame, so rate-limit its logging. We track
    // the running error/blank counts and only emit on the first occurrence + periodically, plus
    // a one-shot "recovered" line, so the debug log shows WHEN tiles blanked without flooding.
    let renderErrCount = 0, lastRenderErrAt = 0, lastRenderErr = '';
    let blankCount = 0, lastBlankAt = 0, lastBlankReason = '';
    let drewSinceIssue = false;
    function noteRenderIssue(reason, isError) {
        const now = Date.now();
        if (isError) { renderErrCount++; lastRenderErr = reason; }
        else { blankCount++; lastBlankReason = reason; }
        const last = isError ? lastRenderErrAt : lastBlankAt;
        if (last === 0 || now - last > 3000) {
            if (isError) lastRenderErrAt = now; else lastBlankAt = now;
            post({
                type: 'radarRender', kind: isError ? 'error' : 'blank', reason: reason,
                cf: currentFrame, errs: renderErrCount, blanks: blankCount,
            });
        }
        drewSinceIssue = false;
    }
    function noteRenderOk() {
        if (!drewSinceIssue && (renderErrCount > 0 || blankCount > 0)) {
            drewSinceIssue = true;
            post({ type: 'radarRender', kind: 'recovered', cf: currentFrame, errs: renderErrCount, blanks: blankCount });
        }
    }

    // WebGL context loss is permanent unless restored — a prime suspect for "tiles vanish and
    // never come back" under the heavy per-frame geometry. Log both edges once.
    let ctxListenersAttached = false;
    function attachContextListeners(map) {
        if (ctxListenersAttached || !map || !map.getCanvas) return;
        try {
            const c = map.getCanvas();
            c.addEventListener('webglcontextlost', function () { post({ type: 'radarRender', kind: 'ctxlost', cf: currentFrame }); }, false);
            c.addEventListener('webglcontextrestored', function () { post({ type: 'radarRender', kind: 'ctxrestored', cf: currentFrame }); }, false);
            ctxListenersAttached = true;
        } catch (e) { hostLog('ctx listener attach failed: ' + (e && e.message ? e.message : e)); }
    }

    // GL objects (recreated in onAdd; null when the layer isn't attached).
    let program = null, posBuf = null, colorBuf = null;
    let aPos = -1, aColor = -1, uMatrix = null, uOpacity = null;

    function showError(msg) {
        document.body.insertAdjacentHTML('beforeend',
            '<div style="position:absolute;top:8px;left:8px;z-index:10;background:rgba(120,0,0,.85);' +
            'color:#fff;font:12px sans-serif;padding:6px 8px;border-radius:4px;max-width:60%">' +
            'Radar: ' + msg + '</div>');
    }
    function hostLog(msg) {
        try { console.log('[radar] ' + msg); } catch (e) { /* ignore */ }
        post({ type: 'radarLog', msg: String(msg) });
    }
    function post(obj) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(obj));
            }
        } catch (e) { /* ignore */ }
    }

    // ---- Off-thread decode via a Web Worker POOL ----
    // A single worker decodes its message queue serially, so the backfill was decode-bound (one frame
    // at a time). A pool of N workers decodes N frames in parallel across cores; round-robin dispatch.
    // Results carry {token,index} and applyFrameResult runs serially on the main thread, so out-of-order
    // completions across workers are safe. Pool persists for the app lifetime (creating workers is
    // expensive); each loads radar-decode.js + the vendored decoder independently (~a few MB each).
    const DECODE_POOL_SIZE = Math.max(1, Math.min(4,
        (typeof navigator !== 'undefined' && navigator.hardwareConcurrency) ? navigator.hardwareConcurrency - 1 : 3));
    let workerPool; // undefined = not tried, array = ready, null = Worker API unavailable
    let workerRR = 0;
    function getWorker() {
        if (workerPool === undefined) {
            try {
                workerPool = [];
                for (let i = 0; i < DECODE_POOL_SIZE; i++) {
                    const w = new Worker(new URL('radar-worker.js', SELF_SCRIPT).href);
                    w.onmessage = function (e) { applyFrameResult(e.data); };
                    w.onerror = function (e) { hostLog('worker error: ' + (e && e.message ? e.message : e)); };
                    workerPool.push(w);
                }
                hostLog('decode pool size=' + workerPool.length);
            } catch (e) {
                workerPool = null; // fall back to main-thread decode
                hostLog('worker unavailable; main-thread decode: ' + (e && e.message ? e.message : e));
            }
        }
        if (!workerPool || !workerPool.length) return null;
        const w = workerPool[workerRR % workerPool.length]; // round-robin next worker
        workerRR++;
        return w;
    }

    // Wraps a decode result (r2 from decodeAndBuild / decodeDowFrame — already the keyed
    // { moments, grids, built, gridsBuilt, ... } shape) into what applyFrameResult consumes, just
    // stamping this load's token/index/url. Used by the main-thread decode fallback and the DOW path.
    // NOTE: the Worker (radar-worker.js) builds the message itself because it must pass the typed-array
    // buffers as postMessage transferables — a worker-only concern it can't reach this IIFE-private helper for.
    function frameResultFrom(r2, token, index, url) {
        return Object.assign({}, r2, { token: token, index: index, url: url });
    }

    function applyFrameResult(res) {
        if (!res || res.token !== loopToken) return; // stale (loop changed)
        if (res.error) {
            upgradeDone(res.index); // free the upgrade slot (if this was one); don't retry a failing frame
            hostLog('frame ' + res.index + ' decode failed: ' + res.error);
            post({ type: 'radarFrameReady', index: res.index, hasData: false });
            return;
        }
        // Compute empty authoritatively from the moments map (every producer sends the same shape), so
        // cachePut below skips caching a no-geometry frame regardless of which path decoded it.
        var mo = res.moments || {};
        res.empty = !Object.keys(mo).some(function (id) { return mo[id]; });
        frames[res.index] = {
            // Geometry + inspector grids keyed by product id (radar-products.js) — see render / lookupValue.
            moments: mo,                    // { id: { positions, colors, count } | null }
            grids: res.grids || {},         // { id: value-grid | null } (present only when Inspect was on)
            built: res.built || {},         // { id: bool } — whether that product's build ran (lazy bookkeeping)
            velNyq: res.velNyq || 0,        // Nyquist (m/s) — lets the inspector show the raw fold of a dealiased gate
            // url = this frame's stable volume URL (so a product/inspect switch can re-decode it),
            // gridsBuilt = whether the inspector value grids were built (skipped by default; built on
            // demand — see setProduct / setInspect).
            url: res.url || null, gridsBuilt: !!res.gridsBuilt,
        };
        // Post the per-frame decode metrics as a STRUCTURED message (the C# RadarDiagnostics
        // service records them, evaluates the suspect heuristics, and quarantines a bad frame's
        // .V06). The metrics are already computed by the decoder; we just forward them losslessly.
        // Retain the decoded result for instant reuse on a site revisit / replay toggle (keyed by its
        // stable volume URL). Shares the typed arrays with frames[res.index] — safe, geometry is read-only.
        cachePut(res.url, res);
        upgradeDone(res.index); // if this arrival was a queued upgrade, free its slot + pump the next
        // Reconcile this just-arrived frame with the ACTIVE product/inspect state. It may have been
        // decoded WITHOUT the geometry the current view needs — refl-only while the user is on Velocity,
        // or without inspector grids while Inspect is on — because the product/Inspect was switched
        // mid-load, AFTER this frame's decode was posted but BEFORE it arrived, so the switch's queue
        // sweep couldn't see it yet (it wasn't in frames[] then). That race left a scattered set of
        // frames stuck refl-only on a slow past-event load (the "switch to Velocity shows nothing until I
        // reload" bug). Queue it for a bounded upgrade; needsUpgrade returns false once built, so there's
        // no decode loop and no cost when the product was already active at decode time.
        queueUpgrade(res.index);
        var reflCount = (mo.reflectivity && mo.reflectivity.count) || 0;
        var velCount = (mo.velocity && mo.velocity.count) || 0;
        post({
            type: 'radarFrame', index: res.index, empty: !!res.empty, cached: !!res.cached,
            tris: reflCount, velTris: velCount,
            decodeMs: res.decodeMs, buildMs: res.buildMs, bytes: res.bytes,
            elevList: res.elevList, velElev: res.velElev, velNyq: res.velNyq,
            reflStats: res.reflStats, velStats: res.velStats, dealias: res.dealias,
            decoded: frames.filter(Boolean).length, total: frames.length, cf: currentFrame,
        });
        try { console.log('[radar] decoded idx=' + res.index + (res.empty ? ' EMPTY' : ' tris=' + reflCount + ' velTris=' + velCount)); } catch (e) { /* ignore */ }

        // Decide what to show now that this frame is available. Crucially, ANY of these paths
        // re-adds the layer if it's missing (e.g. after a reload removed it) — so the radar can
        // never get stuck blank with a decoded current frame.
        if (currentMap) {
            if (res.index === pendingFrame) {
                // A showFrame() requested this index before it had decoded — promote it now.
                pendingFrame = -1;
                currentFrame = res.index;
                uploadedFrame = -1;
                showCurrent(currentMap, 'pending');
            } else if (currentFrame < 0) {
                // Nothing shown yet — adopt the first frame to arrive (host pushes newest first).
                currentFrame = res.index;
                uploadedFrame = -1;
                showCurrent(currentMap, 'first');
            } else if (res.index === currentFrame) {
                // The on-screen frame was re-decoded (live in-place update) — repaint / re-add.
                uploadedFrame = -1;
                showCurrent(currentMap, 're-add');
            }
            // Draw the real outer-extent range ring (RadarScope-style) from this frame's decoded
            // range — AFTER the radar layer (re)add above, so the ring sits on top of the fill.
            if (res.rangeMeters > 0) setRangeRing(currentMap, res.rangeMeters);
        }
        post({ type: 'radarFrameReady', index: res.index, hasData: !res.empty });
        postBuildProgress(); // this frame's build state may have changed the ready count
    }

    // ---- GL custom layer ----
    function compile(glc, type, src) {
        const s = glc.createShader(type);
        glc.shaderSource(s, src);
        glc.compileShader(s);
        if (!glc.getShaderParameter(s, glc.COMPILE_STATUS)) {
            throw new Error(glc.getShaderInfoLog(s) || 'shader compile failed');
        }
        return s;
    }

    function makeCustomLayer() {
        return {
            id: LAYER_ID,
            type: 'custom',
            onAdd: function (map, glc) {
                const vs = compile(glc, glc.VERTEX_SHADER,
                    'uniform mat4 u_matrix;' +
                    'attribute vec2 a_pos;' +
                    'attribute vec4 a_color;' +
                    'varying vec4 v_color;' +
                    'void main(){ v_color=a_color; gl_Position=u_matrix*vec4(a_pos,0.0,1.0); }');
                const fs = compile(glc, glc.FRAGMENT_SHADER,
                    'precision mediump float;' +
                    'uniform float u_opacity;' +
                    'varying vec4 v_color;' +
                    'void main(){ gl_FragColor=vec4(v_color.rgb, v_color.a*u_opacity); }');
                program = glc.createProgram();
                glc.attachShader(program, vs);
                glc.attachShader(program, fs);
                glc.linkProgram(program);
                if (!glc.getProgramParameter(program, glc.LINK_STATUS)) {
                    throw new Error(glc.getProgramInfoLog(program) || 'program link failed');
                }
                aPos = glc.getAttribLocation(program, 'a_pos');
                aColor = glc.getAttribLocation(program, 'a_color');
                uMatrix = glc.getUniformLocation(program, 'u_matrix');
                uOpacity = glc.getUniformLocation(program, 'u_opacity');
                posBuf = glc.createBuffer();
                colorBuf = glc.createBuffer();
                uploadedFrame = -1; // force a re-upload on first render
            },
            render: function (glc, args) {
                if (!program || currentFrame < 0) return;
                const f = frames[currentFrame];
                if (!f) { noteRenderIssue('no frame at cf=' + currentFrame, false); return; }
                // Pick the geometry for the active product — one keyed lookup (radar-products.js).
                let pos, col, cnt;
                const g = f.moments && f.moments[product];
                if (g) { pos = g.positions; col = g.colors; cnt = g.count; }
                try {
                    // Re-upload when the frame OR the product changed. Only latch the buffers as
                    // current once an upload actually happened: a frame that lacks this product's
                    // geometry (e.g. an archive frame in Velocity mode, or a live volume whose Doppler
                    // companion hadn't finished scanning) must NOT mark uploadedFrame, or the buffers
                    // stay stale-but-marked and a later frame that DOES carry the geometry is skipped.
                    if ((uploadedFrame !== currentFrame || uploadedProduct !== product) && pos && col) {
                        glc.bindBuffer(glc.ARRAY_BUFFER, posBuf);
                        glc.bufferData(glc.ARRAY_BUFFER, pos, glc.STATIC_DRAW);
                        glc.bindBuffer(glc.ARRAY_BUFFER, colorBuf);
                        glc.bufferData(glc.ARRAY_BUFFER, col, glc.STATIC_DRAW);
                        uploadedFrame = currentFrame;
                        uploadedProduct = product;
                    }
                    if (!cnt) return; // this product has nothing to draw on this frame

                    const matrix = (args && args.defaultProjectionData && args.defaultProjectionData.mainMatrix)
                        || (args && args.modelViewProjectionMatrix) || args;
                    glc.useProgram(program);
                    glc.uniformMatrix4fv(uMatrix, false, matrix);
                    glc.uniform1f(uOpacity, opacity);
                    glc.bindBuffer(glc.ARRAY_BUFFER, posBuf);
                    glc.enableVertexAttribArray(aPos);
                    glc.vertexAttribPointer(aPos, 2, glc.FLOAT, false, 0, 0);
                    glc.bindBuffer(glc.ARRAY_BUFFER, colorBuf);
                    glc.enableVertexAttribArray(aColor);
                    glc.vertexAttribPointer(aColor, 4, glc.UNSIGNED_BYTE, true, 0, 0);
                    glc.enable(glc.BLEND);
                    glc.blendFunc(glc.SRC_ALPHA, glc.ONE_MINUS_SRC_ALPHA);
                    glc.drawArrays(glc.TRIANGLES, 0, cnt);
                    glc.disableVertexAttribArray(aPos);
                    glc.disableVertexAttribArray(aColor);
                    glc.bindBuffer(glc.ARRAY_BUFFER, null);
                    noteRenderOk();
                } catch (e) {
                    noteRenderIssue((e && e.message ? e.message : String(e)), true);
                }
            },
            onRemove: function (map, glc) {
                if (posBuf) glc.deleteBuffer(posBuf);
                if (colorBuf) glc.deleteBuffer(colorBuf);
                if (program) glc.deleteProgram(program);
                posBuf = colorBuf = program = null;
            },
        };
    }

    // Beneath the watch boxes, the boundary lines (so borders draw over radar), the outlook, and the
    // labels. Watches sit under the boundaries too, so target them FIRST — otherwise whichever of the
    // two was added last (a site click vs the ~2-min watch refresh) would land on top.
    function beforeId(map) {
        if (map.getLayer('spc-watch-fill')) return 'spc-watch-fill';
        if (map.getLayer('boundaries_country')) return 'boundaries_country';
        if (map.getLayer('boundaries')) return 'boundaries';
        if (map.getLayer('spc-outlook-fill')) return 'spc-outlook-fill';
        const layers = (map.getStyle() && map.getStyle().layers) || [];
        const sym = layers.find(function (l) { return l.type === 'symbol'; });
        return sym ? sym.id : undefined;
    }
    function removeLayer(map) {
        if (map.getLayer(LAYER_ID)) { map.removeLayer(LAYER_ID); hostLog('layer removed'); }
    }
    function addLayer(map) {
        if (currentFrame < 0) return;
        removeLayer(map);
        uploadedFrame = -1;
        map.addLayer(makeCustomLayer(), beforeId(map));
        hostLog('layer added before=' + beforeId(map) + ' cf=' + currentFrame);
    }
    // Ensures the current frame is on screen: repaint if the layer is up, else (re)add it.
    // This is the single place that guarantees a decoded current frame is never left blank.
    function showCurrent(map, reason) {
        if (currentFrame < 0 || !frames[currentFrame]) return;
        if (map.getLayer(LAYER_ID)) {
            map.triggerRepaint();
        } else {
            hostLog('showCurrent(' + reason + ') idx=' + currentFrame + ' -> re-add layer');
            addLayer(map);
        }
    }

    // ---- Range ring (real outer data extent) ----
    // A 128-point circle at currentRangeMeters around the site, using the same equirectangular
    // metres-per-degree approximation as the gate geometry (radar-decode buildGates) so the ring
    // lines up exactly with the data's edge.
    function ringGeoJSON() {
        const N = 128, R = currentRangeMeters;
        const coords = [];
        for (let k = 0; k <= N; k++) {
            coords.push(Geo.siteToLngLat(siteLat, siteLon, R, (k / N) * 2 * Math.PI));
        }
        return { type: 'Feature', geometry: { type: 'LineString', coordinates: coords } };
    }
    function addRangeRing(map) {
        if (!map || !(currentRangeMeters > 0) || !Geo) return; // geo.js not loaded yet -> redraws on next setRangeRing
        if (map.getSource(RANGE_SRC)) map.getSource(RANGE_SRC).setData(ringGeoJSON());
        else map.addSource(RANGE_SRC, { type: 'geojson', data: ringGeoJSON() });
        if (!map.getLayer(RANGE_LAYER)) {
            map.addLayer({
                id: RANGE_LAYER, type: 'line', source: RANGE_SRC,
                paint: { 'line-color': '#9fe0ff', 'line-width': 1.3, 'line-opacity': 0.55, 'line-blur': 0.3 },
            }, beforeId(map));
        }
    }
    function removeRangeRing(map) {
        if (map && map.getLayer(RANGE_LAYER)) map.removeLayer(RANGE_LAYER);
        if (map && map.getSource(RANGE_SRC)) map.removeSource(RANGE_SRC);
    }
    // Draw/update the ring for the freshly-decoded range. Per-frame ranges are ~identical, so
    // only rebuild when it actually changes (or a layer is missing, e.g. after a re-add). The sweep
    // pulse owns its own layer (added on demand in pulseSweep), so nothing to ensure here.
    function setRangeRing(map, rangeMeters) {
        if (!map || !(rangeMeters > 0)) return;
        if (Math.abs(rangeMeters - currentRangeMeters) < 500 && map.getLayer(RANGE_LAYER)) {
            return;
        }
        currentRangeMeters = rangeMeters;
        addRangeRing(map);
    }

    // ---- Sweep pulse ----
    // The trailing fan: SWEEP_TRAIL_N+1 radial lines from the site out to the range-ring edge, spanning
    // SWEEP_TRAIL_DEG BEHIND the leading bearing (0 = due north), each carrying an `o` opacity so the
    // line layer fades leading(bright)→tail(transparent). `fade` scales the whole trail for the end fade.
    // Same metres-per-degree approximation as the ring/gates so the arm tracks the circle.
    function sweepFanGeoJSON(leadRad, fade) {
        const feats = [];
        const step = (SWEEP_TRAIL_DEG * Math.PI / 180) / SWEEP_TRAIL_N;
        for (let i = 0; i <= SWEEP_TRAIL_N; i++) {
            const ang = leadRad - i * step;
            if (ang < 0) break; // don't draw behind the sweep's start (north) on the first revolution
            const o = (1 - i / SWEEP_TRAIL_N) * fade;
            if (o <= 0.02) continue;
            const tip = Geo.siteToLngLat(siteLat, siteLon, currentRangeMeters, ang);
            feats.push({ type: 'Feature', properties: { o: o },
                geometry: { type: 'LineString', coordinates: [[siteLon, siteLat], tip] } });
        }
        return { type: 'FeatureCollection', features: feats };
    }
    function ensureSweepLayer(map) {
        if (!map || !(currentRangeMeters > 0) || !Geo) return; // geo.js not loaded yet
        if (!map.getSource(SWEEP_SRC)) map.addSource(SWEEP_SRC, { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
        if (!map.getLayer(SWEEP_LAYER)) {
            // Added after the range ring → sits on top of the ring + radar fill. Per-feature opacity.
            map.addLayer({
                id: SWEEP_LAYER, type: 'line', source: SWEEP_SRC,
                paint: { 'line-color': '#ffe8a8', 'line-width': 1.6, 'line-blur': 0.4, 'line-opacity': ['get', 'o'] },
            }, beforeId(map));
        }
    }
    function clearSweepData(map) {
        const src = map && map.getSource(SWEEP_SRC);
        if (src) src.setData({ type: 'FeatureCollection', features: [] });
    }
    // Stop any in-flight pulse and drop its layer (site change / clear / DOW / turn-off).
    function stopSweep(map) {
        if (sweepRaf) { cancelAnimationFrame(sweepRaf); sweepRaf = 0; }
        if (map && map.getLayer(SWEEP_LAYER)) map.removeLayer(SWEEP_LAYER);
        if (map && map.getSource(SWEEP_SRC)) map.removeSource(SWEEP_SRC);
    }
    function sweepPulseFrame() {
        sweepRaf = 0;
        if (!currentMap || !(currentRangeMeters > 0) || !Geo) return; // nothing to draw (e.g. layer dropped)
        const el = performance.now() - sweepAnimStart;
        if (el >= SWEEP_MS + SWEEP_FADE_MS) { clearSweepData(currentMap); return; } // revolution done → hide arm
        let lead, fade;
        if (el < SWEEP_MS) { lead = (el / SWEEP_MS) * 2 * Math.PI; fade = 1; }       // sweeping 0→360°
        else { lead = 2 * Math.PI; fade = 1 - (el - SWEEP_MS) / SWEEP_FADE_MS; }     // hold at north, fade the trail out
        const src = currentMap.getSource(SWEEP_SRC);
        if (src) src.setData(sweepFanGeoJSON(lead, fade));
        sweepRaf = requestAnimationFrame(sweepPulseFrame);
    }
    // Fire ONE sweep pulse (host calls this when a genuinely-new frame lands). Restarts if one is
    // already mid-flight. No-op until a frame has decoded (no radius to sweep yet).
    function startSweepPulse(map) {
        currentMap = map;
        if (!(currentRangeMeters > 0) || !Geo) return;
        ensureSweepLayer(map);
        sweepAnimStart = performance.now();
        if (!sweepRaf) sweepRaf = requestAnimationFrame(sweepPulseFrame);
    }

    // Decodes one volume into frames[index] (off-thread, with a main-thread fallback).
    function decodeFrame(url, index) {
        const myToken = loopToken;
        // Velocity is the only product that must dealias (expensive), so build it when it's the active
        // product OR while speculatively prefetching it (velPrefetch — armed by the host once the
        // reflectivity loop has rendered, so a later switch to Velocity is instant/near-instant). On
        // reflectivity/CC with prefetch off we skip it and re-decode on demand (setProduct).
        const wantLazy = productLazy(product) || velPrefetch; // build lazy products (velocity) when active OR prefetching
        const wantGrids = inspecting; // inspector value grids are only needed while Inspect is on
        // Cache hit → reuse the decoded geometry synchronously (no fetch, no worker). This is what
        // makes a site revisit / replay toggle instant. Reject a hit that lacks a piece we need now —
        // the lazy product's geometry (a refl-only decode from a prior view) or the inspector grids
        // (decoded with Inspect off) — so we fall through and build it this time. Clone with THIS load's
        // token+index; arrays shared.
        const hit = cacheGet(url);
        if (hit && (!wantLazy || lazyBuiltIn(hit)) && (!wantGrids || hit.gridsBuilt)) {
            hostLog('frame ' + index + ' cache hit');
            applyFrameResult(Object.assign({}, hit, { token: myToken, index: index, cached: true }));
            return;
        }
        fetch(url, { cache: 'no-store' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.arrayBuffer();
        }).then(function (ab) {
            if (myToken !== loopToken) return;
            const w = getWorker();
            if (w) {
                w.postMessage({ ab: ab, siteLat: siteLat, siteLon: siteLon, minDbz: MIN_DBZ, token: myToken, index: index, url: url, buildLazy: wantLazy, buildGrids: wantGrids }, [ab]);
            } else {
                import('./radar-decode.js').then(function (m) {
                    return m.decodeAndBuild(ab, siteLat, siteLon, MIN_DBZ, wantLazy, wantGrids);
                }).then(function (r2) {
                    applyFrameResult(frameResultFrom(r2, myToken, index, url));
                }).catch(function (err) {
                    applyFrameResult({ token: myToken, index: index, error: String(err && err.message ? err.message : err) });
                });
            }
        }).catch(function (err) {
            upgradeDone(index); // this path skips applyFrameResult — free the upgrade slot so the pump doesn't stall
            hostLog('frame ' + index + ' fetch failed: ' + (err && err.message ? err.message : err));
            post({ type: 'radarFrameReady', index: index, hasData: false });
        });
    }

    // ---- Inspector (RadarScope-style "read the value under the cursor") ----
    // In inspect mode a mousemove projects the cursor lng/lat back to the site's polar frame
    // (the SAME equirectangular math buildGates uses, so the inspected gate is exactly the painted
    // one), reads the value from the current frame's grid for the active product, shows a DOM tooltip
    // next to the cursor, and pushes the value to the host (throttled) so the color-scale bar can mark
    // it in real time. All lookups are pure main-thread array reads — no re-decode, no GL readback.
    let inspecting = false;
    let inspectTip = null;          // the DOM tooltip element (lazily created)
    let inspectMove = null, inspectOut = null; // bound MapLibre handlers (so we can off() them)
    let lastInspectPush = 0, lastInspectHad = false; // host-push throttle state

    function ensureInspectTip() {
        if (inspectTip) return inspectTip;
        const el = document.createElement('div');
        el.id = 'radar-inspect-tip';
        el.style.cssText = 'position:absolute;z-index:20;pointer-events:none;display:none;' +
            'font:600 12px/1.3 "Segoe UI",sans-serif;color:#fff;background:rgba(10,12,16,.85);' +
            'border:1px solid rgba(255,255,255,.25);border-radius:4px;padding:3px 7px;white-space:nowrap;' +
            'box-shadow:0 1px 4px rgba(0,0,0,.55);';
        document.body.appendChild(el);
        inspectTip = el;
        return el;
    }

    // The value grid for the current frame + active product, or null if not available.
    function inspectGrid() {
        const f = frames[currentFrame];
        return (f && f.grids && f.grids[product]) || null;
    }

    // Reads the moment value at a geographic point from a polar value grid, or null (no data /
    // out of range). Mirrors buildGates' projection: x∝sin(az), y∝cos(az), az from north clockwise.
    function lookupValue(grid, lat, lng) {
        if (!grid || !grid.values || !Geo) return null; // values null = grid was built metadata-only (Inspect was off)
        const polar = Geo.lngLatToPolar(siteLat, siteLon, lng, lat);
        const rangeKm = polar.rangeMeters / 1000, azDeg = polar.azDeg;
        const j = Math.floor((rangeKm - grid.firstGate) / grid.gateSize);
        if (j < 0 || j >= grid.nGates) return null;
        // Nearest radial by azimuth (unsorted, ~720 entries — trivial per move). Reject if the
        // closest beam is too far (a gap or beyond the sweep), so we don't report a bogus value.
        let best = -1, bestD = 999;
        for (let i = 0; i < grid.az.length; i++) {
            const a = grid.az[i]; if (isNaN(a)) continue;
            let dd = Math.abs(a - azDeg); if (dd > 180) dd = 360 - dd;
            if (dd < bestD) { bestD = dd; best = i; }
        }
        if (best < 0 || bestD > 2) return null;
        const q = grid.values[best * grid.nGates + j];
        if (q === GRID_NODATA) return null;
        return { value: q / grid.scale, unit: grid.unit, digits: grid.digits };
    }

    // Push the inspected value to the host for the color-scale marker. Throttled (~14/s) and edge-
    // triggered on the has/has-not transition so leaving data hides the marker promptly.
    function pushInspect(has, value) {
        const now = Date.now();
        if (has === lastInspectHad && now - lastInspectPush < 70) return;
        lastInspectPush = now; lastInspectHad = has;
        post({ type: 'radarInspect', has: has, value: has ? value : 0 });
    }

    function onInspectMove(e) {
        if (!inspecting) return;
        const r = lookupValue(inspectGrid(), e.lngLat.lat, e.lngLat.lng);
        const el = ensureInspectTip();
        if (r) {
            // Velocity reads in familiar speed units (mph · km/h) rather than the raw m/s; other
            // products keep their native unit (dBZ / unitless CC).
            const main = product === 'velocity'
                ? formatSpeed(r.value)
                : r.value.toFixed(r.digits) + (r.unit ? ' ' + r.unit : '');
            // On Velocity, show the SAME gate's dealiasing breakdown so the unfold can be checked
            // without re-hovering: the displayed value is the dealiased speed; the raw (folded)
            // value is what the radar measured (within ±Nyquist), recovered by removing the whole
            // 2×Nyquist folds the dealiaser added. Lets the user confirm high velocities at a glance.
            const vel = velocityFold(r.value);
            if (vel) {
                el.innerHTML = '<div>' + main + '</div>' +
                    '<div style="font-size:10px;opacity:.75;font-weight:400">raw ' + vel.raw.toFixed(0) +
                    ' · Nyq ' + vel.nyq.toFixed(0) + ' · ' + vel.foldLabel + '</div>';
            } else {
                el.textContent = main;
            }
            el.style.left = (e.point.x + 14) + 'px';
            el.style.top = (e.point.y + 14) + 'px';
            el.style.display = 'block';
            pushInspect(true, r.value);
        } else {
            el.style.display = 'none';
            pushInspect(false, 0);
        }
    }

    // Velocity in m/s → "±47 mph · ±76 km/h" (sign preserved: inbound negative, outbound positive).
    function formatSpeed(ms) {
        return (ms * 2.23694).toFixed(0) + ' mph · ' + (ms * 3.6).toFixed(0) + ' km/h';
    }

    // For a dealiased velocity value, recover the raw (folded) measurement + the fold count from the
    // current frame's Nyquist. Returns null when not on Velocity / no Nyquist (so other products show
    // just their value). Dealiasing only ever adds whole multiples of 2×Nyquist, so this is exact.
    function velocityFold(dealiased) {
        if (product !== 'velocity') return null;
        const f = frames[currentFrame];
        const nyq = f && f.velNyq;
        if (!(nyq > 0)) return null;
        const folds = Math.round(dealiased / (2 * nyq));
        const raw = dealiased - folds * 2 * nyq;
        const foldLabel = folds === 0 ? 'no fold'
            : (folds > 0 ? '+' : '') + folds + ' fold' + (Math.abs(folds) === 1 ? '' : 's');
        return { raw: raw, nyq: nyq, folds: folds, foldLabel: foldLabel };
    }
    function onInspectOut() {
        if (inspectTip) inspectTip.style.display = 'none';
        pushInspect(false, 0);
    }

    window.RadarLayer = {
        beginLoop: function (map, lat, lon) {
            currentMap = map;
            attachContextListeners(map);
            // New site → drop the old range ring + sweep (the first decoded frame redraws them at
            // the new site's range); same site (a reload) → keep them up, no flicker.
            if (lat !== siteLat || lon !== siteLon) {
                removeRangeRing(map); stopSweep(map); currentRangeMeters = 0;
            }
            siteLat = lat; siteLon = lon;
            loopToken++;            // invalidate any in-flight frames from a previous loop
            resetUpgrades();        // and drop any pending/in-flight lazy-upgrade decodes from it
            velPrefetch = false;    // new site: build reflectivity first; the host re-arms velocity prefetch once it's ready
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            uploadedFrame = -1;
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayer(map);
            hostLog('beginLoop token=' + loopToken + ' @ ' + lat.toFixed(3) + ',' + lon.toFixed(3));
        },
        addFrame: function (map, url, index) {
            currentMap = map;
            hostLog('addFrame idx=' + index);
            decodeFrame(url, index);
        },
        showFrame: function (map, index) {
            currentMap = map;
            if (frames[index]) {
                // Decoded: switch to it now (and (re)add the layer if needed).
                pendingFrame = -1;
                if (index !== currentFrame) { currentFrame = index; uploadedFrame = -1; }
                showCurrent(map, 'showFrame');
            } else {
                // Not decoded yet: remember the intent but keep the current frame on screen, so
                // we don't blank the layer. applyFrameResult promotes it once it decodes.
                pendingFrame = index;
                hostLog('showFrame idx=' + index + ' pending (not decoded; keeping cf=' + currentFrame + ')');
            }
        },
        // Incremental loop refresh: reindex the existing decoded frames to a new ordering instead
        // of tearing the loop down. `mappingJson` is an array of [fromIndex, toIndex] pairs; each
        // reused frame's geometry object is carried over (NOT re-decoded), the live frame included.
        // The host then decodes only the genuinely-new volume(s) into the unfilled slots. Crucially
        // this NEVER removes the layer, so an archive reload no longer blanks the radar — the frame
        // already on screen stays up (same geometry, no re-upload) while the new frames stream in.
        remap: function (map, newCount, mappingJson) {
            currentMap = map;
            var mapping;
            try { mapping = JSON.parse(mappingJson); } catch (e) { hostLog('remap parse failed: ' + (e && e.message ? e.message : e)); return; }
            // New loop generation: drop any in-flight decode still targeting an OLD index.
            loopToken++;
            resetUpgrades(); // stale upgrades target old indices; re-queued below against the new frames[]
            var oldCurrent = currentFrame, oldUploaded = uploadedFrame;
            var nf = new Array(newCount);
            var newCurrent = -1;
            for (var k = 0; k < mapping.length; k++) {
                var from = mapping[k][0], to = mapping[k][1];
                if (to >= 0 && to < newCount && frames[from]) nf[to] = frames[from];
                if (from === oldCurrent && to >= 0) newCurrent = to;
            }
            frames = nf;
            pendingFrame = -1;
            if (newCurrent >= 0 && frames[newCurrent]) {
                // The displayed frame survived: keep its image up. The GL buffers already hold this
                // geometry (same object), so skip the re-upload when uploadedFrame tracked it.
                currentFrame = newCurrent;
                uploadedFrame = (oldUploaded === oldCurrent) ? newCurrent : -1;
                if (map.getLayer(LAYER_ID)) map.triggerRepaint(); else addLayer(map);
            } else {
                // The displayed frame fell out of the window: fall back to the newest decoded frame.
                var nn = -1;
                for (var j = newCount - 1; j >= 0; j--) { if (frames[j]) { nn = j; break; } }
                currentFrame = nn;
                uploadedFrame = -1;
                showCurrent(map, 'remap-fallback');
            }
            queueAllUpgrades(); // a reused refl-only frame still needs velocity if Velocity is active
            hostLog('remap newCount=' + newCount + ' cf=' + currentFrame + ' reused=' + frames.filter(Boolean).length + ' token=' + loopToken);
        },
        clear: function (map) {
            loopToken++;
            resetUpgrades();
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            removeLayer(map);
            removeRangeRing(map);
            stopSweep(map);
            currentRangeMeters = 0;
            postBuildProgress(); // frames=[] -> 0/0 clears the "building" readout
            hostLog('clear token=' + loopToken);
        },
        // DOW Event Viewer: show a single curated mobile-radar frame (the "dow-frame/1" JSON from
        // tools/dow_import.py, served from the dowevents host). It takes over the radar layer as a
        // one-frame loop centred on the TRUCK's position — reusing the whole render path (WebGL fill,
        // the real-extent range ring, product toggle, Inspect, legend). No loop/live/sweep machinery.
        showDow: function (map, url) {
            currentMap = map;
            attachContextListeners(map);
            loopToken++;
            resetUpgrades();
            const myToken = loopToken;
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            uploadedFrame = -1;
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayer(map);
            removeRangeRing(map);
            stopSweep(map);
            currentRangeMeters = 0; // a DOW frame is a single sweep — no rotating arm
            hostLog('showDow ' + url);
            fetch(url, { cache: 'no-store' }).then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            }).then(function (json) {
                if (myToken !== loopToken) return null;
                siteLat = (typeof json.lat === 'number') ? json.lat : 0;
                siteLon = (typeof json.lon === 'number') ? json.lon : 0;
                // Recenter on the truck — a DOW frame is a specific (often far-off) deployment, so
                // unlike the NEXRAD loop we DO fly there, or it would render off-screen.
                try { map.flyTo({ center: [siteLon, siteLat], zoom: 9, duration: 800 }); } catch (e) { /* non-fatal */ }
                return import('./radar-decode.js').then(function (m) {
                    return m.decodeDowFrame(json, MIN_DBZ);
                });
            }).then(function (r2) {
                if (!r2 || myToken !== loopToken) return;
                // Feed it as frame 0 — applyFrameResult adopts the first frame (currentFrame<0),
                // (re)adds the layer, and draws the range ring from r2.rangeMeters. DOW always builds
                // velocity (pre-dealiased, cheap) + grids so built.velocity/gridsBuilt=true; url undefined = not cached.
                applyFrameResult(frameResultFrom(r2, myToken, 0, undefined));
            }).catch(function (err) {
                hostLog('showDow failed: ' + (err && err.message ? err.message : err));
            });
        },
        setOpacity: function (map, op) {
            opacity = op;
            if (map.getLayer(LAYER_ID)) map.triggerRepaint();
        },
        // Fire ONE sweep pulse (arm + trailing afterglow, one revolution then hides). The host calls
        // this when a genuinely-new frame lands. No-op until a frame has decoded (no radius yet).
        pulseSweep: function (map) {
            startSweepPulse(map);
        },
        // Stop + remove the sweep (host calls with period <= 0 on clear / entering replay). Kept the
        // name for the existing host shim; the arm is one-shot now, so a positive period just re-pulses.
        setSweep: function (map, periodSeconds) {
            currentMap = map;
            if (Number(periodSeconds) > 0) startSweepPulse(map);
            else stopSweep(map);
        },
        // Switch rendered moment ('reflectivity' | 'velocity' | 'cc'). Reflectivity + CC geometry is
        // always built, so those switch instantly. Velocity is built lazily (it's the one product that
        // must dealias), so switching TO Velocity queues the loaded refl-only frames for a BOUNDED,
        // current-frame-first re-decode (see the upgrade queue up top) — velocity fills in around the
        // frame on screen instead of flooding the decode pool and flashing blanks during playback.
        setProduct: function (map, p) {
            if (!productKnown(p) || p === product) return;
            product = p;
            uploadedFrame = -1; // force the new product's geometry to upload on the next render
            hostLog('product=' + product);
            queueAllUpgrades(); // no-op unless Velocity (or Inspect) needs geometry these frames lack
            postBuildProgress(); // switching to Velocity: report the (mostly not-yet-built) ready set now
            if (map && map.getLayer(LAYER_ID)) map.triggerRepaint();
        },
        // Speculatively build Velocity geometry for the whole loop IN THE BACKGROUND, before the user
        // selects the Velocity product — armed by the host once reflectivity has finished rendering, so a
        // later switch to Velocity is instant (or nearly so). Reuses the SAME bounded, current-frame-first
        // upgrade queue as an on-demand switch, just started early and at low urgency; velPrefetch persists
        // so frames added later (live poll, incremental reload) get their velocity built too. Idempotent,
        // and a no-op cost-wise once every frame is built (needsUpgrade returns false).
        prefetchVelocity: function () {
            if (velPrefetch) return;
            velPrefetch = true;
            queueAllUpgrades();
        },
        // Re-add after a basemap switch (setStyle drops custom layers + sources); frames + the range
        // ring are retained, so restore them. If a sweep pulse is mid-flight, restore its layer too so
        // the in-progress revolution keeps drawing.
        reAdd: function (map) {
            currentMap = map;
            if (currentFrame >= 0) addLayer(map);
            addRangeRing(map);
            if (sweepRaf) ensureSweepLayer(map);
        },
        // Toggle inspect mode (read the value under the cursor). Attaches/detaches the mousemove
        // handlers + crosshair cursor and hides the tooltip / clears the host marker when off.
        setInspect: function (map, on) {
            currentMap = map;
            inspecting = !!on;
            const canvas = map.getCanvas && map.getCanvas();
            if (inspecting) {
                if (!inspectMove) { inspectMove = onInspectMove; map.on('mousemove', inspectMove); }
                if (!inspectOut) { inspectOut = onInspectOut; map.on('mouseout', inspectOut); }
                if (canvas) canvas.style.cursor = 'crosshair';
                // Value grids are skipped by default (memory). Turning Inspect ON now builds them on
                // demand for the loaded frames via the bounded, current-frame-first upgrade queue — so
                // lookups become available around the frame on screen first, without flooding the pool.
                queueAllUpgrades();
            } else {
                if (inspectMove) { map.off('mousemove', inspectMove); inspectMove = null; }
                if (inspectOut) { map.off('mouseout', inspectOut); inspectOut = null; }
                if (inspectTip) inspectTip.style.display = 'none';
                if (canvas) canvas.style.cursor = '';
                pushInspect(false, 0);
            }
            hostLog('inspect=' + inspecting);
        },
    };
})();
