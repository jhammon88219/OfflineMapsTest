# Radar reference generator (`radar_reference.py`)

A dev tool that produces **independent, ground-truth radar images** to check the app's decoder
against. It downloads the *same* NEXRAD Level II volume the app uses (the public AWS
`unidata-nexrad-level2` bucket) and renders the lowest tilt's **reflectivity** and **dealiased
velocity** to PNGs using **Py-ART** — a separate, scientific-grade radar library used by researchers.

**Why it exists:** Py-ART is an *independent* implementation (nothing to do with this app's code) and
it reads **both** the modern Message-31 format **and** the legacy Message-1 format (pre-2008). So it's
the trustworthy "answer key" for:

- **Verifying a legacy (pre-2008) decoder is byte-correct** — you can't eyeball that; you compare to
  this reference.
- **Sanity-checking velocity dealiasing** against Py-ART's own dealiaser (true ground truth, not just
  self-consistency).

This is a standalone Python script — it does **not** touch or depend on the C# app.

---

## One-time setup (Windows)

1. **Install Python** from <https://www.python.org/downloads/>. During install, **check the box
   "Add Python to PATH"** (important).

2. **Open a new terminal** (PowerShell or Command Prompt) and confirm it's there:
   ```
   python --version
   ```
   You should see something like `Python 3.12.x`. (If "not recognized", reopen the terminal, or
   re-run the installer and tick "Add to PATH".)

3. **Install the two packages** (this pulls in NumPy/SciPy/Matplotlib automatically; takes a few
   minutes the first time):
   ```
   python -m pip install arm_pyart matplotlib
   ```
   (or, from this folder: `python -m pip install -r requirements.txt`)

That's it — no accounts, no API keys.

---

## Using it

From this `tools/` folder, run (the **time is UTC** — the app's "Selected Site" card shows the scan
time in UTC, so they line up):

```
python radar_reference.py SITE YYYY-MM-DD HH:MM
```

Examples:
```
python radar_reference.py KTLX 2013-05-20 20:10     # Moore EF5 (modern _V06)
python radar_reference.py KTLX 2011-05-24 22:00     # El Reno-Piedmont (older _V03)
python radar_reference.py KTLX 1999-05-03 23:30     # Bridge Creek-Moore F5 (legacy Message-1)
```

It finds the volume nearest your time, prints the exact scan time it used, and writes:
```
KTLX_20130520_2010_refl.png
KTLX_20130520_2010_vel.png
```
in the current folder.

> Local vs UTC: weather events are usually quoted in local time. Oklahoma in summer is CDT
> (UTC − 5), so e.g. the Moore tornado "~3:01 PM CDT" = **20:01 UTC**. Just add 5 hours to a CDT
> time to get the UTC value to pass here.

---

## How to compare against the app

1. Load the **same site + scan time** in the app's Past Event Viewer (match the UTC scan time the
   script printed — the Selected Site card shows it).
