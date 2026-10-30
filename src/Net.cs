using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace NbaQuant
{
    static class Net
    {
        static Net() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; }
        public static Action<string> Log = s => Console.WriteLine(s);
        static DateTime lastBbref = DateTime.MinValue;

        public static string Get(string url, int retries = 3)
        {
            // Basketball-Reference rate-limits aggressive clients (~20 requests/minute)
            if (url.Contains("basketball-reference.com"))
            {
                var wait = TimeSpan.FromSeconds(4) - (DateTime.UtcNow - lastBbref);
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                lastBbref = DateTime.UtcNow;
            }
            Exception last = null;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Encoding = Encoding.UTF8;
                        wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NbaQuant/1.0";
                        return wc.DownloadString(url);
                    }
                }
                catch (Exception e) { last = e; Thread.Sleep(1000 * (i + 1)); }
            }
            throw new Exception("GET failed: " + url + " :: " + (last == null ? "" : last.Message));
        }

        // Returns the cached copy if younger than maxAge (null = never expires), otherwise downloads and caches.
        public static string Cached(string url, string path, TimeSpan? maxAge)
        {
            if (File.Exists(path) && (maxAge == null || DateTime.Now - File.GetLastWriteTime(path) < maxAge.Value))
                return File.ReadAllText(path, Encoding.UTF8);
            string s = Get(url);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, s, new UTF8Encoding(false));
            return s;
        }

        public static void Download(string url, string path)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NbaQuant/1.0";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                wc.DownloadFile(url, path);
            }
        }
    }
}
