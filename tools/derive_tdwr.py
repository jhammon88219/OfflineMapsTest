"""Derive tdwr-sites.json the same way the NEXRAD/research lists are derived:
  * a site is REAL to us only if it publishes decodable Level II to the AWS bucket, so
    enumerate the T*** ids actually present for a recent day;
  * keep only those the NWS API classifies stationType==TDWR (drops T-prefixed WSR-88Ds
    like TJUA = San Juan);
  * take the ANTENNA coordinates from each site's own volume header (Py-ART) -- the NWS API
    coords are rounded to ~1 km, too coarse for gate projection -- and the friendly name from
    the API.
Writes the JSON array {id,name,lat,lon} sorted by id, plus a summary of anomalies.
"""
import json, urllib.request, tempfile, os, datetime
import xml.etree.ElementTree as ET
import numpy as np
import pyart

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3 = "{http://s3.amazonaws.com/doc/2006-03-01/}"
UA = "Anvil/1.0 (jhammon88219@gmail.com)"
DAY = datetime.date(2026, 7, 8)  # a recent day to enumerate sites with data; bump when regenerating
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Anvil.App", "Assets", "tdwr-sites.json")


def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def bucket_site_ids(day):
    url = "%s?list-type=2&delimiter=/&prefix=%04d/%02d/%02d/" % (BUCKET, day.year, day.month, day.day)
    root = ET.fromstring(get(url))
    ids = []
    for el in root.iter(S3 + "Prefix"):
        parts = el.text.rstrip("/").split("/")
        ids.append(parts[-1])
    return ids


def newest_key(site, day):
    prefix = "%04d/%02d/%02d/%s/" % (day.year, day.month, day.day, site)
    url = "%s?list-type=2&max-keys=1000&prefix=%s" % (BUCKET, prefix)
    root = ET.fromstring(get(url))
    keys = [el.text for el in root.iter(S3 + "Key")
            if not el.text.endswith("_MDM") and el.text.rsplit("/", 1)[-1][:19]]
    keys = [k for k in keys if "_V" in k.rsplit("/", 1)[-1]]
    return keys[-1] if keys else None


# 1) NWS API: all TDWR stations -> id -> name
api = json.loads(get("https://api.weather.gov/radar/stations?stationType=TDWR"))
tdwr_name = {}
for f in api.get("features", []):
    p = f.get("properties", {})
    if p.get("stationType") == "TDWR":
        tdwr_name[p["id"]] = p.get("name") or p["id"]
print("API TDWR stations:", len(tdwr_name))

# 2) bucket T-prefix ids with data today
bucket_ids = [s for s in bucket_site_ids(DAY) if s.startswith("T")]
print("bucket T-prefix ids:", len(bucket_ids))

# 3) intersection = real, decodable TDWRs
targets = sorted(set(bucket_ids) & set(tdwr_name))
skipped = sorted(set(bucket_ids) - set(tdwr_name))
print("TDWRs to import:", len(targets))
print("bucket T-ids NOT classified TDWR by API (skipped):", skipped)

# 4) antenna coords from each volume header
sites, failed = [], []
for sid in targets:
    try:
        key = newest_key(sid, DAY)
        if not key:
            failed.append((sid, "no key")); continue
        data = get(BUCKET + key)
        tmp = tempfile.NamedTemporaryFile(suffix=".ar2v", delete=False)
        tmp.write(data); tmp.close()
        try:
            radar = pyart.io.read_nexrad_archive(tmp.name, delay_field_loading=True)
        finally:
            os.unlink(tmp.name)
        lat = round(float(radar.latitude["data"][0]), 4)
        lon = round(float(radar.longitude["data"][0]), 4)
        sites.append({"id": sid, "name": tdwr_name[sid], "lat": lat, "lon": lon})
        print(f"  {sid}  {tdwr_name[sid]:<22} {lat}, {lon}")
    except Exception as e:
        failed.append((sid, str(e)))
        print(f"  {sid}  FAILED: {e}")

sites.sort(key=lambda s: s["id"])
with open(OUT, "w", encoding="utf-8") as f:
    json.dump(sites, f, indent=4)
    f.write("\n")
print(f"\nwrote {len(sites)} sites -> {OUT}")
if failed:
    print("FAILED:", failed)
