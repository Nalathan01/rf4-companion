import glob
import io
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from collections import deque

import numpy as np
from PIL import Image, ImageOps

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC, CACHE, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
LABEL = re.compile(r"^(基础料|诱.{1,2}添加剂|添加剂|增香剂|香料)")
CJK = re.compile(r"[一-鿿]")


def norm(s):
    return "".join(c for c in s.lower() if c.isalnum())


def lev(a, b):
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(cur[j-1] + 1, prev[j] + 1, prev[j-1] + (ca != cb)))
        prev = cur
    return prev[-1]


def candidates():
    items = json.load(open(os.path.join(ROOT, "data", "itemnames.json"), encoding="utf-8"))
    zi = items["langs"].index("zh")
    names = {}
    for r in items["rows"]:
        if zi < len(r) and CJK.search(r[zi] or ""):
            names.setdefault(norm(r[zi]), r[0])
    game = json.load(open(os.path.join(ROOT, "data", "gamedata.json"), encoding="utf-8"))
    for k, v in game["names"].items():
        if CJK.search(v.get("zh") or ""):
            names.setdefault(norm(v["zh"]), k)
    return names


def find_box(im):
    a = np.asarray(im).max(axis=2)[::4, ::4] < 28
    h, w = a.shape
    seen = np.zeros_like(a)
    best = None
    for y in range(h):
        for x in range(w):
            if not a[y, x] or seen[y, x]:
                continue
            q = deque([(y, x)])
            seen[y, x] = True
            n, y0, y1, x0, x1 = 0, y, y, x, x
            while q:
                cy, cx = q.popleft()
                n += 1
                y0, y1, x0, x1 = min(y0, cy), max(y1, cy), min(x0, cx), max(x1, cx)
                for ny, nx in ((cy-1, cx), (cy+1, cx), (cy, cx-1), (cy, cx+1)):
                    if 0 <= ny < h and 0 <= nx < w and a[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((ny, nx))
            fill = n / ((x1-x0+1) * (y1-y0+1))
            if fill > 0.55 and (x1-x0) > w / 12 and (y1-y0) > h / 8 and (best is None or n > best[0]):
                best = (n, x0 * 4, y0 * 4, (x1+1) * 4, (y1+1) * 4)
    return best


def feed_from_image(data, names):
    im = Image.open(io.BytesIO(data)).convert("RGB")
    box = find_box(im)
    if not box:
        return []
    crop = ImageOps.invert(ImageOps.grayscale(im.crop(box[1:])))
    crop = crop.resize((crop.width * 3, crop.height * 3), Image.LANCZOS)
    crop.save("/tmp/wx_feed.png")
    text = subprocess.run(["tesseract", "/tmp/wx_feed.png", "-", "-l", "chi_sim", "--psm", "6"], capture_output=True, text=True).stdout
    lines = [x.replace(" ", "") for x in text.splitlines()]
    if not any(LABEL.match(x) for x in lines):
        return []
    keys = list(names.keys())
    feed = []
    for line in lines:
        if not LABEL.match(line):
            continue
        name = norm(LABEL.sub("", line))
        if len(name) < 2:
            continue
        scored = sorted((lev(name, k), k) for k in keys if abs(len(k)-len(name)) <= 2)
        if not scored:
            continue
        d, k = scored[0]
        if d > max(1, len(name) // 5):
            continue
        if len(scored) > 1 and scored[1][0] == d and names[scored[1][1]] != names[k]:
            continue
        if names[k] not in feed:
            feed.append(names[k])
    return feed


def main():
    names = candidates()
    cache = {}
    if os.path.exists(CACHE):
        cache = json.load(open(CACHE, encoding="utf-8"))
    out = {}
    done = 0
    for f in sorted(glob.glob(os.path.join(SRC, "api_wechat_positions_*"))):
        for p in json.load(open(f, encoding="utf-8")).get("positions", []):
            pid = p.get("post_id") or ""
            urls = ((p.get("image_urls") or {}).get("config") or [])[:2]
            if not pid or not urls:
                continue
            if pid not in cache:
                feed = []
                for u in urls:
                    try:
                        req = urllib.request.Request(u, headers={"User-Agent": "RF4CompanionMirror/1.0 (+https://github.com/Nalathan01/rf4-companion)"})
                        with urllib.request.urlopen(req, timeout=30) as r:
                            feed = feed_from_image(r.read(), names)
                    except Exception as e:
                        print("Bild", pid, e)
                        feed = None
                        break
                    if feed:
                        break
                    time.sleep(0.3)
                if feed is None:
                    continue
                cache[pid] = feed
                done += 1
            if cache[pid]:
                out[pid] = cache[pid]
    json.dump(cache, open(CACHE, "w", encoding="utf-8"), ensure_ascii=False)
    json.dump(out, open(OUT, "w", encoding="utf-8"), ensure_ascii=False)
    print("Futter aus Bild:", len(out), "Spots, neu gelesen:", done)


main()
