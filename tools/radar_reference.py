#!/usr/bin/env python3
"""
radar_reference.py - independent GROUND-TRUTH radar images, for verifying the app's decoder.

It downloads the SAME NEXRAD Level II volume the app uses (the public AWS
`unidata-nexrad-level2` bucket) and renders the lowest tilt's reflectivity + (dealiased) velocity
to PNG images using Py-ART -- a separate, scientific-grade radar library. Py-ART is an INDEPENDENT
implementation (not derived from this app), and it reads BOTH the modern Message-31 format AND the
legacy Message-1 format (pre-2008). So you can:
  * verify a hand-written legacy decoder is byte-correct (load the same event in the app, compare),
  * sanity-check velocity DEALIASING against Py-ART's own dealiaser (absolute ground truth).

Setup (one-time):
    python -m pip install arm_pyart matplotlib

Usage (TIME IS UTC -- the app's "Selected Site" card shows UTC scan time, so they line up):
    python radar_reference.py SITE YYYY-MM-DD HH:MM
  examples:
    python radar_reference.py KTLX 2013-05-20 20:10     # Moore EF5 (modern, _V06)
    python radar_reference.py KTLX 2011-05-24 22:00     # El Reno-Piedmont (older _V03)
    python radar_reference.py KTLX 1999-05-03 23:30     # Bridge Creek-Moore F5 (legacy Message-1)

Output (in the current folder):
    <SITE>_<yyyymmdd>_<hhmm>_refl.png
    <SITE>_<yyyymmdd>_<hhmm>_vel.png
"""

import sys
import os
import gzip
import tempfile
import datetime
import urllib.request
import xml.etree.ElementTree as ET

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3NS = "{http://s3.amazonaws.com/doc/2006-03-01/}"
RANGE_KM = 150  # half-window of the plotted view (edit if a storm sits farther from the radar)


def list_keys(site, day):
    """Every object key for SITE on the given UTC date (one listing; a day is < 1000 keys)."""
    prefix = "%04d/%02d/%02d/%s/" % (day.year, day.month, day.day, site)
    url = "%s?list-type=2&max-keys=1000&prefix=%s" % (BUCKET, prefix)
    with urllib.request.urlopen(url, timeout=60) as resp:
        root = ET.fromstring(resp.read())
    keys = []
    for el in root.iter(S3NS + "Key"):
        name = el.text
        base = name[:-3] if name.endswith(".gz") else name
        if base.endswith("_MDM"):
            continue  # skip metadata sidecars
        keys.append(name)
    return keys


def key_time(key):
    """UTC scan time parsed from a key like .../KTLX19990503_233012_V03.gz (or no suffix)."""
    fn = key.rsplit("/", 1)[-1]
    stamp = fn[4:19]  # skip the 4-char ICAO -> "yyyymmdd_HHMMSS"
    return datetime.datetime.strptime(stamp, "%Y%m%d_%H%M%S")


def nearest_key(keys, target):
    best, best_delta = None, None
    for k in keys:
        try:
            t = key_time(k)
        except ValueError:
            continue
        delta = abs((t - target).total_seconds())
        if best_delta is None or delta < best_delta:
            best, best_delta = k, delta
    return best


def download(key):
    """Download a volume; gunzip the gzip-wrapped historical files (recent ones are raw)."""
    with urllib.request.urlopen(BUCKET + key, timeout=180) as resp:
        data = resp.read()
    if key.endswith(".gz"):
        data = gzip.decompress(data)
    return data


def lowest_velocity_sweep(radar):
    """Index of the lowest sweep that actually carries velocity (the Doppler cut of a split VCP)."""
    import numpy as np
    if "velocity" not in radar.fields:
        return None
    vd = radar.fields["velocity"]["data"]
    for s in range(radar.nsweeps):
        if np.ma.count(vd[radar.get_slice(s)]) > 0:
            return s
    return None


