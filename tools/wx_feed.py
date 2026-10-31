import glob
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.request

import numpy as np
from PIL import Image, ImageOps
from scipy import ndimage

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAX_NEW = 150
VERSION = 2
BUDGET = 600
CJK = re.compile(r"[一-鿿]")
HEAD = re.compile(r"^(安装|钓组)|修改")
SKIP = re.compile(r"[:：+＋]")
LABEL = re.compile(r"^(基.?料|诱.{1,2}添加剂|添加剂|增香剂|香料|(pop-?up)?香.{0,2}成饵)", re.I)
CRUSHED = re.compile(r"[\(（]?碎的[\)）]?")


def norm(s):
    return "".join(c for c in s.lower() if c.isalnum() and not "①" <= c <= "⓿")


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
    feed = {}
    for r in items["rows"]:
        if zi < len(r) and CJK.search(r[zi] or ""):
            feed.setdefault(norm(r[zi]), r[0])
    game = json.load(open(os.path.join(ROOT, "data", "gamedata.json"), encoding="utf-8"))
    for k, v in game["names"].items():
        if CJK.search(v.get("zh") or ""):
            feed.setdefault(norm(v["zh"]), k)
    baits = {}
    cats = json.load(open(os.path.join(ROOT, "data", "itemcats.json"), encoding="utf-8"))
    keys = list(game["baits"]) + [x for c in ("spinlure", "pilker", "seasoft", "teaser", "seanatural") for x in cats.get(c, [])]
    for k in keys:
        zh = (game["names"].get(k) or {}).get("zh") or ""
        if CJK.search(zh):
            baits.setdefault(norm(zh), k)
        if len(norm(k)) >= 6:
            baits.setdefault(norm(k), k)
    dips = json.load(open(os.path.join(ROOT, "data", "dips.json"), encoding="utf-8"))
    for k, v in dips.items():
        if CJK.search(v.get("zh") or ""):
            baits.setdefault(norm(v["zh"]), k)
    return feed, baits


