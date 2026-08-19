using System;
using System.Collections.Generic;
using System.IO;

namespace DshDesktop
{
    /// <summary>
    /// Offline path discovery: node.exe, the dsh CLI entry (lib/bin.js), and Edge.
    /// Everything is resolved from the filesystem / PATH / config — no network.
    /// </summary>
    internal static class Locator
    {
        /// <summary>Find node.exe: config first, then PATH, then common install dirs.</summary>
        public static string FindNode(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.NodePath) && File.Exists(cfg.NodePath)) return cfg.NodePath;

            string found = FindOnPath("node.exe");
            if (found != "") return found;

            string[] common = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
                @"C:\nodejs\node.exe",
            };
            foreach (string f in common)
            {
                try { if (File.Exists(f)) return f; }
                catch { }
            }
            return "";
        }

        /// <summary>
        /// Find the dsh CLI entry file lib/bin.js.
        /// Resolution order: config dshLib (file, package dir, or node_modules root),
        /// npx caches under _npx, then the dsh.cmd shim on PATH.
        /// </summary>
        public static string FindDshLib(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.DshLib))
            {
                string t = cfg.DshLib;
                try
                {
                    if (File.Exists(t)) return t;
                    string guess = Path.Combine(t, "lib", "bin.js");
                    if (File.Exists(guess)) return guess;
                    guess = Path.Combine(t, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(guess)) return guess;
                }
                catch { }
            }

            // Portable npx cache roots: standard environment locations plus the
            // cache path declared in the user's .npmrc (custom npm cache dirs).
            List<string> npxRoots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache", "_npx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm", "_npx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "npm-cache", "_npx")
            };
            string npmCache = GetNpmCacheFromNpmrc();
            if (npmCache != "")
                npxRoots.Add(Path.Combine(npmCache, "_npx"));
            foreach (string root in npxRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    string[] dirs = Directory.GetDirectories(root);
                    foreach (string sub in dirs)
                    {
                        string cand = Path.Combine(sub, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(cand)) return cand;
                    }
                }
                catch { }
            }

            string shim = FindOnPath("dsh.cmd");
            if (shim != "")
            {
                string viaShim = ResolveDshFromCmdShim(shim);
                if (viaShim != "") return viaShim;
            }
            string shim2 = FindOnPath("dsh.ps1");
            if (shim2 != "")
            {
                string viaShim = ResolveDshFromCmdShim(shim2);
                if (viaShim != "") return viaShim;
            }
            return "";
        }

        /// <summary>Parse an npm ".bin" shim to recover the real lib/bin.js path.</summary>
        private static string ResolveDshFromCmdShim(string shimPath)
        {
            try
            {
                string text = File.ReadAllText(shimPath);
                int i = text.IndexOf("@deepseek-ai\\dsh\\lib\\bin.js");
                string marker = "@deepseek-ai\\dsh\\lib\\bin.js";
                if (i < 0) { marker = "@deepseek-ai/dsh/lib/bin.js"; i = text.IndexOf(marker); }
                if (i < 0) return "";

                int j = text.LastIndexOf("%~dp0", i);
                if (j < 0) j = text.LastIndexOf("%dp0%", i);
                if (j < 0) return "";

                int k = j;
                while (k < text.Length && text[k] != '%') k++;
                if (k >= text.Length) return "";
                k++; // skip '%'

                string shimDir = Path.GetDirectoryName(shimPath);
                string rest = text.Substring(k).Replace('/', '\\');
                int e = 0;
                while (e < rest.Length && rest[e] != '"' && rest[e] != ' ' && rest[e] != '%'
                    && rest[e] != '\r' && rest[e] != '\n') e++;
                rest = rest.Substring(0, e);
                if (rest.Length == 0) return "";

                string full = Path.GetFullPath(Path.Combine(shimDir, rest));
                if (File.Exists(full)) return full;
            }
            catch { }
            return "";
        }

        /// <summary>Find msedge.exe for the --app fallback shell.</summary>
        public static string FindEdge()
        {
            string[] cands = new string[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            };
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; }
                catch { }
            }
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("");
                        if (v != null && File.Exists(v.ToString())) return v.ToString();
                    }
                }
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("");
                        if (v != null && File.Exists(v.ToString())) return v.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Read the npm cache directory from the user's .npmrc (the "cache=..."
        /// key), so a custom npm cache location is honored on any machine.
        /// </summary>
        private static string GetNpmCacheFromNpmrc()
        {
            try
            {
                string npmrc = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
                if (!File.Exists(npmrc)) return "";
                foreach (string raw in File.ReadAllLines(npmrc))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    if (key != "cache") continue;
                    string val = line.Substring(eq + 1).Trim();
                    if (val.Length > 0) return val;
                }
            }
            catch { }
            return "";
        }

        private static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return "";
            string[] parts = path.Split(';');
            foreach (string dirRaw in parts)
            {
                string dir = dirRaw.Trim();
                if (dir.Length == 0) continue;
                try
                {
                    string f = Path.Combine(dir, fileName);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return "";
        }
    }
}
