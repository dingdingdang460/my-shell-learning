"""把筛选出的动态壁纸下载进本地库（library/），并写入索引。

设计约束：
- 只存 1080P 时以本机原生分辨率 1920x1080 为准，避免 4K 解码带来的无谓功耗；
- 4K 直链写入索引，日后可在软件里一键升级单张，不必重抓页面；
- 每个文件都做完整性校验（字节数一致 + ftyp 头 + mvhd 时长 > 0），不靠"下载没报错"。

用法:
    python tools/fetch_wallpapers.py --numbers 48,49,50 --res 1080
"""
import argparse
import concurrent.futures as cf
import json
import os
import struct
import time
import urllib.request

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"


def mp4_duration(path):
    """解析 mp4 的 mvhd 盒子取时长（秒）。纯函数，无外部依赖。"""
    with open(path, "rb") as f:
        data = f.read()
    i = data.find(b"mvhd")
    if i < 0:
        return 0.0
    ver = data[i + 4]
    try:
        if ver == 0:
            timescale, duration = struct.unpack(">II", data[i + 16 : i + 24])
        else:
            timescale, duration = struct.unpack(">IQ", data[i + 24 : i + 36])
    except struct.error:
        return 0.0
    return round(duration / timescale, 2) if timescale else 0.0


def download(url, dest, timeout=180):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        total = int(r.headers.get("Content-Length") or 0)
        tmp = dest + ".part"
        got = 0
        with open(tmp, "wb") as f:
            while True:
                chunk = r.read(262144)
                if not chunk:
                    break
                f.write(chunk)
                got += len(chunk)
        os.replace(tmp, dest)
        return got, total


def verify(path, expected):
    if not os.path.exists(path):
        return False, "文件不存在"
    size = os.path.getsize(path)
    if expected and size != expected:
        return False, f"字节数不符 {size}/{expected}"
    with open(path, "rb") as f:
        head = f.read(32)
    if b"ftyp" not in head:
        return False, "缺少 ftyp 头，不是合法 mp4"
    dur = mp4_duration(path)
    if dur <= 0:
        return False, "时长解析失败"
    return True, f"{dur}s"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--numbers", required=True)
    ap.add_argument("--data", default="probe/candidates.json")
    ap.add_argument("--lib", default="D:/Wallpaper")
    ap.add_argument("--res", choices=["1080", "4k"], default="1080")
    a = ap.parse_args()

    rows = json.load(open(a.data, encoding="utf-8"))
    nums = [int(x) for x in a.numbers.split(",") if x.strip()]
    vdir = os.path.join(a.lib, "videos")
    os.makedirs(vdir, exist_ok=True)
    index_path = os.path.join(a.lib, "index.json")
    index = json.load(open(index_path, encoding="utf-8")) if os.path.exists(index_path) else []

    def job(n):
        r = rows[n - 1]
        url = r["mp4_4k"] if a.res == "4k" else r["mp4_1080"]
        if a.res == "4k" and not r.get("size_4k"):
            url = r["mp4_1080"]
        dest = os.path.join(vdir, r["slug"] + ".mp4")
        if os.path.exists(dest) and os.path.getsize(dest) > 1024 * 512:
            ok, note = verify(dest, 0)
            return n, r, ok, note, os.path.getsize(dest), url, "skip"
        t0 = time.time()
        try:
            got, total = download(url, dest)
        except Exception as e:
            return n, r, False, f"下载失败 {e}", 0, url, "fail"
        ok, note = verify(dest, total)
        return n, r, ok, note, got, url, f"{time.time() - t0:.1f}s"

    results = []
    with cf.ThreadPoolExecutor(max_workers=4) as ex:
        for res in ex.map(job, nums):
            results.append(res)
            n, r, ok, note, size, url, extra = res
            print(f"[{'OK ' if ok else 'ERR'}] #{n} {r['slug']} {size/1048576:.1f}MB {note} ({extra})")

    ok_rows = [x for x in results if x[2]]
    indexed = {x["slug"] for x in index}
    for n, r, ok, note, size, url, extra in ok_rows:
        if r["slug"] in indexed:
            continue
        index.append({
            "id": r["slug"],
            "title": r["title"],
            "tag": r["tag"],
            "page": r["page"],
            "file": f"videos/{r['slug']}.mp4",
            "thumb": f"thumbs/{r['slug']}.jpg",
            "bytes": size,
            "duration_s": float(str(note).replace("s", "")) if str(note).endswith("s") else 0,
            "src_res": "2160p" if "3840x2160" in url else "1080p",
            "upgrade_4k": r["mp4_4k"],
            "added_at": time.strftime("%Y-%m-%d %H:%M"),
        })
    with open(index_path, "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=1)

    total = sum(x["bytes"] for x in index)
    bad = [x for x in results if not x[2]]
    print(f"\n[库] {len(index)} 条 / {total/1048576:.1f}MB → {index_path}")
    if bad:
        print(f"[失败] {len(bad)} 条: " + ", ".join(str(x[0]) for x in bad))


if __name__ == "__main__":
    main()
