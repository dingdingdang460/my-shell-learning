using System;
using System.Collections.Generic;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 给新入库的壁纸现写一句欢迎语。
    ///
    /// 为什么不用云端大模型：下载、云端出片都是「顺手」发生的事，多一次联网请求就多一处
    /// 会失败、会等、会花钱的地方；断网时还得有个兜底，不如一开始就本地算。
    /// 规则是「标题关键词 → 画面里有什么」，再按分类兜底、按色调补光线，最后接一句欢迎。
    ///
    /// 生成结果写到 %APPDATA%\wallpaper\greetings-auto.json，不碰 resources\greetings.json
    /// 里那批手工文案；同一张壁纸手工文案优先。删掉 auto 文件即可整批重新生成。
    /// </summary>
    public static class WelcomeGen
    {
        sealed class Scene
        {
            public string[] Keys;
            public string[] Heads;
            public Scene(string[] keys, string[] heads) { Keys = keys; Heads = heads; }
        }

        // 顺序只在「同一句里命中多个」时有意义：先收前两个，避免一条标题凑出四种画面
        static readonly Scene[] Scenes = new Scene[]
        {
            new Scene(new string[] { "cyber", "neon", "synth", "赛博", "霓虹" },
                      new string[] { "霓虹亮了一整条街", "招牌的蓝光落在水洼里", "电线把夜切成一格一格" }),
            new Scene(new string[] { "rain", "drizzle", "storm", "wet", "雨" },
                      new string[] { "雨还在下", "雨丝斜着落进灯里", "屋檐在滴水" }),
            new Scene(new string[] { "ocean", "sea", "coast", "beach", "wave", "cliff", "shore", "海", "浪", "礁" },
                      new string[] { "浪在礁石上碎开", "潮水正漫上沙滩", "海面一直泛着光" }),
            new Scene(new string[] { "forest", "tree", "wood", "leaf", "moss", "jungle", "林", "树", "草" },
                      new string[] { "树叶把光切成碎片", "风从林子深处过来", "林间的光走得很慢" }),
            new Scene(new string[] { "space", "star", "galaxy", "nebula", "cosmos", "moon", "星", "宇宙", "月" },
                      new string[] { "星星铺了一整片", "月光很轻", "夜色里全是光点" }),
            new Scene(new string[] { "sunrise", "sunup", "dawn", "morning", "daybreak", "日出", "晨", "朝", "黎明", "曙" },
                      new string[] { "天刚亮起来", "晨光正铺过来", "雾还没散，日头先到了" }),
            new Scene(new string[] { "torii", "shrine", "鸟居", "神社", "参道" },
                      new string[] { "鸟居正对着天光", "参道尽头有光", "穿过鸟居就是亮处" }),
            new Scene(new string[] { "cloud", "mist", "fog", "云海", "云层", "云雾", "雾" },
                      new string[] { "云海慢慢漫上山脊", "雾把远山藏了一半", "云在脚下走得比人慢" }),
            new Scene(new string[] { "sunset", "dusk", "evening", "twilight", "黄昏", "晚霞", "日落" },
                      new string[] { "晚霞还挂在天边", "天光正一点点暗下去", "云被烧成了橘色" }),
            new Scene(new string[] { "night", "midnight", "dark", "夜" },
                      new string[] { "夜已经很深了", "夜色压在窗上", "只剩几扇窗还亮着" }),
            new Scene(new string[] { "snow", "winter", "ice", "frost", "雪", "冬" },
                      new string[] { "外面下着雪", "雪把声音都吸走了", "窗上有薄薄一层霜" }),
            new Scene(new string[] { "cat", "kitten", "猫" },
                      new string[] { "猫还占着窗台", "猫在打盹", "猫抬头看了你一眼" }),
            new Scene(new string[] { "dog", "puppy", "狗" },
                      new string[] { "狗在门口等着", "尾巴先摇起来了" }),
            new Scene(new string[] { "bridge", "bay", "river", "lake", "桥", "江", "湖", "河" },
                      new string[] { "桥上的灯刚亮", "桥影落在水里", "水面把灯拉得很长" }),
            new Scene(new string[] { "mountain", "hill", "valley", "peak", "山", "谷" },
                      new string[] { "山影慢慢沉下去", "风从山谷里上来", "远处的山脊很清楚" }),
            new Scene(new string[] { "flower", "bloom", "petal", "garden", "花", "花园" },
                      new string[] { "花开在雾里", "花还开着", "风一过，花瓣动了动" }),
            new Scene(new string[] { "bike", "car", "ride", "road", "highway", "drive", "车", "路" },
                      new string[] { "路上的灯一路亮着", "车灯把夜划开", "路一直伸到灯里" }),
            new Scene(new string[] { "fire", "flame", "ember", "candle", "火", "烛" },
                      new string[] { "火还没熄", "火光在墙上跳", "炉子还热着" }),
            new Scene(new string[] { "city", "urban", "street", "downtown", "skyline", "tower", "cityscape", "城", "街", "楼" },
                      new string[] { "楼群的灯次第亮起", "街灯一盏一盏亮过去", "城市还没睡" }),
            new Scene(new string[] { "abstract", "geometric", "matrix", "circle", "fractal", "waveform", "抽象", "几何" },
                      new string[] { "线条一直绕回原点", "形状在慢慢转", "光沿着边线走" }),
            new Scene(new string[] { "anime", "girl", "boy", "character", "portrait", "动漫", "少女", "女孩" },
                      new string[] { "画面里的人正抬眼看你", "她安静地待在那里", "光落在她的侧脸上" })
        };

        // 收尾：都要是「回家」的意思，但别每次都同一句
        static readonly string[] Closes = new string[]
        {
            "欢迎回来", "灯一直亮着", "这里给你留着位置", "进来歇一会儿", "回家吧", "有盏灯是给你的"
        };

        /// <summary>分类兜底：标题里一个画面关键词都没有时，按 tag 说点什么</summary>
        static string[] TagHeads(string tag)
        {
            switch ((tag ?? "").ToLowerInvariant())
            {
                case "city": return new string[] { "楼群的灯还亮着", "城市慢下来了" };
                case "ocean": return new string[] { "浪声一直没停", "海面很宽" };
                case "nature": return new string[] { "风里有点草木味", "外面很安静" };
                case "space": return new string[] { "星星还挂着", "夜空很干净" };
                case "anime": return new string[] { "画面里的人还在等", "光正落在她身上" };
                case "abstract": return new string[] { "颜色在慢慢流动", "画面安静地转着" };
                case "fire": return new string[] { "火还烧着", "屋里是暖的" };
                case "car": return new string[] { "车灯还在前面", "路还没走完" };
                case "game": return new string[] { "存档点就在前面", "这一局先歇一下" };
                default: return new string[] { "灯亮着", "屋里有光", "桌面已经铺好了" };
            }
        }

        /// <summary>连分类都认不出时，用色调给半句光线</summary>
        static string ColorHead(string color)
        {
            if (color == ColorTag.Cool) return "蓝调的夜里很安静";
            if (color == ColorTag.Warm) return "暖光落在墙上";
            if (color == ColorTag.Dark) return "夜色还没散";
            if (color == ColorTag.Bright) return "天光大亮";
            return "画面正好看";
        }

        /// <summary>
        /// 生成一句。used = 已经用过的句子（同库里尽量不重复）。
        /// 返回的句子一定是「画面 + ，+ 欢迎」，长度控制在 6~26 字，超长会退回较短的组合。
        /// </summary>
        public static string Compose(string title, string tag, string color,
                                     HashSet<string> used, Random rnd)
        {
            Random r = rnd == null ? new Random() : rnd;
            List<string> heads = new List<string>();

            string t = (title ?? "").ToLowerInvariant();
            for (int i = 0; i < Scenes.Length && heads.Count < 6; i++)
            {
                Scene sc = Scenes[i];
                bool hit = false;
                for (int k = 0; k < sc.Keys.Length; k++)
                {
                    if (t.IndexOf(sc.Keys[k], StringComparison.Ordinal) >= 0) { hit = true; break; }
                }
                if (hit) heads.AddRange(sc.Heads);
            }

            if (heads.Count == 0) heads.AddRange(TagHeads(tag));
            if (heads.Count == 0) heads.Add(ColorHead(color));

            for (int attempt = 0; attempt < 32; attempt++)
            {
                string head = heads[r.Next(heads.Count)];
                string close = Closes[r.Next(Closes.Length)];
                string line = head + "，" + close;

                if (line.Length > 26) continue;
                // 「屋里有光，灯一直亮着」这种同字重复读着别扭，换一组再来
                if (head.IndexOf('灯') >= 0 && close.IndexOf('灯') >= 0) continue;
                if (used != null && used.Contains(line)) continue;
                return line;
            }

            // 组合都用过了：允许重复，但不能交空
            return heads[0] + "，" + Closes[0];
        }
    }
}