2. Put the app window and the reference PNG side by side.
3. **Compare STRUCTURE and VALUES, not exact colors** — Py-ART and the app use different color
   tables. Check:
   - reflectivity: same storm shape, hook, and dBZ values (use the app's Inspect tool);
   - velocity: same couplet location, the inbound/outbound on the same sides, and the same
     dealiased speeds (Inspect again). If a region disagrees, that's a real bug to chase.

The reference plots are centered on the radar in km (range rings at 50/100/150/200 km) — for a
storm farther out, bump `RANGE_KM` near the top of `radar_reference.py`.

---

## Troubleshooting

- **"No volumes found"** — that date is before the archive starts for that site, or a data gap that
  day. Try a nearby day, or a different (closer) radar.
- **A legacy file errors out** — note it; Py-ART reads most Message-1 data, but the oldest
  (early-1990s) files occasionally have quirks.
- **It's slow** — Py-ART's dealiasing on a big volume takes a few seconds; that's normal.

---

# Dealiasing regression check (`dealias_check.py`)

A dev tool that **regression-checks the app's velocity dealiasing against Py-ART** without needing a
JavaScript runtime. The app's dealiaser (`radar-decode.js` `dealiasSweep`, "v2") is a port of Py-ART's
region-based algorithm; this script **re-implements that same algorithm in Python** (a faithful mirror
of the JS, no scipy/Cython) and, on the *same* AWS volume the app uses, diffs it against Py-ART's own
`dealias_region_based` — the scientific answer key.

**Why it exists:** the dev box has no Node/deno/bun, so the JS can't be executed here directly. When
you change `dealiasSweep`, mirror the edit in `dealias_v2()` in this script and re-run — if the couplet
core stays within a few mph of Py-ART and agreement stays > ~99%, the change is safe to ship. It's how
the v2 rewrite (which fixed violent-couplet unfolding — v1 capped the couplet near Nyquist) was
validated before it went into the app.

## Setup

```
python -m pip install arm_pyart      (same as radar_reference.py — see above)
```

## Using it

```
python dealias_check.py SITE YYYY MM DD HH MM        # time is UTC
```

Examples (known couplets):
```
python dealias_check.py KTLX 2013 05 20 20 12    # Moore EF5     -> Py-ART core ~ -135 mph
python dealias_check.py KTLX 1999 05 03 23 46    # Bridge Creek F5 (legacy) -> core ~ -100 mph
```

It prints the Nyquist, the dealiased range from the mirror vs Py-ART, the whole-sweep agreement
(fraction of gates that chose the same fold), and — the number that matters for a violent tornado —
the **couplet core** value from both. A healthy result: couplet core within a few mph of Py-ART,
agreement > ~99%. (`dealias_check.py` `dealias_v2()` must be kept in sync with the JS `dealiasSweep`;
it does **not** import the app.)

---

# DOW importer (`dow_import.py`)

A second, separate tool that converts a **mobile-radar sweep** — Doppler on Wheels (DOW), COW, NOXP,
etc. — into the app's normalized **`.dow.json`** frame, which the in-app **DOW Event Viewer** renders.

**Why it's offline-only (and not part of the shipped app).** The app decodes NEXRAD Level II in
JavaScript and ships fully offline — no Python at runtime. DOW data comes as **DORADE** sweep files
or **CfRadial** NetCDF, which no in-app decoder reads. Instead of bolting a fragile DORADE/HDF5
parser into the app, DOW conversion is **offline data curation** (like the script that built
`radar-sites.json`): you run this once per event, it emits a small `.dow.json`, and the app reads it
with a trivial pure-JS reader. Py-ART does the format-specific decode here; the app stays
single-language and offline.

> **Velocity is pre-dealiased here** with Py-ART (the scientific gold standard). The app's own
> dealiaser is tuned for S-band WSR-88D VAD and wouldn't behave on close-range X-band DOW velocity,
> so folds are resolved offline and the app just colors the result.

## Setup

```
python -m pip install arm_pyart numpy
```

## Where to get DOW data

DOW data is archived by **field campaign**, not as a continuous feed:

- **NSF NCAR / EOL data archive** — <https://data.eol.ucar.edu/> (search a project, e.g. **VORTEX2**).
  Classic open deployments ship as **DORADE** sweep files; some are also offered as **CfRadial**.
- **University of Illinois FARM** — newer datasets (PECAN, PERiLS, …) are often **password/FTP-gated**;
  email the dataset's point of contact for access.

Good first target: **VORTEX2 — Goshen County, WY, 5 June 2009** (the LaGrange tornado), a heavily
sampled, openly archived supercell.

> **Redistribution:** bundling a converted event into the app redistributes someone's research data.
> Use the **openly-licensed** archives and carry the required **citation/acknowledgment** (CSWR/FARM).
> Don't ship the password-gated sets.

## Using it

```
python dow_import.py INPUT_SWEEP [-o OUT.dow.json] [--site DOW7] [--event "..."] \
                     [--elevation DEG | --sweep N] [--band X] [--no-dealias]
```

Examples:
```
# lowest sweep, refl + dealiased velocity, auto field detection:
python dow_import.py cfrad.20090605_220113_DOW7.nc --site DOW7 --event "Goshen County, WY 2009-06-05"

# a DORADE sweep, pick the cut nearest 1.0 deg, keep RAW velocity:
python dow_import.py swp.1090605220113.DOW7v274.0.8_SUR_v274 --elevation 1.0 --no-dealias
```

`INPUT` may be **DORADE** (`swp.*` / `*.dor`) or **CfRadial** (`*.nc`) — Py-ART auto-detects. Output is
`<input>.dow.json` (override with `-o`).

## The `.dow.json` format (`dow-frame/1`)

A flat, self-describing JSON object: the **truck** `lat`/`lon`/`altM` for that deployment, the swept
`elevationDeg`, `band` + `nyquistMps`, a TRUE-north `azimuth[]` (one per radial), and a `moments`
table. Each moment carries `firstGateKm`/`gateSizeKm`/`nGates`/`scale` and a row-major
`values[]` of **Int16** (dequantize as `v = q / scale`; `q == nodata` (-32768) = no data). This maps
1:1 onto the app's existing `buildGates`/`buildGrid` input (`{moment_data, first_gate, gate_size}` per
radial + a true azimuth), so the WebGL render, the Inspect tool, and the color-scale legend are all
reused unchanged — the only new pieces are the per-frame mobile position and the JSON reader.

## Verifying it

Render the same sweep independently with Py-ART for an answer key:
```
python -c "import pyart,matplotlib;matplotlib.use('Agg');import matplotlib.pyplot as plt; \
r=pyart.io.read('INPUT_SWEEP');d=pyart.graph.RadarDisplay(r); \
d.plot('reflectivity',0);plt.savefig('dow_ref.png')"
```
Compare structure/values against the app's DOW Event Viewer, same as the NEXRAD reference above.
