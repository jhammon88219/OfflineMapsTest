#!/usr/bin/env python3
"""
dow_import.py - convert a mobile-radar sweep (Doppler on Wheels / DOW, COW, NOXP, ...) into the
app's normalized ".dow.json" frame, which the in-app DOW Event Viewer renders.

WHY THIS EXISTS (and why it's OFFLINE-only):
  The app decodes NEXRAD Level II in JavaScript inside the WebView and ships fully offline -- it has
  no Python at runtime. DOW data, by contrast, comes as DORADE sweep files or CfRadial NetCDF, which
  no in-app decoder reads. Rather than bolt a fragile DORADE/HDF5 parser into the shipped app, we
  treat DOW conversion as OFFLINE DATA CURATION -- exactly like the script that generated
  `radar-sites.json`. You run this once per event; it emits a small ".dow.json" the app reads with a
  trivial pure-JS reader. So: Py-ART does the heavy, format-specific decode here, the app stays
  single-language (JS) and offline, and a curated DOW event corpus gets baked in.

  Velocity is PRE-DEALIASED here with Py-ART (the scientific gold standard). The app's own dealiaser
  is tuned for S-band WSR-88D VAD and would not behave on close-range X-band DOW velocity, so we
  resolve folds offline and the app just colors the result.

Setup (one-time):
    python -m pip install arm_pyart numpy

Usage:
    python dow_import.py INPUT_SWEEP [-o OUTPUT.dow.json] [options]
  examples:
    # lowest sweep, reflectivity + (dealiased) velocity, auto field detection:
    python dow_import.py cfrad.20090605_220113_DOW7.nc --site DOW7 \
        --event "Goshen County, WY 2009-06-05"
    # a DORADE sweep, pick the sweep nearest 1.0 deg elevation, no dealias:
    python dow_import.py swp.1090605220113.DOW7v274.0.8_SUR_v274 --elevation 1.0 --no-dealias

  INPUT may be DORADE (swp.* / *.dor) or CfRadial NetCDF (*.nc) -- Py-ART auto-detects.

Output: a single ".dow.json" frame (default <input>.dow.json). See FORMAT below.

FORMAT ("dow-frame/1") -- a flat, self-describing JSON object:
    {
      "format": "dow-frame/1",
      "site": "DOW7", "event": "...", "timeIso": "2009-06-05T22:01:13Z",
      "lat": 41.70, "lon": -104.53, "altM": 1497.0,   # TRUCK position for this deployment
      "elevationDeg": 0.80,                            # the swept elevation angle
      "band": "X", "nyquistMps": 16.0,                 # band + Nyquist (for the fold readout)
      "nRadials": 360, "nodata": -32768,               # Int16 sentinel for "no data"
      "azimuth": [ ... ],                              # length nRadials, TRUE-north degrees
      "moments": {
        "reflectivity": {"unit":"dBZ","firstGateKm":0.05,"gateSizeKm":0.075,
                         "nGates":1000,"scale":10,"values":[ ... ]},  # Int16, row-major [radial][gate]
        "velocity":     {"unit":"m/s", ... , "dealiased":true, ...}
      }
    }
  Each moment's `values` is length nRadials*nGates, row-major; dequantize as v = q / scale, with
  q == nodata meaning "no data". This maps 1:1 onto the app's existing buildGates/buildGrid input
  ({moment_data, first_gate(km), gate_size(km)} per radial + a true azimuth), so the WebGL render,
  inspector, and color-scale legend are all reused unchanged.
"""

import sys
import os
import json
import argparse
import datetime

# Quantization scale per moment (value * scale -> Int16) and the Py-ART field names we'll accept for
# each. DOW DORADE files often keep native short names (DBZ/VEL/ZDR/...), so we try several.
MOMENTS = [
    # our name        scale  unit    candidate Py-ART / native field names (first match wins)
    ("reflectivity",  10,   "dBZ",  ["reflectivity", "DBZ", "DBZHC", "DZ", "REF", "DBZH"]),
    ("velocity",      10,   "m/s",  ["velocity", "VEL", "VE", "VR", "corrected_velocity"]),
    ("spectrum_width", 10,  "m/s",  ["spectrum_width", "WIDTH", "SW", "SPW"]),
    ("zdr",           100,  "dB",   ["differential_reflectivity", "ZDR", "DR"]),
    ("rho",          1000,  "",     ["cross_correlation_ratio", "RHOHV", "RHO", "RH"]),
]
NODATA = -32768


def resolve_field(radar, candidates):
    """First field present in the radar matching a candidate name (case-insensitive)."""
    have = {name.lower(): name for name in radar.fields}
    for cand in candidates:
        if cand.lower() in have:
            return have[cand.lower()]
    return None