def main():
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(1)

    site = sys.argv[1].upper()
    try:
        day = datetime.datetime.strptime(sys.argv[2], "%Y-%m-%d").date()
        hh, mm = (int(x) for x in sys.argv[3].split(":"))
        target = datetime.datetime(day.year, day.month, day.day, hh, mm)
    except ValueError:
        print("Date/time must be 'YYYY-MM-DD HH:MM' (UTC). See --help.")
        sys.exit(1)

    print("Listing %s volumes for %s (UTC) ..." % (site, day))
    keys = list_keys(site, day)
    if not keys:
        print("No volumes found for that site/date (pre-archive, or a data gap that day).")
        sys.exit(1)

    key = nearest_key(keys, target)
    actual = key_time(key)
    print("Nearest volume: %s  (scan time %s UTC)" % (key.rsplit("/", 1)[-1], actual.strftime("%H:%M:%S")))

    try:
        import numpy as np
        import pyart
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError as e:
        print("\nMissing a Python package (%s)." % e)
        print("Install the dependencies first:\n    python -m pip install arm_pyart matplotlib")
        sys.exit(1)

    # Py-ART registers its NWS colormaps with matplotlib; newer versions dropped the "pyart_" prefix.
    # Pick whichever name this install actually has.
    def cmap_name(*candidates):
        available = set(plt.colormaps())
        for name in candidates:
            if name in available:
                return name
        return candidates[0]

    global NWS_REF_CMAP, NWS_VEL_CMAP
    NWS_REF_CMAP = cmap_name("NWSRef", "pyart_NWSRef")
    NWS_VEL_CMAP = cmap_name("NWSVel", "pyart_NWSVel")

    print("Downloading + decoding with Py-ART ...")
    data = download(key)
    tmp = tempfile.NamedTemporaryFile(suffix=".ar2v", delete=False)
    try:
        tmp.write(data)
        tmp.close()
        radar = pyart.io.read_nexrad_archive(tmp.name)
    finally:
        os.unlink(tmp.name)

    stamp = "%s_%s" % (site, actual.strftime("%Y%m%d_%H%M"))
    disp = pyart.graph.RadarDisplay(radar)

    # --- Reflectivity (lowest sweep) ---
    fig = plt.figure(figsize=(8, 8))
    disp.plot("reflectivity", 0, vmin=-32, vmax=80, cmap=NWS_REF_CMAP,
              title="%s  reflectivity 0.5deg  %s UTC  (Py-ART)" % (site, actual.strftime("%Y-%m-%d %H:%M")))
    disp.plot_range_rings([50, 100, 150, 200])
    disp.set_limits(xlim=(-RANGE_KM, RANGE_KM), ylim=(-RANGE_KM, RANGE_KM))
    fig.savefig(stamp + "_refl.png", dpi=110, bbox_inches="tight")
    plt.close(fig)
    print("  wrote %s_refl.png" % stamp)

    # --- Velocity (dealiased), the sweep that carries velocity ---
    vs = lowest_velocity_sweep(radar)
    if vs is None:
        print("  (no velocity in this volume - skipping the velocity image)")
    else:
        field, label = "velocity", "velocity RAW"
        try:
            corr = pyart.correct.dealias_region_based(radar)
            radar.add_field("corrected_velocity", corr, replace_existing=True)
            field, label = "corrected_velocity", "velocity DEALIASED"
        except Exception as e:
            print("  (dealias failed: %s - plotting RAW velocity instead)" % e)
        try:
            nyq = float(radar.instrument_parameters["nyquist_velocity"]["data"][radar.get_slice(vs)][0])
            vmax = max(40.0, nyq * 2.0)
        except Exception:
            vmax = 50.0
        fig = plt.figure(figsize=(8, 8))
        disp.plot(field, vs, vmin=-vmax, vmax=vmax, cmap=NWS_VEL_CMAP,
                  title="%s  %s 0.5deg  %s UTC  (Py-ART)" % (site, label, actual.strftime("%Y-%m-%d %H:%M")))
        disp.plot_range_rings([50, 100, 150, 200])
        disp.set_limits(xlim=(-RANGE_KM, RANGE_KM), ylim=(-RANGE_KM, RANGE_KM))
        fig.savefig(stamp + "_vel.png", dpi=110, bbox_inches="tight")
        plt.close(fig)
        print("  wrote %s_vel.png" % stamp)

    print("\nDone. Open the PNG(s) and compare against the app at the SAME scan time")
    print("(%s UTC). Compare STRUCTURE + values, not exact colors -- the color tables differ." % actual.strftime("%H:%M:%S"))


if __name__ == "__main__":
    main()
