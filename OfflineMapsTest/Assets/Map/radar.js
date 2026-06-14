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

    const LAYER_ID = 'level2-radar';
    const MIN_DBZ = 10;

    // frames[index] = { positions, colors, count } for a drawable frame, or { count: 0 } for
    // an empty (no-echo) frame. currentFrame is the index being rendered.
    let frames = [];
    let currentFrame = -1;
    let uploadedFrame = -1; // which frame's geometry is currently in the GL buffers
    let siteLat = 0, siteLon = 0;
    let opacity = 0.85;
    let loopToken = 0;      // bumped per loop so stale async frames are dropped
    let currentMap = null;

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
                worker = new Worker('radar-worker.js');
                worker.onmessage = function (e) { applyFrameResult(e.data); };
                worker.onerror = function (e) { hostLog('worker error: ' + (e && e.message ? e.message : e)); };
            } catch (e) {
                worker = null;
                hostLog('worker unavailable; main-thread decode: ' + (e && e.message ? e.message : e));
            }
        }
        return worker;
    }

    function applyFrameResult(res) {
        if (!res || res.token !== loopToken) return; // stale (loop changed)
        if (res.error) {
            hostLog('frame ' + res.index + ' decode failed: ' + res.error);
            post({ type: 'radarFrameReady', index: res.index, hasData: false });
            return;
        }
        frames[res.index] = res.empty
            ? { count: 0 }
            : { positions: res.positions, colors: res.colors, count: res.count };

        // Show the first frame that arrives (the host pushes newest first).
        if (currentFrame < 0 && currentMap) {
            currentFrame = res.index;
            uploadedFrame = -1;
            addLayer(currentMap);
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
                if (!f) return;
                try {
                    // Upload this frame's geometry only when the current frame changed.
                    if (uploadedFrame !== currentFrame) {
                        if (f.positions) {
                            glc.bindBuffer(glc.ARRAY_BUFFER, posBuf);
                            glc.bufferData(glc.ARRAY_BUFFER, f.positions, glc.STATIC_DRAW);
                            glc.bindBuffer(glc.ARRAY_BUFFER, colorBuf);
                            glc.bufferData(glc.ARRAY_BUFFER, f.colors, glc.STATIC_DRAW);
                        }
                        uploadedFrame = currentFrame;
                    }
                    if (!f.count) return; // empty (no-echo) frame

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
                    glc.drawArrays(glc.TRIANGLES, 0, f.count);
                    glc.disableVertexAttribArray(aPos);
                    glc.disableVertexAttribArray(aColor);
                    glc.bindBuffer(glc.ARRAY_BUFFER, null);
                } catch (e) {
                    hostLog('render error: ' + (e && e.message ? e.message : e));
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
        if (map.getLayer(LAYER_ID)) map.removeLayer(LAYER_ID);
    }
    function addLayer(map) {
        if (currentFrame < 0) return;
        removeLayer(map);
        uploadedFrame = -1;
        map.addLayer(makeCustomLayer(), beforeId(map));
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
                    applyFrameResult({
                        token: myToken, index: index, empty: !r2.geom,
                        positions: r2.geom && r2.geom.positions, colors: r2.geom && r2.geom.colors,
                        count: r2.geom && r2.geom.count,
                    });
                }).catch(function (err) {
                    applyFrameResult({ token: myToken, index: index, error: String(err && err.message ? err.message : err) });
                });
            }
        }).catch(function (err) {
            hostLog('frame ' + index + ' fetch failed: ' + (err && err.message ? err.message : err));
            post({ type: 'radarFrameReady', index: index, hasData: false });
        });
    }

    window.RadarLayer = {
        beginLoop: function (map, lat, lon) {
            currentMap = map;
            siteLat = lat; siteLon = lon;
            loopToken++;            // invalidate any in-flight frames from a previous loop
            frames = [];
            currentFrame = -1;
            uploadedFrame = -1;
            removeLayer(map);
        },
        addFrame: function (map, url, index) {
            currentMap = map;
            decodeFrame(url, index);
        },
        showFrame: function (map, index) {
            currentMap = map;
            if (index === currentFrame) return;
            currentFrame = index;
            if (frames[index]) {
                if (map.getLayer(LAYER_ID)) map.triggerRepaint();
                else addLayer(map);
            }
        },
        clear: function (map) {
            loopToken++;
            frames = [];
            currentFrame = -1;
            removeLayer(map);
        },
        setOpacity: function (map, op) {
            opacity = op;
            if (map.getLayer(LAYER_ID)) map.triggerRepaint();
        },
        // Re-add after a basemap switch (setStyle drops custom layers); frames are retained.
        reAdd: function (map) {
            currentMap = map;
            if (currentFrame >= 0) addLayer(map);
        },
    };
})();
