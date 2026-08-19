using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace DshDesktop
{
    /// <summary>
    /// Owns the DSH backend lifecycle: detect an already-running server on the
    /// configured host:port (attach mode), otherwise spawn
    /// `node <dsh lib>/lib/bin.js web --host H --port P` hidden, wait until it
    /// answers HTTP, and on demand kill the process tree.
    /// </summary>
    internal sealed class Backend
    {
        private readonly Config _cfg;
        private Process _proc;
        private bool _startedByUs;
        private string _logPath;

        public Backend(Config cfg)
        {
            _cfg = cfg;
            _logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DshDesktop", "backend.log");
        }

        public bool StartedByUs { get { return _startedByUs; } }
        public string LogPath { get { return _logPath; } }
        public string Url { get { return "http://" + _cfg.Host + ":" + _cfg.Port + "/"; } }

        /// <summary>True when the configured host:port already answers HTTP.</summary>
        public bool IsUp()
        {
            return IsUp(_cfg.Host, _cfg.Port);
        }

        private static bool IsUp(string host, int port)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://" + host + ":" + port + "/");
                req.Method = "GET";
                req.Timeout = 1500;
                req.ReadWriteTimeout = 1500;
                req.AllowAutoRedirect = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    resp.Close();
                    return true;
                }
            }
            catch (WebException wex)
            {
                // Any HTTP answer (even 4xx/5xx) means a server owns the port.
                if (wex.Response != null) { wex.Response.Close(); return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Start the backend if it is not already running, then wait until it
        /// answers. Returns null on success or an error message on failure.
        /// status (optional) receives progress text; may be called from a
        /// background thread.
        /// </summary>
        public string StartAndWait(Action<string> status, int timeoutSeconds)
        {
            string node = Locator.FindNode(_cfg);
            if (node == "") return "未找到 node.exe。可在配置里设置 nodePath。";
            string lib = Locator.FindDshLib(_cfg);
            if (lib == "") return "未找到 dsh 的 lib/bin.js。可在配置里设置 dshLib。";

            string logDir = Path.GetDirectoryName(_logPath);
            try
            {
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            }
            catch { }

            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home)) home = _cfg.DshHome;
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = "\"" + lib + "\" web --host " + _cfg.Host + " --port " + _cfg.Port;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["DSH_HOME"] = home;

            StreamWriter log = null;
            try
            {
                log = new StreamWriter(_logPath, true, Encoding.UTF8);
                log.WriteLine("===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
                log.WriteLine(psi.FileName + " " + psi.Arguments);
                log.WriteLine("DSH_HOME=" + home);
                log.Flush();

                _proc = new Process();
                _proc.StartInfo = psi;
                _proc.EnableRaisingEvents = true;
                _proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { try { log.WriteLine(e.Data); log.Flush(); } catch { } }
                };
                _proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { try { log.WriteLine(e.Data); log.Flush(); } catch { } }
                };
                _proc.Exited += delegate
                {
                    try { log.Flush(); log.Close(); }
                    catch { }
                };
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _startedByUs = true;
            }
            catch (Exception ex)
            {
                if (log != null) { try { log.Close(); } catch { } }
                return "启动后端失败: " + ex.Message;
            }

            int totalMs = timeoutSeconds * 1000;
            int waited = 0;
            while (waited < totalMs)
            {
                if (_proc.HasExited)
                    return "后端进程提前退出，请查看日志: " + _logPath;
                if (IsUp())
                {
                    if (status != null) status("后端已就绪: " + Url);
                    return null;
                }
                Thread.Sleep(400);
                waited += 400;
                if (status != null)
                    status("正在启动 DSH 后端… " + (waited / 1000) + "s / " + timeoutSeconds + "s");
            }
            return "等待后端超时(" + timeoutSeconds + "s)，请查看日志: " + _logPath;
        }

        /// <summary>Kill the backend process tree we started (no-op in attach mode).</summary>
        public void Stop()
        {
            if (!_startedByUs || _proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill",
                        "/PID " + _proc.Id + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process p = Process.Start(psi);
                    if (p != null) p.WaitForExit(5000);
                }
            }
            catch { }
        }
    }
}
