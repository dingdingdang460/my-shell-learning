using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 自检用的本地模拟图生视频服务端（--cloudflowtest）。
    ///
    /// 为什么要有它：主人要的是「点击了能用」，不是「有个页面」。
    /// 没有真 API Key 时，只验证 UI 文案等于什么都没验 —— 所以这里起一个真的
    /// TCP/HTTP 服务，让应用内那套 HttpWebRequest / WebClient 原封不动地打过来：
    ///   提交 POST → 拿 task_id → 轮询 GET（前两次 RUNNING，第三次 SUCCEEDED）
    ///   → 按 video_url 下载真的 mp4 → SweepCloudJobs 收货入库。
    /// 全程真实网络栈、真实文件落盘，只是对端在本机。生产路径不受影响：
    /// GenApi.EndpointOverride 只有自检会设，正常启动永远是官方地址。
    ///
    /// 不用 HttpListener 是因为它在 Windows 上要 netsh urlacl（需要管理员）；
    /// TcpListener 绑 127.0.0.1 随机端口不需要任何权限，够这里用了。
    /// </summary>
    public sealed class MockCloudServer
    {
        TcpListener listener;
        Thread worker;
        volatile bool running;
        string videoPath;

        public string BaseUrl = "";
        public string TaskId = "mock-task-0001";
        public int RunningBeforeSuccess = 2;

        // 请求证据：自检逐项断言，证明不是「假装在跑」
        public int SubmitCount;
        public int PollCount;
        public int DownloadCount;
        public string LastBody = "";
        public string LastAuth = "";
        public string LastAsyncHeader = "";
        public string LastError = "";

        public bool Start(string video)
        {
            videoPath = video;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" + port;
                running = true;
                worker = new Thread(new ThreadStart(Loop));
                worker.IsBackground = true;
                worker.Start();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (worker != null && worker.IsAlive) worker.Join(1200); } catch { }
        }

        void Loop()
        {
            while (running)
            {
                TcpClient c = null;
                try { c = listener.AcceptTcpClient(); }
                catch { if (!running) break; continue; }
                try { Handle(c); }
                catch (Exception ex) { LastError = ex.GetType().Name + "：" + ex.Message; }
                finally { try { c.Close(); } catch { } }
            }
        }

        void Handle(TcpClient c)
        {
            NetworkStream s = c.GetStream();
            c.ReceiveTimeout = 8000;
            c.SendTimeout = 20000;

            // ---- 读请求头（逐字节读到空行；请求头很小，够用） ----
            List<byte> headBytes = new List<byte>();
            byte[] one = new byte[1];
            int tail = 0;
            while (tail < 4)
            {
                int n = s.Read(one, 0, 1);
                if (n <= 0) return;
                headBytes.Add(one[0]);
                byte expect;
                if (tail == 0 || tail == 2) expect = 13;      // \r
                else expect = 10;                              // \n
                if (one[0] == expect) tail++;
                else tail = (one[0] == 13) ? 1 : 0;
                if (headBytes.Count > 65536) return;
            }
            string head = Encoding.ASCII.GetString(headBytes.ToArray());
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant();
            string path = first[1];

            int contentLength = 0;
            string auth = "", asyncHeader = "";
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string k = lines[i].Substring(0, colon).Trim();
                string v = lines[i].Substring(colon + 1).Trim();
                if (string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(v, out contentLength);
                else if (string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase)) auth = v;
                else if (string.Equals(k, "X-DashScope-Async", StringComparison.OrdinalIgnoreCase)) asyncHeader = v;
            }

            byte[] body = new byte[0];
            if (contentLength > 0)
            {
                body = new byte[contentLength];
                int got = 0;
                while (got < contentLength)
                {
                    int n = s.Read(body, got, contentLength - got);
                    if (n <= 0) break;
                    got += n;
                }
            }

            // ---- 路由 ----
            if (method == "POST" && path.IndexOf("video-synthesis", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SubmitCount++;
                LastBody = Encoding.UTF8.GetString(body);
                LastAuth = auth;
                LastAsyncHeader = asyncHeader;
                ReplyJson(s, "{\"output\":{\"task_id\":\"" + TaskId
                             + "\",\"task_status\":\"PENDING\",\"message\":\"accepted\"}}");
                return;
            }

            if (method == "GET" && path.IndexOf("/api/v1/tasks/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                PollCount++;
                if (PollCount <= RunningBeforeSuccess)
                    ReplyJson(s, "{\"output\":{\"task_status\":\"RUNNING\"}}");
                else
                    ReplyJson(s, "{\"output\":{\"task_status\":\"SUCCEEDED\",\"video_url\":\""
                                 + BaseUrl + "/video/clip.mp4\"}}");
                return;
            }

            if (method == "GET" && path.IndexOf("/video/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DownloadCount++;
                byte[] v;
                try { v = File.ReadAllBytes(videoPath); }
                catch { v = new byte[2048]; }   // 文件没了也不能让自检卡住，让长度断言去抓
                Reply(s, "video/mp4", v);
                return;
            }

            Reply(s, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("mock: unknown route " + path));
        }

        static void ReplyJson(NetworkStream s, string json)
        {
            Reply(s, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        static void Reply(NetworkStream s, string contentType, byte[] body)
        {
            string header = "HTTP/1.1 200 OK\r\n"
                          + "Content-Type: " + contentType + "\r\n"
                          + "Content-Length: " + body.Length + "\r\n"
                          + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(header);
            s.Write(h, 0, h.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }
    }
}
