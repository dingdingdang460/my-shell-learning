using System;
using System.IO;
using System.Net;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 全应用唯一的代理解析处。
    ///
    /// 以前每个网络调用点都硬写 `Proxy = null`（绕开系统代理，保证 B 站/壁纸站直连稳定），
    /// 于是想加「代理」这个设置项就得改七处、还容易漏。现在统一走这里：
    ///   - 配置为空 → Proxy = null，行为与以前完全一致（直连）；
    ///   - 配置了 → 用填的地址，支持 host:port 和 http://host:port。
    /// 注意：`Proxy = null` 在 .NET 里表示**不使用代理**，不是「用系统默认」，这点很关键。
    /// </summary>
    public static class Net
    {
        /// <summary>null = 直连</summary>
        public static IWebProxy Proxy;

        /// <summary>当前生效的代理地址（空 = 直连），只用于界面显示</summary>
        public static string Current = "";

        public static bool Configure(string proxyUrl)
        {
            Current = proxyUrl == null ? "" : proxyUrl.Trim();
            Proxy = null;
            if (Current.Length == 0) return true;

            try
            {
                string u = Current;
                if (u.IndexOf("://", StringComparison.Ordinal) < 0) u = "http://" + u;
                WebProxy wp = new WebProxy(u);
                wp.BypassProxyOnLocal = true;
                Proxy = wp;
                return true;
            }
            catch
            {
                Proxy = null;
                return false;
            }
        }

        public static void Apply(WebClient wc)
        {
            if (wc != null) wc.Proxy = Proxy;
        }

        public static void Apply(HttpWebRequest req)
        {
            if (req != null) req.Proxy = Proxy;
        }
    }

    /// <summary>
    /// 最小的 HTTP 助手：只做「发一个 JSON、拿一段文本」和「GET 一段文本」。
    ///
    /// 为什么不用 HttpClient / 第三方库：这个项目只用 Windows 自带的 csc 编译，
    /// 不能带 NuGet 包，所以只能落在 System.Net 上。够用就行。
    ///
    /// 两个细节值得留着：
    ///   1) 强制 TLS 1.2+：不设的话，老 .NET 默认走 TLS 1.0，两家云服务都会直接握手失败。
    ///   2) 出错时把**响应体**读出来：云服务的错误原因（额度不足 / Key 无效 / 参数不对）
    ///      都写在 body 里，只报「400 Bad Request」等于什么都没说。
    /// </summary>
    public static class Http
    {
        static Http()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;   // 12288 = Tls13（老枚举里没有）
            }
            catch
            {
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
                catch { }
            }
        }

        public static bool PostJson(string url, string json, string auth, string extraHeader,
                                    int timeoutMs, out string resp, out string err)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            return Send(url, "POST", payload, auth, extraHeader, timeoutMs, out resp, out err);
        }

        public static bool Get(string url, string auth, int timeoutMs, out string resp, out string err)
        {
            return Send(url, "GET", null, auth, null, timeoutMs, out resp, out err);
        }

        static bool Send(string url, string method, byte[] payload, string auth, string extraHeader,
                         int timeoutMs, out string resp, out string err)
        {
            resp = "";
            err = "";
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                // 传图是几 MB 的 base64，默认会先发 Expect:100-continue 干等一拍；
                // 关掉这次往返，对真实云 API 和本机模拟服务端都更快更稳。
                req.ServicePoint.Expect100Continue = false;
                Net.Apply(req);
                req.UserAgent = "WallpaperManager/1.0";
                req.Accept = "application/json";
                if (!string.IsNullOrEmpty(auth)) req.Headers["Authorization"] = auth;
                if (!string.IsNullOrEmpty(extraHeader))
                {
                    int c = extraHeader.IndexOf(':');
                    if (c > 0)
                        req.Headers[extraHeader.Substring(0, c).Trim()] = extraHeader.Substring(c + 1).Trim();
                }

                if (payload != null)
                {
                    req.ContentType = "application/json";
                    req.ContentLength = payload.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                }

                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    resp = sr.ReadToEnd();
                }
                return true;
            }
            catch (WebException we)
            {
                err = Describe(we);
                try
                {
                    if (we.Response != null)
                    {
                        using (Stream s = we.Response.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            resp = sr.ReadToEnd();
                        if (!string.IsNullOrEmpty(resp)) err += "｜响应：" + OneLine(resp, 400);
                    }
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
            finally
            {
                try { if (req != null) req.Abort(); } catch { }
            }
        }

        static string Describe(WebException we)
        {
            if (we.Status == WebExceptionStatus.Timeout) return "请求超时（网络慢或服务商排队）";
            if (we.Status == WebExceptionStatus.NameResolutionFailure) return "解析不了域名（检查网络/DNS）";
            if (we.Status == WebExceptionStatus.ConnectFailure) return "连不上服务器（检查网络或代理）";
            if (we.Status == WebExceptionStatus.TrustFailure) return "HTTPS 证书校验失败（可能是代理拦截）";

            HttpWebResponse r = we.Response as HttpWebResponse;
            if (r != null)
            {
                int code = (int)r.StatusCode;
                if (code == 401 || code == 403) return "鉴权失败（" + code + "）：API Key 不对或没开通这个模型";
                if (code == 429) return "被限流了（429）：稍等一下再试";
                if (code >= 500) return "服务端错误（" + code + "）：不是你的问题，过会儿再试";
                return "HTTP " + code + " " + r.StatusDescription;
            }
            return we.Status + "：" + we.Message;
        }

        static string OneLine(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
