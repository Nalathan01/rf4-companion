import html
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
API = "https://discord.com/api/v10"
UA = "DiscordBot (https://github.com/Nalathan01/rf4-companion, 1.0)"
MAX_DAYS = 8
COORD = re.compile(r"(?<![\d.,])(\d{1,3})'?\s*[:;_/]\s*(\d{1,3})(?![\d.,]*\s*(?:uhr|h\b|:))", re.I)
COORD_DOT = re.compile(r"(?:^|koord\w*\s*:?\s*|kordi?\w*\s*:?\s*|spot\s*:?\s*|\s)(\d{2,3})[.,](\d{2,3})(?!\s*(?:kg|g|cm|m)\b)(?!\d)", re.I | re.M)
CLIP = re.compile(r"(?:clip\w*|klipp?\w*)\s*[:=]?\s*(?:auf\s*)?(\d{1,3}(?:[.,]\d)?)\s*m?\b|(\d{1,3}(?:[.,]\d)?)\s*m\s*(?:clip|klipp?)", re.I)
DEPTH = re.compile(r"tiefe\w*\s*[:=]?\s*(\d{1,3}(?:[.,]\d{1,2})?)\s*(cm|m)?", re.I)
TEMP = re.compile(r"(?:wasser)?temp\w*\s*[:=]?\s*(kalt|normal|warm|cold)", re.I)
MENTION = re.compile(r"<[@#][&!]?\d+>|<a?:\w+:\d+>")
GENERIC = {"fluss", "river", "lake", "lower", "untere", "rivulet"}
SPOTNAME = re.compile(r"spot|angelpl|fangpl", re.I)
NOT_BAIT = {"haken", "hook", "fisch", "fische"}
WORD = re.compile(r"[\wäöüß-]+", re.I)


def norm(s):
    return "".join(c for c in (s or "").lower() if c.isalnum())


def api(path, token):
    req = urllib.request.Request(API + path, headers={"Authorization": "Bot " + token, "User-Agent": UA})
    for _ in range(5):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 429:
                time.sleep(float(json.loads(e.read().decode("utf-8")).get("retry_after", 2)) + 0.5)
                continue
            if e.code in (403, 404):
                return None
            raise
    return None


