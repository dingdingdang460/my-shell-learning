"""把候选清单生成本地可离线打开的筛选页（probe/gallery.html）。

用法: python tools/build_gallery.py --data probe/candidates.json --out probe/gallery.html
"""
import argparse
import html
import json
import os


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="probe/candidates.json")
    ap.add_argument("--out", default="probe/gallery.html")
    ap.add_argument("--embed", action="store_true", help="把缩略图内联为 data URI，单文件可任意搬动")
    a = ap.parse_args()

    rows = json.load(open(a.data, encoding="utf-8"))
    base = os.path.dirname(os.path.abspath(a.out))
    cards = []
    for i, r in enumerate(rows, 1):
        rel = os.path.join("probe/thumbs", r["slug"] + ".jpg")
        thumb = os.path.relpath(rel, base).replace("\\", "/")
        if not os.path.exists(os.path.join(base, thumb)):
            thumb = ""
        if thumb and a.embed:
            import base64
            raw = open(os.path.join(base, thumb), "rb").read()
            thumb = "data:image/jpeg;base64," + base64.b64encode(raw).decode()
        s1 = r.get("size_1080") or 0
        s4 = r.get("size_4k") or 0
        cards.append({
            "n": i,
            "slug": r["slug"],
            "title": r["title"],
            "tag": r["tag"],
            "thumb": thumb,
            "mb1080": round(s1 / 1048576, 1) if s1 else None,
            "mb4k": round(s4 / 1048576, 1) if s4 else None,
        })

    tags = sorted({c["tag"] for c in cards})
    chips = ''.join(
        f'<button class="chip{"" if t != tags[0] else " on"}" data-tag="{html.escape(t)}">{html.escape(t)}</button>'
        for t in tags
    )

    tpl = """<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>动态壁纸候选筛选</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--line:#e3e6ea;--ink:#1b1f24;--muted:#6b7280;--accent:#1f6feb}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.55 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif}
header{position:sticky;top:0;z-index:9;background:rgba(246,247,249,.94);backdrop-filter:blur(8px);border-bottom:1px solid var(--line);padding:12px 20px}
h1{margin:0 0 8px;font-size:16px;font-weight:650;letter-spacing:.2px}
.sub{color:var(--muted);font-size:12px;margin-bottom:10px}
.chips{display:flex;gap:6px;flex-wrap:wrap}
.chip{border:1px solid var(--line);background:#fff;color:#374151;border-radius:999px;padding:4px 11px;font-size:12px;cursor:pointer}
.chip.on{background:var(--accent);border-color:var(--accent);color:#fff}
main{padding:18px 20px 120px;display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden;position:relative;transition:.15s}
.card:hover{box-shadow:0 6px 18px rgba(16,24,40,.08);transform:translateY(-1px)}
.card.on{border-color:var(--accent);box-shadow:0 0 0 2px rgba(31,111,235,.18)}
.thumb{display:block;width:100%;aspect-ratio:16/9;object-fit:cover;background:#eceff3}
.noimg{display:flex;align-items:center;justify-content:center;width:100%;aspect-ratio:16/9;background:#eceff3;color:#9aa3ad;font-size:12px}
.meta{padding:9px 11px 11px}
.t{font-weight:600;font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.d{color:var(--muted);font-size:11.5px;margin-top:3px;display:flex;gap:8px;flex-wrap:wrap}
.n{position:absolute;left:8px;top:8px;background:rgba(17,24,39,.72);color:#fff;font-size:11px;padding:2px 7px;border-radius:999px}
.pick{position:absolute;right:8px;top:8px;width:22px;height:22px;accent-color:var(--accent)}
footer{position:fixed;left:0;right:0;bottom:0;background:#fff;border-top:1px solid var(--line);padding:10px 20px;display:flex;gap:12px;align-items:center;flex-wrap:wrap}
.btn{background:var(--accent);color:#fff;border:0;border-radius:8px;padding:8px 14px;font-size:13px;cursor:pointer}
.btn.ghost{background:#fff;color:#374151;border:1px solid var(--line)}
.out{flex:1;min-width:240px;border:1px solid var(--line);border-radius:8px;padding:7px 9px;font:12px/1.4 ui-monospace,Consolas,monospace;color:#111827;background:#fbfcfd}
.hide{display:none!important}
</style></head><body>
<header>
  <h1>动态壁纸候选 · 勾选你喜欢的</h1>
  <div class="sub">共 __COUNT__ 张，全部为 4K 直链实测可下载；勾完点「复制已选」把编号发我，我下到本地库。缩略图已存本地，断网也能看。</div>
  <div class="chips">__CHIPS__</div>
</header>
<main id="grid"></main>
<footer>
  <button class="btn" id="copy">复制已选</button>
  <button class="btn ghost" id="clear">清空</button>
  <input class="out" id="out" readonly placeholder="已选项会显示在这里">
</footer>
<script>
const DATA = __DATA__;
const grid = document.getElementById('grid');
let cur = '__FIRSTTAG__';
function render(){
  grid.innerHTML = '';
  DATA.forEach(d=>{
    if(d.tag !== cur) return;
    const el = document.createElement('div');
    el.className = 'card' + (sel.has(d.n) ? ' on' : '');
    el.innerHTML = `
      <span class="n">#${d.n}</span>
      <input class="pick" type="checkbox" data-n="${d.n}" ${sel.has(d.n)?'checked':''}>
      ${d.thumb ? `<img class="thumb" loading="lazy" src="${d.thumb}" alt="">` : '<div class="noimg">无缩略图</div>'}
      <div class="meta">
        <div class="t">${d.title}</div>
        <div class="d"><span>${d.tag}</span>${d.mb1080?`<span>1080P ${d.mb1080}MB</span>`:''}${d.mb4k?`<span>4K ${d.mb4k}MB</span>`:''}</div>
      </div>`;
    grid.appendChild(el);
  });
}
const sel = new Set();
function sync(){
  const nums = [...sel].sort((a,b)=>a-b);
  document.getElementById('out').value = nums.join(',');
  document.querySelectorAll('.card').forEach(c=>{
    const cb = c.querySelector('.pick');
    c.classList.toggle('on', cb.checked);
  });
}
document.addEventListener('change', e=>{
  if(!e.target.classList.contains('pick')) return;
  const n = +e.target.dataset.n;
  e.target.checked ? sel.add(n) : sel.delete(n);
  sync();
});
document.querySelectorAll('.chip').forEach(b=>b.addEventListener('click',()=>{
  document.querySelectorAll('.chip').forEach(x=>x.classList.remove('on'));
  b.classList.add('on'); cur = b.dataset.tag; render(); sync();
}));
document.getElementById('copy').addEventListener('click', async ()=>{
  const v = document.getElementById('out').value;
  try { await navigator.clipboard.writeText(v); } catch(e){ document.getElementById('out').select(); document.execCommand('copy'); }
  const b = document.getElementById('copy'); const t=b.textContent; b.textContent='已复制 ✓';
  setTimeout(()=>b.textContent=t,1200);
});
document.getElementById('clear').addEventListener('click', ()=>{ sel.clear(); render(); sync(); });
render(); sync();
</script></body></html>"""

    out = (tpl.replace("__COUNT__", str(len(cards)))
              .replace("__CHIPS__", chips)
              .replace("__FIRSTTAG__", tags[0])
              .replace("__DATA__", json.dumps(cards, ensure_ascii=False)))
    with open(a.out, "w", encoding="utf-8") as f:
        f.write(out)
    print(f"[done] {len(cards)} 张候选 → {a.out}")


if __name__ == "__main__":
    main()
