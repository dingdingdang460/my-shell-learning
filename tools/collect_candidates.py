"""采集 motionbgs.com 动态壁纸候选（公开页面，仅供自用筛选）。

列表页（/tag:<name>/）内嵌 /media/<id>/<slug>.<分辨率> 资源标识，
据此可还原 1080P / 4K 直链与缩略图，无需逐条打开详情页。

用法:
    python tools/collect_candidates.py --tags nature,space,city --out probe/candidates.json
"""
import argparse
import concurrent.futures as cf
import json
import re
import urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
BASE = "https://motionbgs.com"


def get(url, timeout=25):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def head_size(url, timeout=15):
    req = urllib.request.Request(url, headers={"User-Agent": UA}, method="HEAD")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return int(r.headers.get("Content-Length") or 0)
    except Exception:
        return 0


def title_from_slug(slug):
    return " ".join(w.capitalize() for w in slug.replace("-", " ").split())


def collect_tag(tag):
    html = get(f"{BASE}/tag:{tag}/").decode("utf-8", "ignore")
    found = []
    for mid, slug, res in re.findall(r"/media/(\d+)/([a-z0-9-]+)\.(\d{3,4}x\d{3,4})", html):
        found.append((mid, slug))
    seen, out = set(), []
    for mid, slug in found:
        if slug in seen:
            continue
        seen.add(slug)
        out.append({
            "tag": tag,
            "slug": slug,
            "title": title_from_slug(slug),
            "page": f"{BASE}/{slug}",
            "thumb": f"{BASE}/media/{mid}/{slug}.960x540.jpg",
            "mp4_1080": f"{BASE}/media/{mid}/{slug}.1920x1080.mp4",
            "mp4_4k": f"{BASE}/media/{mid}/{slug}.3840x2160.mp4",
        })
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tags", default="nature,space,city,abstract,fire,car,ocean,rain")
    ap.add_argument("--out", default="probe/candidates.json")
    ap.add_argument("--probe", action="store_true", help="HEAD 探测可用分辨率与体积")
    a = ap.parse_args()

    tags = [t.strip() for t in a.tags.split(",") if t.strip()]
    rows, seen = [], set()
    for t in tags:
        try:
            items = collect_tag(t)
        except Exception as e:
            print(f"[tag fail] {t}: {e}")
            continue
        new = [x for x in items if x["slug"] not in seen]
        seen.update(x["slug"] for x in new)
        rows.extend(new)
        print(f"[tag ok] {t}: 页面 {len(items)} 条，新增 {len(new)} 条")

    if a.probe:
        def probe(r):
            r["size_1080"] = head_size(r["mp4_1080"])
            r["size_4k"] = head_size(r["mp4_4k"])
            return r

        with cf.ThreadPoolExecutor(max_workers=10) as ex:
            rows = list(ex.map(probe, rows))
        rows = [r for r in rows if r.get("size_1080")]

    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(rows, f, ensure_ascii=False, indent=1)
    print(f"[done] {len(rows)} 条 → {a.out}")


if __name__ == "__main__":
    main()
