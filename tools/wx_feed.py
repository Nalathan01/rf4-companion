import glob
import gzip
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

import numpy as np
from PIL import Image, ImageDraw, ImageOps
from scipy import ndimage
try:
    from rapidocr_onnxruntime import RapidOCR
except ImportError:
    RapidOCR = None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAX_NEW = 150
VERSION = 9
WORKERS = 3
BUDGET = 540
MIRROR = "https://raw.githubusercontent.com/Nalathan01/rf4-companion-data/mirror/spots.json.gz"
CJK = re.compile(r"[一-鿿]")
HEAD = re.compile(r"^(安装|钓组)|修改")
SKIP = re.compile(r"[:：+＋]")
LABEL = re.compile(r"^(.?基.?.?料|.{1,3}添.{1,2}剂|添加剂|增香剂|.?香料|真料|(pop-?up)?香.{0,2}成饵)", re.I)
CRUSHED = re.compile(r"[\(（]?(?<!磨)碎的[\)）]?")
OCR_FIX = {"蛋糖": "蛋糕", "王米": "玉米", "磨砻": "磨碎"}


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
    ALTS.clear()
    DIPS.clear()
    DIPS_ZH.clear()
    cats = json.load(open(os.path.join(ROOT, "data", "itemcats.json"), encoding="utf-8"))
    keys = list(game["baits"]) + [x for c in ("spinlure", "pilker", "seasoft", "teaser", "seanatural") for x in cats.get(c, [])]
    for k in keys:
        zh = (game["names"].get(k) or {}).get("zh") or ""
        if CJK.search(zh):
            ALTS.setdefault(norm(zh), [])
            if k not in ALTS[norm(zh)]:
                ALTS[norm(zh)].append(k)
            old = baits.get(norm(zh))
            if old is None or ("Pellets" in old and "Pellets" not in k):
                baits[norm(zh)] = k
        if len(norm(k)) >= 6:
            baits.setdefault(norm(k), k)
    dips = json.load(open(os.path.join(ROOT, "data", "dips.json"), encoding="utf-8"))
    for k, v in dips.items():
        DIPS.add(k)
        if CJK.search(v.get("zh") or ""):
            DIPS_ZH[k] = norm(re.sub(r"^[A-Za-z0-9'&.\s]+", "", v["zh"]))
            baits.setdefault(norm(v["zh"]), k)
    return feed, baits


ALTS = {}
DIPS = set()
DIPS_ZH = {}


def form_of(line):
    s = norm(line)
    if not ("形" in s or "式" in s):
        return ""
    if "玉米" in s or "人造" in s:
        return "corn"
    if "颗粒" in s or "颗" in s:
        return "pellets"
    if "小药" in s or "药" in s:
        return "dip"
    if "成饵" in s or "煮" in s or "下沉" in s:
        return "boilie"
    return ""


def kind_of(key):
    if key in DIPS:
        return "dip"
    if "Pellets" in key:
        return "pellets"
    if "Flavor" in key or not re.search(r"\d", key):
        return "corn"
    return "boilie"


