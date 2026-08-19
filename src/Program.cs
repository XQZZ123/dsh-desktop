using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop
{
    internal static class Program
    {
        private const string MUTEX_NAME = "DshDesktop.DSH.GUI.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            // 在创建任何窗口/UI 之前声明 PerMonitorV2 DPI 感知，
            // 否则 Windows 会整窗位图放大（125% 缩放下会模糊）。
            Dpi.Enable();

            if (args.Length > 0)
            {
                string a = args[0];
                if (a == "--check" || a == "--self-test") return Checker.Run();
                if (a == "--config-path")
                {
                    Console.WriteLine(Config.ConfigPath());
                    return 0;
                }
                if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("DshDesktop - DeepSeek Harness 桌面版");
                    Console.WriteLine("  无参数          启动桌面壳（后端未运行时自动启动）");
                    Console.WriteLine("  --check         自检：node / dsh 库 / 端口 / WebView2");
                    Console.WriteLine("  --config-path   打印配置文件位置");
                    Console.WriteLine("  --help          本帮助");
                    return 0;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            using (Mutex m = new Mutex(true, MUTEX_NAME, out createdNew))
            {
                if (!createdNew)
                {
                    Native.BringToFront(Config.Instance.Title);
                    return 0;
                }
                try
                {
                    Application.Run(new MainForm());
                }
                finally
                {
                    try { m.ReleaseMutex(); }
                    catch { }
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// 进程级 DPI 感知声明（与 app.manifest 双保险）：
    /// 优先 PerMonitorV2，回退到 system-DPI-aware 或 legacy 感知。
    /// </summary>
    internal static class Dpi
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        private static readonly IntPtr PMV2 = new IntPtr(-4);
        private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        public static void Enable()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(PMV2)) return;
            }
            catch { }
            try
            {
                if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0) return;
            }
            catch { }
            try { SetProcessDPIAware(); }
            catch { }
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static void BringToFront(string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            try
            {
                IntPtr h = FindWindowW(null, title);
                if (h != IntPtr.Zero) SetForegroundWindow(h);
            }
            catch { }
        }
    }

    /// <summary>Console self test: resolves dependencies and probes capabilities.</summary>
    internal static class Checker
    {
        public static int Run()
        {
            Console.WriteLine("== DshDesktop self test ==");
            Config cfg = Config.Instance;
            Console.WriteLine("config: " + Config.ConfigPath());

            string node = Locator.FindNode(cfg);
            Console.WriteLine("node    : " + (node == "" ? "NOT FOUND" : node));

            string lib = Locator.FindDshLib(cfg);
            Console.WriteLine("dsh lib : " + (lib == "" ? "NOT FOUND" : lib));

            string edge = Locator.FindEdge();
            Console.WriteLine("edge    : " + (edge == "" ? "NOT FOUND" : edge));

            Backend b = new Backend(cfg);
            Console.WriteLine("port " + cfg.Host + ":" + cfg.Port + " up: " + b.IsUp());

            Console.WriteLine("webview2: " + WebViewProbe());
            Console.WriteLine("spawn   : " + SpawnProbe());

            int fail = 0;
            if (node == "") { Console.WriteLine("[FAIL] node not found"); fail = 1; }
            if (lib == "") { Console.WriteLine("[FAIL] dsh lib not found"); fail = 1; }
            if (edge == "") { Console.WriteLine("[WARN] edge not found (shell falls back to WebView2 only)"); }

            Console.WriteLine(fail == 0 ? "RESULT: OK" : "RESULT: INCOMPLETE (see above)");
            return fail;
        }

        private static string WebViewProbe()
        {
            try
            {
                string udf = Path.Combine(Path.GetTempPath(), "DshDesktop-probe-" + Guid.NewGuid().ToString("N"));
                CoreWebView2Environment env = CoreWebView2Environment.CreateAsync(null, udf).GetAwaiter().GetResult();
                return "OK (" + env.BrowserVersionString + ")";
            }
            catch (Exception ex)
            {
                return "FAIL: " + ex.Message;
            }
        }

        private static string SpawnProbe()
        {
            string node = Locator.FindNode(Config.Instance);
            if (node == "") return "FAIL: node not found";
            int port = FreePort();
            if (port <= 0) return "FAIL: no free port";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(node,
                    "-e \"require('http').createServer(function(q,s){s.end('ok')}).listen(" + port + ",'127.0.0.1')\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (p == null) return "FAIL: spawn returned null";
                bool up = false;
                for (int i = 0; i < 25; i++)
                {
                    Thread.Sleep(200);
                    if (TryGet(port)) { up = true; break; }
                    if (p.HasExited) break;
                }
                try
                {
                    if (!p.HasExited)
                    {
                        ProcessStartInfo k = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                        k.UseShellExecute = false;
                        k.CreateNoWindow = true;
                        Process kp = Process.Start(k);
                        if (kp != null) kp.WaitForExit(3000);
                    }
                }
                catch { }
                return up ? "OK (spawn + wait + kill)" : "FAIL: server did not answer";
            }
            catch (Exception ex)
            {
                return "FAIL: " + ex.Message;
            }
        }

        private static int FreePort()
        {
            try
            {
                System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Loopback, 0);
                l.Start();
                int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
            catch { return 0; }
        }

        private static bool TryGet(int port)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
                req.Method = "GET";
                req.Timeout = 1000;
                req.AllowAutoRedirect = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) { resp.Close(); return true; }
            }
            catch (WebException wex)
            {
                if (wex.Response != null) { wex.Response.Close(); return true; }
                return false;
            }
            catch { return false; }
        }
    }
}
