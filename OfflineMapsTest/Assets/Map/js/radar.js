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

    // frames[index] = { positions, colors, count, velPositions, velColors, velCount }: the
    // reflectivity AND velocity gate geometry (each with baked colors), so switching product is
    // instant (no re-decode). count/velCount are 0 when that product has nothing to draw.
    // currentFrame is the index being rendered.
    let frames = [];
    let currentFrame = -1;
    let product = 'reflectivity'; // 'reflectivity' | 'velocity' — which moment to render
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
    // Sweep arm: a rotating line from the site centre out to the range ring, drawn ON THE MAP
    // (scaled to the real coverage, RadarScope-style) — not a DOM decoration. Its own GeoJSON line
    // layer, updated each animation frame. The host phase-locks it to the live-poll cycle via
    // setSweep(periodSeconds): one revolution == the time until the next update. period 0 = off.
    const SWEEP_SRC = 'level2-sweep', SWEEP_LAYER = 'level2-sweep';
    let sweepPeriodMs = 0, sweepT0 = 0, sweepRaf = 0;

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

    // ---- Off-thread decode via Web Worker ----
    let worker; // undefined = not tried, Worker = ready, null = unavailable
    function getWorker() {
        if (worker === undefined) {
            try {
                worker = new Worker(new URL('radar-worker.js', SELF_SCRIPT).href);
                worker.onmessage = function (e) { applyFrameResult(e.data); };
                worker.onerror = function (e) { hostLog('worker error: ' + (e && e.message ? e.message : e)); };
            } catch (e) {
                worker = null;
                hostLog('worker unavailable; main-thread decode: ' + (e && e.message ? e.message : e));
            }
        }
        return worker;
    }

    // Flattens a decode result (r2 from decodeAndBuild / decodeDowFrame: { geom, velGeom, ccGeom,
    // reflGrid, ... }) into the flat message shape applyFrameResult consumes. Used by the main-thread
    // decode fallback and the DOW path. NOTE: the Worker (radar-worker.js) builds this same shape itself
    // rather than calling here, because it must pass the typed-array buffers as postMessage transferables
    // — a worker-only concern, and the worker can't reach this IIFE-private helper anyway.
    function frameResultFrom(r2, token, index) {
        return {
            token: token, index: index, empty: !r2.geom && !r2.velGeom && !r2.ccGeom,
            positions: r2.geom && r2.geom.positions, colors: r2.geom && r2.geom.colors, count: r2.geom && r2.geom.count,
            velPositions: r2.velGeom && r2.velGeom.positions, velColors: r2.velGeom && r2.velGeom.colors, velCount: r2.velGeom && r2.velGeom.count,
            ccPositions: r2.ccGeom && r2.ccGeom.positions, ccColors: r2.ccGeom && r2.ccGeom.colors, ccCount: r2.ccGeom && r2.ccGeom.count,
            reflGrid: r2.reflGrid, velGrid: r2.velGrid, ccGrid: r2.ccGrid, rangeMeters: r2.rangeMeters,
            decodeMs: r2.decodeMs, buildMs: r2.buildMs, radials: r2.radials, gates: r2.gates, bytes: r2.bytes,
            elevList: r2.elevList, velElev: r2.velElev, reflStats: r2.reflStats, velStats: r2.velStats,
            velNyq: r2.velNyq, dealias: r2.dealias,
        };
    }

    function applyFrameResult(res) {
        if (!res || res.token !== loopToken) return; // stale (loop changed)
        if (res.error) {
            hostLog('frame ' + res.index + ' decode failed: ' + res.error);
            post({ type: 'radarFrameReady', index: res.index, hasData: false });
            return;
        }
        frames[res.index] = {
            positions: res.positions, colors: res.colors, count: res.count || 0,
            velPositions: res.velPositions, velColors: res.velColors, velCount: res.velCount || 0,
            ccPositions: res.ccPositions, ccColors: res.ccColors, ccCount: res.ccCount || 0,
            // Inspector value grids (keyed by product name) — see setInspect / lookupValue below.
            grids: { reflectivity: res.reflGrid || null, velocity: res.velGrid || null, cc: res.ccGrid || null },
            velNyq: res.velNyq || 0, // Nyquist (m/s) — lets the inspector show the raw fold of a dealiased gate
        };
        // Post the per-frame decode metrics as a STRUCTURED message (the C# RadarDiagnostics
        // service records them, evaluates the suspect heuristics, and quarantines a bad frame's
        // .V06). The metrics are already computed by the decoder; we just forward them losslessly.
        post({
            type: 'radarFrame', index: res.index, empty: !!res.empty,
            tris: res.count || 0, velTris: res.velCount || 0,
            decodeMs: res.decodeMs, buildMs: res.buildMs, bytes: res.bytes,
            elevList: res.elevList, velElev: res.velElev, velNyq: res.velNyq,
            reflStats: res.reflStats, velStats: res.velStats, dealias: res.dealias,
            decoded: frames.filter(Boolean).length, total: frames.length, cf: currentFrame,
        });
        try { console.log('[radar] decoded idx=' + res.index + (res.empty ? ' EMPTY' : ' tris=' + res.count + ' velTris=' + (res.velCount || 0))); } catch (e) { /* ignore */ }

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
                // Pick the geometry for the active product (reflectivity / velocity / correlation).
                let pos, col, cnt;
                if (product === 'velocity') { pos = f.velPositions; col = f.velColors; cnt = f.velCount; }
                else if (product === 'cc') { pos = f.ccPositions; col = f.ccColors; cnt = f.ccCount; }
                else { pos = f.positions; col = f.colors; cnt = f.count; }
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

    // Beneath the boundary lines (so borders draw over radar), the outlook, and the labels.
    function beforeId(map) {
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
    // only rebuild when it actually changes (or a layer is missing, e.g. after a re-add). Also
    // ensures the sweep layer once the range is known (the host may have started the sweep before
    // the first frame decoded, i.e. before there was a radius to sweep).
    function setRangeRing(map, rangeMeters) {
        if (!map || !(rangeMeters > 0)) return;
        if (Math.abs(rangeMeters - currentRangeMeters) < 500 && map.getLayer(RANGE_LAYER)) {
            if (sweepPeriodMs > 0) ensureSweepLayer(map); // range unchanged, but sweep may need its layer
            return;
        }
        currentRangeMeters = rangeMeters;
        addRangeRing(map);
        if (sweepPeriodMs > 0) ensureSweepLayer(map);
    }

    // ---- Sweep arm ----
    // A line from the site centre to the range-ring edge at the current bearing (0 = due north),
    // using the same metres-per-degree approximation as the ring/gates so it tracks the circle.
    function sweepArmGeoJSON(angleRad) {
        const tip = Geo.siteToLngLat(siteLat, siteLon, currentRangeMeters, angleRad);
        return { type: 'Feature', geometry: { type: 'LineString', coordinates: [[siteLon, siteLat], tip] } };
    }
    function ensureSweepLayer(map) {
        if (!map || !(currentRangeMeters > 0) || !Geo) return; // geo.js not loaded yet
        if (!map.getSource(SWEEP_SRC)) map.addSource(SWEEP_SRC, { type: 'geojson', data: sweepArmGeoJSON(0) });
        if (!map.getLayer(SWEEP_LAYER)) {
            // Added after the range ring → sits on top of the ring + radar fill.
            map.addLayer({
                id: SWEEP_LAYER, type: 'line', source: SWEEP_SRC,
                paint: { 'line-color': '#ffe8a8', 'line-width': 1.6, 'line-opacity': 0.8, 'line-blur': 0.4 },
            }, beforeId(map));
        }
    }
    function removeSweepLayer(map) {
        if (map && map.getLayer(SWEEP_LAYER)) map.removeLayer(SWEEP_LAYER);
        if (map && map.getSource(SWEEP_SRC)) map.removeSource(SWEEP_SRC);
    }
    function sweepFrame() {
        if (sweepPeriodMs <= 0) { sweepRaf = 0; return; } // disabled — stop the loop
        // Keep the loop alive while active; just skip drawing until the range + layer exist (the
        // host can start the sweep before the first frame decodes, i.e. before there's a radius).
        if (currentMap && currentRangeMeters > 0 && Geo) {
            const frac = ((performance.now() - sweepT0) % sweepPeriodMs) / sweepPeriodMs;
            const src = currentMap.getSource(SWEEP_SRC);
            if (src) src.setData(sweepArmGeoJSON(frac * 2 * Math.PI));
        }
        sweepRaf = requestAnimationFrame(sweepFrame);
    }

    // Decodes one volume into frames[index] (off-thread, with a main-thread fallback).
    function decodeFrame(url, index) {
        const myToken = loopToken;
        fetch(url, { cache: 'no-store' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.arrayBuffer();
        }).then(function (ab) {
            if (myToken !== loopToken) return;
            const w = getWorker();
            if (w) {
                w.postMessage({ ab: ab, siteLat: siteLat, siteLon: siteLon, minDbz: MIN_DBZ, token: myToken, index: index }, [ab]);
            } else {
                import('./radar-decode.js').then(function (m) {
                    return m.decodeAndBuild(ab, siteLat, siteLon, MIN_DBZ);
                }).then(function (r2) {
                    applyFrameResult(frameResultFrom(r2, myToken, index));
                }).catch(function (err) {
                    applyFrameResult({ token: myToken, index: index, error: String(err && err.message ? err.message : err) });
                });
            }
        }).catch(function (err) {
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
        if (!grid || !Geo) return null;
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
            const main = r.value.toFixed(r.digits) + (r.unit ? ' ' + r.unit : '');
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
                removeRangeRing(map); removeSweepLayer(map); currentRangeMeters = 0;
            }
            siteLat = lat; siteLon = lon;
            loopToken++;            // invalidate any in-flight frames from a previous loop
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
            hostLog('remap newCount=' + newCount + ' cf=' + currentFrame + ' reused=' + frames.filter(Boolean).length + ' token=' + loopToken);
        },
        clear: function (map) {
            loopToken++;
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            removeLayer(map);
            removeRangeRing(map);
            removeSweepLayer(map);
            currentRangeMeters = 0;
            sweepPeriodMs = 0;
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
            const myToken = loopToken;
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            uploadedFrame = -1;
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayer(map);
            removeRangeRing(map);
            removeSweepLayer(map);
            currentRangeMeters = 0;
            sweepPeriodMs = 0; // a DOW frame is a single sweep — no rotating arm
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
                // (re)adds the layer, and draws the range ring from r2.rangeMeters.
                applyFrameResult(frameResultFrom(r2, myToken, 0));
            }).catch(function (err) {
                hostLog('showDow failed: ' + (err && err.message ? err.message : err));
            });
        },
        setOpacity: function (map, op) {
            opacity = op;
            if (map.getLayer(LAYER_ID)) map.triggerRepaint();
        },
        // Phase-lock the on-map sweep arm to the live-poll cycle. The host calls this at each cycle
        // start with the seconds until the next poll; one revolution then spans that interval, so
        // the arm completes as new data is due. period <= 0 stops + removes the sweep.
        setSweep: function (map, periodSeconds) {
            currentMap = map;
            const p = Number(periodSeconds);
            if (!(p > 0)) { sweepPeriodMs = 0; removeSweepLayer(map); return; }
            sweepPeriodMs = p * 1000;
            sweepT0 = performance.now();        // this cycle starts now
            ensureSweepLayer(map);              // no-op until the range is known (first frame)
            if (!sweepRaf) sweepRaf = requestAnimationFrame(sweepFrame);
        },
        // Switch rendered moment ('reflectivity' | 'velocity' | 'cc'). All geometries are already
        // decoded per frame, so this just re-uploads + repaints — no re-fetch/re-decode.
        setProduct: function (map, p) {
            if ((p !== 'reflectivity' && p !== 'velocity' && p !== 'cc') || p === product) return;
            product = p;
            uploadedFrame = -1; // force the new product's geometry to upload on the next render
            hostLog('product=' + product);
            if (map && map.getLayer(LAYER_ID)) map.triggerRepaint();
        },
        // Re-add after a basemap switch (setStyle drops custom layers + sources); frames, the range
        // ring, and the sweep are retained, so restore them. The sweep's rAF keeps running and
        // resumes drawing once its layer is back.
        reAdd: function (map) {
            currentMap = map;
            if (currentFrame >= 0) addLayer(map);
            addRangeRing(map);
            if (sweepPeriodMs > 0) ensureSweepLayer(map);
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