def quantize(masked_2d, scale, qc_keep=None):
    """Masked 2-D field (radials x gates) -> flat list of Int16 with NODATA for masked/invalid.
    qc_keep (optional bool array, same shape) force-nulls gates where it's False (quality mask)."""
    import numpy as np
    arr = np.ma.asarray(masked_2d, dtype="float64")
    q = np.rint(np.ma.filled(arr, np.nan) * scale)
    q = np.where(np.isfinite(q), q, NODATA)
    q = np.clip(q, NODATA + 1, 32767)            # keep NODATA reserved for "no data"
    q = np.where(np.ma.getmaskarray(arr), NODATA, q)
    if qc_keep is not None:
        q = np.where(qc_keep, q, NODATA)
    return q.astype("int16").reshape(-1).tolist()


def pick_sweep(radar, target_elev, sweep_index):
    """Choose a sweep index: explicit --sweep, else nearest --elevation, else lowest with reflectivity."""
    import numpy as np
    if sweep_index is not None:
        if not (0 <= sweep_index < radar.nsweeps):
            raise SystemExit("--sweep %d out of range (file has %d sweeps)" % (sweep_index, radar.nsweeps))
        return sweep_index
    fixed = np.asarray(radar.fixed_angle["data"], dtype="float64")
    if target_elev is not None:
        return int(np.argmin(np.abs(fixed - target_elev)))
    # Default: the lowest-elevation sweep that actually carries reflectivity.
    order = np.argsort(fixed)
    refl = resolve_field(radar, MOMENTS[0][3])
    if refl is not None:
        rd = radar.fields[refl]["data"]
        for s in order:
            if np.ma.count(rd[radar.get_slice(int(s))]) > 0:
                return int(s)
    return int(order[0])