class Catalog:
    def __init__(self):
        g = json.load(open(os.path.join(ROOT, "data", "gamedata.json"), encoding="utf-8"))
        items = json.load(open(os.path.join(ROOT, "data", "itemnames.json"), encoding="utf-8"))
        dips = json.load(open(os.path.join(ROOT, "data", "dips.json"), encoding="utf-8"))
        cats = json.load(open(os.path.join(ROOT, "data", "itemcats.json"), encoding="utf-8"))
        self.lakes = []
        for l in g["lakes"]:
            names = {norm(l["name"].get(k) or "") for k in ("de", "en")}
            names |= {norm(re.sub(r"^(fluss|der|die|das|untere)\s+|\s+(lake|river|see)$", "", l["name"].get(k) or "", flags=re.I)) for k in ("de", "en")}
            self.lakes.append((l["id"], {n for n in names if len(n) >= 4}, l.get("bounds")))
        words = {}
        for l in g["lakes"]:
            for k in ("de", "en"):
                for w in re.split(r"[\s.-]+", l["name"].get(k) or ""):
                    if len(w) >= 5 and w.lower() not in GENERIC:
                        words.setdefault(norm(w), set()).add(l["id"])
        for lid, names, _ in self.lakes:
            names |= {w for w, ids in words.items() if ids == {lid}}
        self.bounds = {i: b for i, _, b in self.lakes}
        self.fish = {}
        self.fish_family = {}
        for t in g["trophies"]:
            en = t["fish"]
            for lang in ("de", "en"):
                n = ((g["names"].get(en) or {}).get(lang) or (en if lang == "en" else "")).strip()
                if len(n) >= 3:
                    self.fish.setdefault(n.lower(), en)
                    last = n.split()[-1].split("-")[-1]
                    if len(last) >= 4:
                        self.fish_family.setdefault(last.lower(), set()).add(en)
        bait_keys = set(g["baits"]) | set(dips) | {x for c in ("spinlure", "pilker", "seasoft", "seanatural") for x in cats.get(c, [])}
        self.dips = set(dips)
        de = items["langs"].index("de")
        rows = {r[0]: r for r in items["rows"]}
        self.bait = {}
        self.bait_family = {}
        for en in bait_keys:
            r = rows.get(en)
            loc = [en, (g["names"].get(en) or {}).get("de") or ""]
            if r and len(r) > de:
                loc.append(r[de] or "")
            for n in loc:
                if not n:
                    continue
                self.bait.setdefault(norm(n), en)
                fam = re.sub(r"\s*\d+\s*$", "", n).strip()
                if fam != n and len(norm(fam)) >= 4:
                    self.bait_family.setdefault(norm(fam), set()).add(en)
        self.fish_names = {norm(k) for k in self.fish}
        self.maxlen = 5

    def lake_from(self, text):
        n = norm(text)
        best = None
        for lid, names, _ in self.lakes:
            for name in names:
                if name in n and (best is None or len(name) > best[1]):
                    best = (lid, len(name))
        return best[0] if best else None

    def in_bounds(self, lake, x, y):
        b = self.bounds.get(lake)
        if not b:
            return True
        return b["xMin"]-2 <= x <= b["xMax"]+2 and b["ySouth"]-2 <= y <= b["yNorth"]+2

    def find_fish(self, text):
        low = text.lower()
        found = []
        for name in sorted(self.fish, key=len, reverse=True):
            if re.search(r"(?<![\wäöüß])" + re.escape(name) + r"(?![\wäöüß])", low):
                if self.fish[name] not in found:
                    found.append(self.fish[name])
                low = low.replace(name, " ")
        fams = []
        for w in WORD.findall(low):
            f = self.fish_family.get(w.lower()) or self.fish_family.get(w.lower().rstrip("n"))
            if f and not any(x in found for x in f):
                fams.append((w, sorted(f)))
        return found, fams

    def find_baits(self, text):
        words = WORD.findall(text)
        found, fams, used = [], [], set()
        for size in range(self.maxlen, 0, -1):
            for i in range(len(words)+1-size):
                if any(j in used for j in range(i, i+size)):
                    continue
                key = norm(" ".join(words[i:i+size]))
                if len(key) < 3:
                    continue
                if key in self.fish_names or key in NOT_BAIT:
                    used.update(range(i, i+size))
                    continue
                if key in self.bait:
                    en = self.bait[key]
                    if en not in found:
                        found.append(en)
                    used.update(range(i, i+size))
                elif key in self.bait_family and len(key) >= 4:
                    fams.append((" ".join(words[i:i+size]), sorted(self.bait_family[key])))
                    used.update(range(i, i+size))
        return found, fams


