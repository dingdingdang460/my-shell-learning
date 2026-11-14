using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WallpaperManager
{
    /// <summary>删除功能的离线验证：只在临时目录里跑，不碰真实壁纸库</summary>
    static class TestDelete
    {
        static int fails = 0;

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
            if (!ok) fails++;
        }

        static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "wptest_lib");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(Path.Combine(root, "images"));
            File.WriteAllText(Path.Combine(root, "images", "a.jpg"), "AAA");
            File.WriteAllText(Path.Combine(root, "images", "b.jpg"), "BBB");
            File.WriteAllText(Path.Combine(root, "index.json"),
                "[{\"id\":\"a\",\"title\":\"A\",\"file\":\"images/a.jpg\",\"thumb\":\"images/a.jpg\"}," +
                "{\"id\":\"b\",\"title\":\"B\",\"file\":\"images/b.jpg\",\"thumb\":\"images/b.jpg\"}]",
                new UTF8Encoding(false));

            WallpaperItem a = new WallpaperItem();
            a.Id = "a"; a.Title = "A"; a.File = "images/a.jpg"; a.Thumb = "images/a.jpg";

            string err;
            bool ok = LibraryStore.Delete(a, root, out err);
            Check(ok, "删除返回成功（" + (err.Length == 0 ? "无错误" : err) + "）");
            Check(!File.Exists(Path.Combine(root, "images", "a.jpg")), "a.jpg 已从磁盘删除");
            Check(File.Exists(Path.Combine(root, "images", "b.jpg")), "b.jpg 仍在，未误删别的条目");

            string idx = File.ReadAllText(Path.Combine(root, "index.json"));
            Check(idx.IndexOf("\"a\"") < 0, "index.json 里 a 的记录已移除");
            Check(idx.IndexOf("\"b\"") >= 0, "index.json 里 b 的记录还在");
            Check(File.Exists(Path.Combine(root, "index.json.bak")), "生成 index.json.bak 备份");

            // 越界保护：index.json 里万一写了库外路径，也不能删库外文件
            string outside = Path.Combine(Path.GetTempPath(), "wp_outside.txt");
            File.WriteAllText(outside, "EVIL");
            WallpaperItem evil = new WallpaperItem();
            evil.Id = "evil";
            evil.File = "../../wp_outside.txt";
            LibraryStore.Delete(evil, root, out err);
            Check(File.Exists(outside), "库外文件没被删（越界保护生效）");
            try { File.Delete(outside); } catch { }

            List<WallpaperItem> list = LibraryStore.Load(root);
            bool hasA = false;
            for (int i = 0; i < list.Count; i++) if (list[i].Id == "a") hasA = true;
            Check(!hasA, "重新加载后 a 不再出现（库里有 " + list.Count + " 条）");

            try { Directory.Delete(root, true); } catch { }

            Console.WriteLine(fails == 0 ? "\n全部通过" : "\n失败 " + fails + " 项");
            Environment.Exit(fails);
        }
    }
}
