"""motionbgs 站内搜索解析器（F3b 联网搜索的原型实现）。

列表页内嵌 /media/<id>/<slug>.<分辨率> 资源标识，据此还原：
- 缩略图 / 4K 静态封面 / 1080P 动态 / 4K 动态
- 详情页地址

用法:
    python tools/search_online.py "neon city"
    python tools/search_online.py anime --limit 5

限流约定：请求间隔 >= 800ms，失败重试 1 次（指数退避），连续失败即停止。
"""
import argparse
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
BASE = "https://motionbgs.com"
MIN_INTERVAL = 0.8
_last_call = [0.0]


def _throttle():
    gap = time.time() - _last_call[0]
    if gap < MIN_INTERVAL:
        time.sleep(MIN_INTERVAL - gap)
    _last_call[0] = time.time()


def fetch(url, retries=1, timeout=20):
    """带节流与单次重试的 GET，返回解码后的文本；失败抛 RuntimeError。"""
    last = None
    for attempt in range(retries + 1):
        _throttle()
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read()
            if not raw:
                raise RuntimeError("空响应")
            return raw.decode("utf-8", "ignore")
        except (urllib.error.URLError, RuntimeError, TimeoutError) as e:
            last = e
            if attempt < retries:
                time.sleep(1.5 * (attempt + 1))
    raise RuntimeError(f"请求失败: {url} ({last})")


def parse_items(html, limit=None):
    """从列表页 HTML 提取条目，按 slug 去重。纯函数，可单测。"""
    stems = re.findall(r"/media/(\d+)/([a-z0-9-]+)\.(\d{3,4}x\d{3,4})", html)
    out, seen = [], set()
    for mid, slug, _res in stems:
        if slug in seen:
            continue
        seen.add(slug)
        out.append({
            "slug": slug,
            "title": " ".join(w.capitalize() for w in slug.replace("-", " ").split()),
            "page": f"{BASE}/{slug}",
            "thumb": f"{BASE}/media/{mid}/{slug}.3840x2160.jpg",
            "jpg_4k": f"{BASE}/media/{mid}/{slug}.3840x2160.jpg",
            "mp4_1080": f"{BASE}/media/{mid}/{slug}.1920x1080.mp4",
            "mp4_4k": f"{BASE}/media/{mid}/{slug}.3840x2160.mp4",
        })
        if limit and len(out) >= limit:
            break
    return out


def search(keyword, limit=None):
    """关键词搜索。

    注意：站点对无匹配的关键词不会返回空列表，而是回一批推荐内容
    （实测 「zzzqqqxxx」 仍返回 4 条无关结果）。因此上层不要用「列表为空」
    判断无结果，应以标题与关键词是否匹配来决定是否提示「没搜到」。
    """
    url = f"{BASE}/search?q={urllib.parse.quote(keyword)}"
    html = fetch(url)
    return parse_items(html, limit)


def by_tag(tag, limit=None):
    url = f"{BASE}/tag:{urllib.parse.quote(tag)}/"
    return parse_items(fetch(url), limit)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("keyword")
    ap.add_argument("--limit", type=int, default=10)
    ap.add_argument("--tag", action="store_true", help="按分类频道取，而非关键词搜索")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()

    rows = by_tag(a.keyword, a.limit) if a.tag else search(a.keyword, a.limit)
    if a.json:
        print(json.dumps(rows, ensure_ascii=False, indent=1))
        return
    if not rows:
        print(f"[无结果] 「{a.keyword}」没有搜到内容（可能是关键词无匹配或触发限流，稍后再试）")
        return
    print(f"[{len(rows)} 条] 「{a.keyword}」\n")
    for i, r in enumerate(rows, 1):
        print(f"{i:2}. {r['title']}")
        print(f"    详情 {r['page']}")


if __name__ == "__main__":
    main()