def parse(cat, guild, ch, ch_lake, m):
    text = MENTION.sub(" ", m.get("content") or "")
    for e in m.get("embeds") or []:
        text += "\n" + (e.get("title") or "") + "\n" + (e.get("description") or "")
    lake = ch_lake or cat.lake_from(text)
    coords = None
    if lake:
        for rx in (COORD, COORD_DOT):
            for c in rx.finditer(text):
                x, y = int(c.group(1)), int(c.group(2))
                if cat.in_bounds(lake, x, y):
                    coords = (x, y)
                    break
            if coords:
                break
    fish, fish_fam = cat.find_fish(text)
    baits, bait_fam = cat.find_baits(text)
    r = {
        "id": "dcd:%s/%s" % (ch["id"], m["id"]),
        "guild": guild,
        "lake": lake,
        "posted": m["timestamp"],
        "url": "https://discord.com/channels/%s/%s/%s" % (guild, ch["id"], m["id"]),
        "fish": fish,
        "bait": [],
        "images": [a.get("url") for a in m.get("attachments") or [] if (a.get("content_type") or "").startswith("image/")],
        "src": "discord",
        "type": "spot",
    }
    if coords:
        r["x"], r["y"] = coords
    b = [x for x in baits if x not in cat.dips]
    dp = [x for x in baits if x in cat.dips]
    if b:
        r["baitDetail"] = " + ".join(b)
    if dp:
        r["dip"] = " + ".join(dp)
    c = CLIP.search(text)
    if c:
        r["clip"] = float((c.group(1) or c.group(2)).replace(",", "."))
    d = DEPTH.search(text)
    if d:
        v = float(d.group(1).replace(",", "."))
        r["depth"] = v / 100 if d.group(2) == "cm" else v
    t = TEMP.search(text)
    if t:
        r["ptemp"] = {"kalt": "cold", "cold": "cold", "warm": "warm"}.get(t.group(1).lower(), "normal")
    return r, text, fish_fam, bait_fam, m


def channels(token, cfg, cat):
    for g in api("/users/@me/guilds", token) or []:
        gc = cfg.get(g["id"], {})
        cats = set(gc.get("categories", []))
        ids = set(gc.get("channels", []))
        skip = set(gc.get("exclude", []))
        chs = api("/guilds/%s/channels" % g["id"], token) or []
        names = {c["id"]: c.get("name") or "" for c in chs if c.get("type") == 4}
        for c in chs:
            if c.get("type") not in (0, 5) or c["id"] in skip:
                continue
            if c.get("parent_id") in cats or c["id"] in ids or SPOTNAME.search(c.get("name") or "") or SPOTNAME.search(names.get(c.get("parent_id"), "")) or cat.lake_from(c.get("name") or ""):
                yield g["id"], c


def collect(token, cfg, cat, days):
    since = datetime.now(timezone.utc)-timedelta(days=days)
    out = []
    for guild, ch in channels(token, cfg, cat):
        ch_lake = cat.lake_from(ch["name"])
        before = None
        while True:
            msgs = api("/channels/%s/messages?limit=100%s" % (ch["id"], "&before=" + before if before else ""), token)
            if not msgs:
                break
            stop = False
            for m in msgs:
                if datetime.fromisoformat(m["timestamp"].replace("Z", "+00:00")) < since:
                    stop = True
                    break
                if m.get("type") not in (0, 19):
                    continue
                out.append((ch["name"],) + parse(cat, guild, ch, ch_lake, m))
            if stop or len(msgs) < 100:
                break
            before = msgs[-1]["id"]
            time.sleep(0.5)
    return out


def esc(s):
    return html.escape(str(s))


def thumb(a):
    u = a.get("proxy_url") or a.get("url") or ""
    return '<img src="%s" loading="lazy">' % esc(u + ("&" if "?" in u else "?") + "width=240&height=135")