def main():
    ap = argparse.ArgumentParser(description="Convert a DOW/mobile-radar sweep to a .dow.json frame.")
    ap.add_argument("input", help="DORADE (swp.* / *.dor) or CfRadial (*.nc) sweep file")
    ap.add_argument("-o", "--output", help="output path (default <input>.dow.json)")
    ap.add_argument("--site", default=None, help='radar name, e.g. "DOW7" (default: from file)')
    ap.add_argument("--event", default="", help='event label, e.g. "Goshen County, WY 2009-06-05"')
    ap.add_argument("--band", default="X", help="radar band X/C/S (default X for DOW)")
    ap.add_argument("--elevation", type=float, default=None, help="target elevation deg (default: lowest)")
    ap.add_argument("--sweep", type=int, default=None, help="explicit sweep index (overrides --elevation)")
    ap.add_argument("--no-dealias", action="store_true", help="emit RAW velocity (skip Py-ART dealias)")
    ap.add_argument("--ncp-min", type=float, default=0.2,
                    help="drop gates whose Normalized Coherent Power is below this (clutter/noise "
                         "filter; cleans the velocity speckle + near-truck clutter). 0 = off. Default 0.2.")
    ap.add_argument("--snr-min", type=float, default=None,
                    help="also drop gates whose SNR (dB) is below this (off by default).")
    args = ap.parse_args()

    try:
        import numpy as np
        import pyart
    except ImportError as e:
        print("\nMissing a Python package (%s)." % e)
        print("Install the dependencies first:\n    python -m pip install arm_pyart numpy")
        sys.exit(1)

    if not os.path.exists(args.input):
        raise SystemExit("Input file not found: %s" % args.input)

    print("Reading %s with Py-ART ..." % os.path.basename(args.input))
    radar = pyart.io.read(args.input)  # auto-detects CfRadial / Sigmet / NEXRAD / ... (NOT DORADE)
    print("  fields present: %s" % ", ".join(radar.fields.keys()))

    sweep = pick_sweep(radar, args.elevation, args.sweep)
    sl = radar.get_slice(sweep)
    elev_deg = float(np.mean(radar.elevation["data"][sl]))
    az = np.asarray(radar.azimuth["data"][sl], dtype="float64")  # TRUE-north degrees
    n_radials = int(az.shape[0])

    rng_m = np.asarray(radar.range["data"], dtype="float64")     # gate-center ranges (m)
    if rng_m.size < 2:
        raise SystemExit("Sweep has too few range gates to render.")
    first_gate_km = float(rng_m[0]) / 1000.0
    gate_size_km = float(rng_m[1] - rng_m[0]) / 1000.0
    n_gates = int(rng_m.size)

    lat = float(np.asarray(radar.latitude["data"]).ravel()[0])
    lon = float(np.asarray(radar.longitude["data"]).ravel()[0])
    try:
        alt_m = float(np.asarray(radar.altitude["data"]).ravel()[0])
    except Exception:
        alt_m = None

    # Nyquist (m/s) for the velocity fold readout / sanity.
    nyquist = None
    try:
        nyq = radar.instrument_parameters["nyquist_velocity"]["data"][sl]
        nyquist = float(np.asarray(nyq).ravel()[0])
    except Exception:
        pass

    # Quality mask: drop incoherent (clutter/noise) gates using NCP and/or SNR. This is what cleans
    # the velocity speckle (velocity has no dBZ threshold, so every gate — including pure noise — gets
    # colored) and the near-truck ground clutter. Applied identically to every output moment (they share
    # the sweep's gate geometry). Resolved from whatever the file calls these fields; absent → no mask.
    qc_keep = None

    def field_slice(cands):
        f = resolve_field(radar, cands)
        return None if f is None else np.ma.asarray(radar.fields[f]["data"][sl], dtype="float64")

    ncp = field_slice(["NCP", "normalized_coherent_power", "SQI"]) if args.ncp_min > 0 else None
    snr = field_slice(["SNRHC", "SNR", "signal_to_noise_ratio"]) if args.snr_min is not None else None
    if ncp is not None or snr is not None:
        keep = np.ones((n_radials, n_gates), dtype=bool)
        if ncp is not None:
            keep &= (np.ma.filled(ncp, -1.0) >= args.ncp_min)
        if snr is not None:
            keep &= (np.ma.filled(snr, -1e9) >= args.snr_min)
        qc_keep = keep
        print("  quality mask: NCP>=%s, SNR>=%s -> keeping %.0f%% of gates"
              % (args.ncp_min if ncp is not None else "off",
                 args.snr_min if snr is not None else "off",
                 100.0 * keep.sum() / keep.size))
    elif args.ncp_min > 0:
        print("  quality mask: no NCP/SNR field found in this file -> not masking")

    # Velocity: pre-dealias with Py-ART (region-based) unless --no-dealias. We add the corrected field
    # back so the normal field-resolution path below picks it up as "velocity".
    vel_dealiased = False
    vel_native = resolve_field(radar, MOMENTS[1][3])
    if vel_native is not None and not args.no_dealias:
        try:
            print("  dealiasing velocity (%d radials x %d gates, Py-ART region-based — this can take "
                  "up to a minute; use --no-dealias to skip)..." % (n_radials, n_gates), flush=True)
            corr = pyart.correct.dealias_region_based(radar, vel_field=vel_native)
            radar.add_field("velocity", corr, replace_existing=True)
            vel_dealiased = True
            print("  velocity dealiased (Py-ART region-based)")
        except Exception as e:
            print("  velocity dealias FAILED (%s) -- emitting RAW velocity" % e)

    moments = {}
    for name, scale, unit, candidates in MOMENTS:
        field = resolve_field(radar, candidates)
        if field is None:
            continue
        data = radar.fields[field]["data"][sl]  # masked (radials x gates)
        if int(np.ma.count(data)) == 0:
            continue  # nothing valid in this sweep for this moment
        entry = {
            "unit": unit,
            "firstGateKm": first_gate_km,
            "gateSizeKm": gate_size_km,
            "nGates": n_gates,
            "scale": scale,
            "values": quantize(data, scale, qc_keep),
        }
        if name == "velocity":
            entry["dealiased"] = vel_dealiased
        moments[name] = entry
        print("  + %-13s from '%s'  (%d gates)" % (name, field, n_gates))

    if not moments:
        raise SystemExit(
            "No usable moments found in this sweep. Field names present were:\n    %s\n"
            "Add the relevant ones to the MOMENTS table near the top of this script (or send them "
            "along)." % ", ".join(radar.fields.keys()))

    # Scan time (best-effort from the file's time coverage start).
    time_iso = None
    try:
        t0 = pyart.util.datetime_from_radar(radar)  # volume start as a datetime
        time_iso = t0.replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")
    except Exception:
        time_iso = datetime.datetime.utcnow().replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")

    site = args.site
    if not site:
        site = str(radar.metadata.get("instrument_name", "DOW")).strip() or "DOW"

    frame = {
        "format": "dow-frame/1",
        "site": site,
        "event": args.event,
        "timeIso": time_iso,
        "lat": lat,
        "lon": lon,
        "altM": alt_m,
        "elevationDeg": round(elev_deg, 3),
        "band": args.band,
        "nyquistMps": nyquist,
        "nRadials": n_radials,
        "nodata": NODATA,
        "azimuth": [round(a, 3) for a in az.tolist()],
        "moments": moments,
    }

    out = args.output or (os.path.splitext(args.input)[0] + ".dow.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(frame, f, separators=(",", ":"))
    size_mb = os.path.getsize(out) / 1024.0 / 1024.0
    print("\nWrote %s  (%.1f MB)" % (out, size_mb))
    print("  site=%s  elev=%.2f deg  radials=%d  gates=%d  moments=%s"
          % (site, elev_deg, n_radials, n_gates, ",".join(moments.keys())))
    print("  truck lat=%.4f lon=%.4f%s" % (lat, lon, ("" if alt_m is None else "  alt=%.0f m" % alt_m)))
    print("\nNext: drop this file into the app's bundled DOW events (or load it) and view it.")


if __name__ == "__main__":
    main()
