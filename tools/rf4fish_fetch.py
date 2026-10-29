import datetime as dt
import json
import os
import re
import sys
import time
import urllib.request

SITE = "https://rf4.fish"
OUT = sys.argv[1]
KEY = os.environ.get("RF4_MIRROR_KEY", "")


def name(path):
    return re.sub(r"[^A-Za-z0-9]+", "_", path).strip("_")


def get(path):
    headers = {"User-Agent": "RF4CompanionMirror/1.0 (+https://github.com/Nalathan01/rf4-companion)"}
    if KEY:
        headers["X-RF4-Mirror-Key"] = KEY
    with urllib.request.urlopen(urllib.request.Request(SITE + path, headers=headers), timeout=60) as r:
        body = r.read()
    with open(os.path.join(OUT, name(path)), "wb") as fh:
        fh.write(body)
    time.sleep(1)
    return body


os.makedirs(OUT, exist_ok=True)
cutoff = (dt.datetime.utcnow() - dt.timedelta(days=8)).isoformat()
for offset in range(0, 3000, 100):
    page = json.loads(get("/api/wechat/positions?limit=100&offset=%d" % offset))
    rows = page.get("positions", [])
    if len(rows) < 100 or any((p.get("publish_date") or "") < cutoff for p in rows):
        break
for path in ("/api/trophy-weights", "/RodList.csv", "/LinesData.csv", "/LuresData.csv", "/data/reels.csv"):
    get(path)
print("rf4.fish:", len(os.listdir(OUT)), "Dateien")