def report(rows, path):
    def state(r):
        if r["lake"] and "x" in r and (r.get("baitDetail") or r.get("dip")):
            return "full", "Spot mit Köder"
        if r["lake"] and "x" in r:
            return "part", "Spot ohne Köder"
        return "none", "kein Spot erkannt"
    counts = {}
    body = []
    for chname, r, text, ff, bf, m in rows:
        cls, label = state(r)
        counts[label] = counts.get(label, 0) + 1
        info = ["Gewässer: " + esc(r["lake"] or "?"), "Koordinaten: " + (esc("%d:%d" % (r["x"], r["y"])) if "x" in r else "?")]
        if "clip" in r:
            info.append("Clip: %s m" % esc(r["clip"]))
        if "depth" in r:
            info.append("Tiefe: %s m" % esc(r["depth"]))
        if "ptemp" in r:
            info.append("Temperatur: " + esc(r["ptemp"]))
        if r["fish"]:
            info.append("Fisch: " + esc(", ".join(r["fish"])))
        for w, f in ff:
            info.append("Fisch unklar: „%s“ = %s" % (esc(w), esc(" / ".join(f[:4]) + (" ..." if len(f) > 4 else ""))))
        if r.get("baitDetail"):
            info.append("Köder: " + esc(r["baitDetail"]))
        if r.get("dip"):
            info.append("Dip: " + esc(r["dip"]))
        for w, f in bf:
            info.append("Köder ohne Größe: „%s“ = %s" % (esc(w), esc(" / ".join(f[:3]) + (" ..." if len(f) > 3 else ""))))
        thumbs = "".join(thumb(a) for a in (m.get("attachments") or []) if (a.get("content_type") or "").startswith("image/"))
        body.append('<tr class="%s"><td><b>%s</b><br><span class="d">#%s<br>%s</span><br><a href="%s">öffnen</a></td><td>%s</td><td class="t">%s</td><td class="i">%s</td></tr>' % (
            cls, esc(label), esc(chname), esc(r["posted"][:10]), esc(r["url"]), "<br>".join(info), esc(text.strip()[:500]).replace("\n", "<br>"), thumbs))
    summ = " · ".join("%s: %d" % (esc(k), v) for k, v in sorted(counts.items()))
    page = """<!doctype html><html lang="de"><head><meta charset="utf-8"><title>Discord Spot Test</title><style>
:root{--bg:#161b20;--fg:#e8ecf0;--mut:#8a96a3;--line:#2a323a;--a:#52aad0}
body{background:var(--bg);color:var(--fg);font:14px/1.45 Segoe UI,sans-serif;margin:0;padding:16px}
h1{font-size:20px;margin:0 0 4px}p{color:var(--mut);margin:0 0 12px}
table{border-collapse:collapse;width:100%%}td{border-top:1px solid var(--line);padding:8px;vertical-align:top}
tr.full td:first-child{border-left:4px solid #4fbf6a}tr.part td:first-child{border-left:4px solid #e0b030}tr.none td:first-child{border-left:4px solid #7a5560}
.d{color:var(--mut);font-size:12px}a{color:var(--a)}.t{max-width:420px;color:#cfd6dd}.i img{height:90px;margin:2px;border-radius:4px}
label{margin-right:12px;color:var(--mut)}
</style></head><body><h1>Discord Spot Test</h1><p>%d Nachrichten · %s</p>
<p><label><input type="checkbox" checked onchange="f('full',this)"> mit Köder</label><label><input type="checkbox" checked onchange="f('part',this)"> ohne Köder</label><label><input type="checkbox" checked onchange="f('none',this)"> kein Spot</label></p>
<table>%s</table><script>function f(c,e){document.querySelectorAll('tr.'+c).forEach(r=>r.style.display=e.checked?'':'none')}</script></body></html>""" % (len(rows), summ, "".join(body))
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(page)
    return counts


def main():
    token = os.environ.get("DISCORD_BOT_TOKEN", "")
    if not token:
        print("DISCORD_BOT_TOKEN fehlt")
        return 1
    cfg = json.load(open(os.path.join(ROOT, "tools", "dc_channels.json"), encoding="utf-8"))
    cat = Catalog()
    if len(sys.argv) >= 3 and sys.argv[1] == "--test":
        rows = collect(token, cfg, cat, 3650)
        print(len(rows), report(rows, sys.argv[2]))
        return 0
    out = sys.argv[1] if len(sys.argv) > 1 else "dc_feed.json"
    rows = collect(token, cfg, cat, int(sys.argv[2]) if len(sys.argv) > 2 else MAX_DAYS)
    spots = [r for _, r, *_ in rows if r["lake"] and "x" in r]
    with open(out, "w", encoding="utf-8") as fh:
        json.dump({"generated": datetime.now(timezone.utc).isoformat(), "reports": spots}, fh, ensure_ascii=False)
    print(len(rows), "Nachrichten,", len(spots), "Spots")
    return 0


if __name__ == "__main__":
    sys.exit(main())