def match_tail(line, names):
    s = norm(line)
    if len(s) < 2:
        return None
    best = []
    cj = "".join(CJK.findall(s))
    for k, en in names.items():
        if len(k) > len(s) + 2:
            continue
        if len(k) < 2:
            if cj == k:
                best.append((0, -1, k, en))
            continue
        if any(c not in cj for c in CJK.findall(k)):
            continue
        for cut in (0, 1, 2):
            t = s[:len(s)-cut] if cut else s
            if len(t) < len(k)-2:
                break
            d = min(lev(t[-len(k):], k), lev(t, k))
            if len(k) <= 4 and d > 0:
                continue
            best.append((d, -len(k), k, en))
    if not best:
        return None
    best.sort()
    d, nl, k, en = best[0]
    if d > max(1, len(k) // 5):
        return None
    for o in best[1:]:
        if o[0] != d or o[1] != nl:
            break
        if o[3] != en:
            return None
    return en


def ocr(img, psm):
    path = os.path.join(tempfile.gettempdir(), "wx_ocr.png")
    img.save(path)
    text = subprocess.run([os.environ.get("TESSERACT", "tesseract"), path, "-", "-l", "chi_sim+eng", "--psm", str(psm)], capture_output=True, text=True, encoding="utf-8").stdout
    return [x.replace(" ", "") for x in text.splitlines() if x.strip()]


def prepare(im):
    g = ImageOps.invert(ImageOps.grayscale(im))
    f = max(2.0, 2000.0 / g.width)
    return g.resize((int(g.width * f), int(g.height * f)), Image.LANCZOS)


def find_box(im):
    a = np.asarray(im).max(axis=2)[::4, ::4] < 28
    lab, _ = ndimage.label(a)
    best = None
    for i, sl in enumerate(ndimage.find_objects(lab), 1):
        if sl is None:
            continue
        ys, xs = sl
        hh, ww = ys.stop-ys.start, xs.stop-xs.start
        n = int((lab[sl] == i).sum())
        if n / (hh * ww) > 0.55 and ww > a.shape[1] / 12 and hh > a.shape[0] / 8 and (best is None or n > best[0]):
            best = (n, xs.start * 4, ys.start * 4, xs.stop * 4, ys.stop * 4)
    return best


def feed_from_lines(lines, names):
    feed = []
    inside = False
    for line in lines:
        if line.startswith("类型"):
            inside = True
            continue
        if not inside:
            continue
        if line.startswith("情况") or line.startswith("类别"):
            break
        rest = LABEL.sub("", CRUSHED.sub("", line))
        if len(norm(rest)) < 2:
            continue
        en = match_tail(rest, names)
        if en and en not in feed:
            feed.append(en)
    return feed


def baits_from_lines(lines, names):
    baits = []
    inside = False
    for line in lines:
        if HEAD.search(line):
            inside = True
            continue
        if not inside or SKIP.search(line) or len(norm(line)) < 2:
            continue
        en = match_tail(line, names)
        if en and en not in baits:
            baits.append(en)
    return baits


def read_image(data, feed_names, bait_names):
    im = Image.open(io.BytesIO(data)).convert("RGB")
    feed, baits = [], []
    box = find_box(im)
    if box:
        crop = ImageOps.invert(ImageOps.grayscale(im.crop(box[1:])))
        crop = crop.resize((crop.width * 3, crop.height * 3), Image.LANCZOS)
        feed = feed_from_lines(ocr(crop, 4), feed_names)
    if not feed:
        lines = ocr(prepare(im), 6)
        baits = baits_from_lines(lines, bait_names)
        if not baits:
            feed = feed_from_lines(lines, feed_names)
    return feed, baits


def main(src, cache_path, out_path):
    feed_names, bait_names = candidates()
    cache = {}
    if os.path.exists(cache_path):
        cache = {k: v for k, v in json.load(open(cache_path, encoding="utf-8")).items() if isinstance(v, dict) and v.get("v") == VERSION}
    out = {}
    done = 0
    start = time.time()
    posts = []
    for f in glob.glob(os.path.join(src, "api_wechat_positions_*")):
        posts.extend(json.load(open(f, encoding="utf-8")).get("positions", []))
    posts.sort(key=lambda p: p.get("publish_date") or "", reverse=True)
    for p in posts:
        pid = p.get("post_id") or ""
        urls = ((p.get("image_urls") or {}).get("config") or [])[:3]
        if not pid or not urls:
            continue
        if pid not in cache:
            if done >= MAX_NEW or time.time()-start > BUDGET:
                continue
            res = {"v": VERSION, "f": [], "b": []}
            ok = True
            for u in urls:
                try:
                    req = urllib.request.Request(u, headers={"User-Agent": "RF4CompanionMirror/1.0 (+https://github.com/Nalathan01/rf4-companion)"})
                    with urllib.request.urlopen(req, timeout=30) as r:
                        feed, baits = read_image(r.read(), feed_names, bait_names)
                except Exception as e:
                    print("Bild", pid, e)
                    ok = False
                    break
                res["f"] += [x for x in feed if x not in res["f"]]
                res["b"] += [x for x in baits if x not in res["b"]]
                time.sleep(0.3)
            if not ok:
                continue
            cache[pid] = res
            done += 1
            if done % 20 == 0:
                json.dump(cache, open(cache_path, "w", encoding="utf-8"), ensure_ascii=False)
        if cache[pid]["f"] or cache[pid]["b"]:
            out[pid] = cache[pid]
    json.dump(cache, open(cache_path, "w", encoding="utf-8"), ensure_ascii=False)
    json.dump(out, open(out_path, "w", encoding="utf-8"), ensure_ascii=False)
    print("Aus Bild:", len(out), "Spots, Futter:", sum(1 for v in out.values() if v["f"]), "Köder:", sum(1 for v in out.values() if v["b"]), "neu gelesen:", done)


if __name__ == "__main__":
    main(*sys.argv[1:4])