def pick_form(en, form):
    if not form or kind_of(en) == form:
        return en
    if en in DIPS_ZH:
        for k in ALTS.get(DIPS_ZH[en], []):
            if kind_of(k) == form:
                return k
        return en
    for keys in ALTS.values():
        if en in keys:
            for k in keys:
                if kind_of(k) == form:
                    return k
    return en


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
        miss = [c for c in CJK.findall(k) if c not in cj]
        if miss and (len(miss) > 1 or len(k) < 6 or miss[0] != k[-1] or "".join(CJK.findall(k))[:-1] not in cj):
            continue
        kc = "".join(CJK.findall(k))
        i = s.rfind(kc)
        if len(k) <= 4 and kc and (i < 0 or i > 0 and CJK.match(s[i-1])):
            continue
        for cut in (0, 1, 2):
            t = s[:len(s)-cut] if cut else s
            if cut and CJK.search(s[len(s)-cut:]):
                break
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
    if d > max(1, len(k) // (3 if len(k) >= 6 else 5)):
        return None
    for o in best[1:]:
        if o[0] != d or o[1] != nl:
            break
        if o[3] != en:
            return None
    return en


def ocr(img, psm):
    fd, path = tempfile.mkstemp(suffix=".png")
    os.close(fd)
    try:
        img.save(path)
        text = subprocess.run([os.environ.get("TESSERACT", "tesseract"), path, "-", "-l", "chi_sim+eng", "--psm", str(psm)], capture_output=True, text=True, encoding="utf-8").stdout
    finally:
        os.remove(path)
    lines = [x.replace(" ", "") for x in text.splitlines() if x.strip()]
    for a, b in OCR_FIX.items():
        lines = [x.replace(a, b) for x in lines]
    return lines


def prepare(im, target=2000.0):
    g = ImageOps.invert(ImageOps.grayscale(im))
    f = min(4.0, target / g.width)
    return g.resize((int(g.width * f), int(g.height * f)), Image.LANCZOS)


def photo(im, blur):
    g = ImageOps.grayscale(im)
    f = min(3.0, 2000.0 / g.width)
    g = np.asarray(g.resize((int(g.width * f), int(g.height * f)), Image.LANCZOS), dtype=np.float32)
    g = ndimage.gaussian_filter(g, blur)
    return Image.fromarray(np.where(g-ndimage.uniform_filter(g, 61) > 18, 0, 255).astype(np.uint8))


def find_boxes(im, thr=28, smooth=1):
    a = ndimage.uniform_filter(np.asarray(im).max(axis=2).astype(np.float32), smooth)[::4, ::4] < thr
    lab, _ = ndimage.label(a)
    found = []
    for i, sl in enumerate(ndimage.find_objects(lab), 1):
        if sl is None:
            continue
        ys, xs = sl
        hh, ww = ys.stop-ys.start, xs.stop-xs.start
        n = int((lab[sl] == i).sum())
        if n / (hh * ww) > 0.55 and ww > a.shape[1] / 12 and hh > a.shape[0] / 8:
            found.append((n, xs.start * 4, ys.start * 4, xs.stop * 4, ys.stop * 4))
    return sorted(found, reverse=True)


def find_box(im, thr=28, smooth=1):
    found = find_boxes(im, thr, smooth)
    return found[0] if found else None


def box_feed(im, box, feed_names):
    gray = ImageOps.invert(ImageOps.grayscale(im.crop(box[1:])))
    for limit, width, psm in ((3.0, 1800.0, 4), (5.0, 2400.0, 6)):
        f = min(limit, width / gray.width)
        crop = gray.resize((int(gray.width * f), int(gray.height * f)), Image.LANCZOS)
        feed = feed_from_lines(ocr(crop, psm), feed_names)
        if feed:
            right = crop.crop((int(crop.width * 0.5), 0, crop.width, crop.height))
            feed += [en for en in (match_tail(x, feed_names) for x in ocr(right, 6)) if en and en not in feed]
            return feed
    return []


def feed_from_lines(lines, names):
    feed = []
    inside = False
    for line in lines:
        rest = LABEL.sub("", CRUSHED.sub("", line))
        if re.match(r"^类.{0,2}[:：]?$", line) or line.startswith("类型") or (LABEL.match(line) and len(norm(rest)) < 2):
            inside = True
            continue
        if not inside:
            continue
        if line.startswith("情况") or line.startswith("类别"):
            break
        if len(norm(rest)) < 2 or line.startswith("形式"):
            continue
        en = match_tail(rest, names)
        if en and en not in feed:
            feed.append(en)
    return feed


def baits_from_lines(lines, names):
    baits = []
    inside = not any(HEAD.search(x) for x in lines)
    for i, line in enumerate(lines):
        if HEAD.search(line):
            inside = True
            continue
        if not inside or SKIP.search(line) or LABEL.match(line) or len(norm(line)) < 2:
            continue
        en = match_tail(line, names)
        if en:
            form = next((f for f in (form_of(x) for x in lines[i+1:i+3]) if f), "")
            en = pick_form(en, form)
        if en and en not in baits:
            baits.append(en)
    return baits


LOCAL = threading.local()


def rapid_items(im):
    if not hasattr(LOCAL, "engine"):
        LOCAL.engine = RapidOCR()
    res, _ = LOCAL.engine(np.asarray(im)[:, :, ::-1])
    items = []
    for box, text, _ in res or []:
        xs, ys = [p[0] for p in box], [p[1] for p in box]
        items.append((min(ys), min(xs), max(xs), max(ys)-min(ys), text.replace(" ", "")))
    return sorted(items)


def rapid_fix(t):
    t = re.sub(r"[鲢鲜鮭](?=鱼|$)", "鲑", t)
    t = re.sub(r"(?<=[一-鿿])[sS]$", "5", t)
    return CRUSHED.sub("", t.strip("①©®Oo0 ") if re.search(r"[一-鿿]\d+[①©®]$", t) else t)


def rapid_feed(items, feed_names):
    head = next((i for i in items if re.match(r"^(类(?!别).{0,2}[:：]?|基.?料)$", i[4])), None)
    if not head:
        return []
    end = next((i[0] for i in items if i[0] > head[0] and abs(i[1]-head[1]) < head[3] * 3 and re.match(r"^(情况|可以使用|类别)", i[4])), None)
    feed = []
    for y, x0, x1, h, text in items:
        if y < head[0]-head[3] or (end and y >= end) or x0 < head[1] + head[3] * 4:
            continue
        en = match_tail(rapid_fix(text), feed_names)
        if en and en not in feed:
            feed.append(en)
    return feed


def rapid_baits(items, bait_names):
    baits = []
    for k, (y, x0, x1, h, text) in enumerate(items):
        form = form_of(text)
        if not form and not re.match(r"^形式.{0,3}饵鱼", text):
            continue
        above = [i for i in items[:k] if abs(i[1]-x0) < h * 2 and 0 < y-i[0] < h * 4 and not i[4].startswith("形式")]
        if not above:
            continue
        en = match_tail(rapid_fix(above[-1][4]), bait_names)
        if en:
            en = pick_form(en, form)
            if en not in baits:
                baits.append(en)
    return baits


def read_rapid(im, feed_names, bait_names):
    f = 1.0 if im.width >= 1000 else 1000.0 / im.width
    items = rapid_items(im.resize((int(im.width * f), int(im.height * f)), Image.LANCZOS) if f > 1 else im)
    feed = rapid_feed(items, feed_names)
    baits = rapid_baits(items, bait_names)
    if not baits:
        baits = baits_from_lines([rapid_fix(i[4]) for i in items], bait_names)
    return feed, baits


def read_image(data, feed_names, bait_names):
    im = Image.open(io.BytesIO(data)).convert("RGB")
    if RapidOCR is not None:
        feed, baits = read_rapid(im, feed_names, bait_names)
        if feed or baits:
            return feed, baits, False
    feed, baits = [], []
    masked = im.copy()
    draw = ImageDraw.Draw(masked)
    for box in find_boxes(im)[:3]:
        got = box_feed(im, box, feed_names)
        if got:
            feed += [x for x in got if x not in feed]
            draw.rectangle(box[1:], fill=(70, 70, 70))
    lines = ocr(prepare(masked), 6)
    baits = baits_from_lines(lines, bait_names)
    baits += [x for x in baits_from_lines(ocr(prepare(masked, 3200.0), 6), bait_names) if x not in baits]
    if not feed:
        feed = feed_from_lines(lines, feed_names)
    if feed or baits:
        return feed, baits, False
    reads = [ocr(photo(im, b), 6) for b in (1.5, 2.5)]
    for lines in reads:
        feed += [x for x in feed_from_lines(lines, feed_names) if x not in feed]
    box = find_box(im, 55, 9)
    if box:
        crop = im.crop(box[1:])
        crop = ImageOps.invert(ImageOps.grayscale(crop.crop((int(crop.width * 0.55), 0, crop.width, crop.height))))
        f = min(4.0, 900.0 / crop.width)
        crop = crop.resize((int(crop.width * f), int(crop.height * f)), Image.LANCZOS)
        vals = [en for en in (match_tail(x, feed_names) for x in ocr(crop, 6)) if en]
        if len(set(vals)) >= 2:
            feed += [x for x in vals if x not in feed]
    if not feed:
        for lines in reads:
            if any(HEAD.search(x) for x in lines):
                baits += [x for x in baits_from_lines(lines, bait_names) if x not in baits]
    return feed, baits, bool(feed or baits)


def read_post(urls, feed_names, bait_names, start):
    if time.time()-start > BUDGET:
        return None
    res = {"v": VERSION, "f": [], "b": [], "s": []}
    for u in urls:
        data = None
        for attempt in range(3):
            try:
                req = urllib.request.Request(u, headers={"User-Agent": "RF4CompanionMirror/1.0 (+https://github.com/Nalathan01/rf4-companion)"})
                with urllib.request.urlopen(req, timeout=30) as r:
                    data = r.read()
                break
            except Exception as e:
                print("Bild", u, e)
                time.sleep(2 + attempt * 3)
        if data is None:
            return None
        feed, baits, ph = read_image(data, feed_names, bait_names)
        if ph:
            res["p"] = 1
        res["f"] += [x for x in feed if x not in res["f"]]
        res["b"] += [x for x in baits if x not in res["b"]]
        if baits and baits not in res["s"]:
            res["s"].append(baits)
    return res


def older_posts():
    try:
        req = urllib.request.Request(MIRROR, headers={"User-Agent": "RF4CompanionMirror/1.0 (+https://github.com/Nalathan01/rf4-companion)"})
        with urllib.request.urlopen(req, timeout=60) as r:
            reports = json.loads(gzip.decompress(r.read())).get("reports", [])
    except Exception as e:
        print("Spiegel", e)
        return []
    return [(r.get("posted") or "", r["id"][3:], (r.get("cfg") or [])[:3]) for r in reports if str(r.get("id", "")).startswith("wx:")]


def main(src, cache_path, out_path):
    feed_names, bait_names = candidates()
    cache = {}
    if os.path.exists(cache_path):
        cache = {k: v for k, v in json.load(open(cache_path, encoding="utf-8")).items() if isinstance(v, dict) and (v.get("v") == VERSION or v.get("f") or v.get("b"))}
    start = time.time()
    posts = []
    for f in glob.glob(os.path.join(src, "api_wechat_positions_*")):
        posts.extend(json.load(open(f, encoding="utf-8")).get("positions", []))
    items = [(p.get("publish_date") or "", p.get("post_id") or "", ((p.get("image_urls") or {}).get("config") or [])[:3]) for p in posts]
    known = {x[1] for x in items}
    items += [x for x in older_posts() if x[1] not in known]
    items.sort(reverse=True)
    todo = {}
    for _, pid, urls in items:
        if pid and urls and (cache.get(pid) or {}).get("v") != VERSION and pid not in todo and len(todo) < MAX_NEW:
            todo[pid] = urls
    done = 0
    with ThreadPoolExecutor(WORKERS) as ex:
        jobs = {pid: ex.submit(read_post, urls, feed_names, bait_names, start) for pid, urls in todo.items()}
        for pid, job in jobs.items():
            res = job.result()
            if res is None:
                continue
            old = cache.get(pid) or {}
            if not (res["f"] or res["b"]) and (old.get("f") or old.get("b")):
                res = dict(old, v=VERSION)
            cache[pid] = res
            done += 1
            if done % 20 == 0:
                json.dump(cache, open(cache_path, "w", encoding="utf-8"), ensure_ascii=False)
    out = {pid: v for pid, v in cache.items() if v.get("f") or v.get("b")}
    json.dump(cache, open(cache_path, "w", encoding="utf-8"), ensure_ascii=False)
    json.dump(out, open(out_path, "w", encoding="utf-8"), ensure_ascii=False)
    print("Aus Bild:", len(out), "Spots, Futter:", sum(1 for v in out.values() if v["f"]), "Köder:", sum(1 for v in out.values() if v["b"]), "neu gelesen:", done, "offen:", len(todo)-done)


if __name__ == "__main__":
    main(*sys.argv[1:4])
