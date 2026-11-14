using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 自检用的本机 HTTP 代理（--proxytest）。
    ///
    /// 交接文档第 9 节留的待办：「代理只验证了解析，没在真实代理后面跑过联网搜索」。
    /// Net.Configure 只是把字符串变成 WebProxy 对象，--configtest 验到这一层就停了；
    /// 这里起一个真的 TCP 代理，让应用内那套 HttpWebRequest / WebClient 原封不动地打过来，
    /// 用「请求行是不是绝对 URI」当客观证据：
    ///   走代理 = GET http://host/path HTTP/1.1
    ///   直连   = GET /path HTTP/1.1
    ///
    /// 代理不转发到外网，自己直接回一段固定 JSON：全程本机、零流量、零费用、不消耗任何额度。
    /// 不用 HttpListener 是因为它在 Windows 上要 netsh urlacl（需要管理员）。
    /// </summary>
    public sealed class MockProxyServer
    {
        TcpListener listener;
        Thread worker;
        volatile bool running;

        public int Port;
        public string BaseUrl = "";
        public int RequestCount;
        public string LastRequestLine = "";
        public string LastHost = "";
        public string LastError = "";

        public bool Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" + Port;
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
            c.SendTimeout = 8000;

            // 只读请求头：自检发的都是 GET，没有 body
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

            LastRequestLine = lines[0];
            LastHost = "";
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                if (string.Equals(lines[i].Substring(0, colon).Trim(), "Host", StringComparison.OrdinalIgnoreCase))
                    LastHost = lines[i].Substring(colon + 1).Trim();
            }
            RequestCount++;

            byte[] body = Encoding.UTF8.GetBytes("{\"proxy\":\"ok\"}");
            string header = "HTTP/1.1 200 OK\r\n"
                          + "Content-Type: application/json; charset=utf-8\r\n"
                          + "Content-Length: " + body.Length + "\r\n"
                          + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(header);
            s.Write(h, 0, h.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }
    }
}
