using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DshDesktop
{
    /// <summary>
    /// User configuration, stored as JSON under %APPDATA%\DshDesktop\config.json.
    /// A default template is written on first run. Edit it and restart the app.
    /// </summary>
    internal sealed class Config
    {
        // Fields (defaults). All optional; the app auto-detects node/dsh when blank.
        public string NodePath = "";
        public string DshLib = "";
        public string DshHome = "";
        public string Host = "127.0.0.1";
        public int Port = 3080;
        public int StartTimeoutSeconds = 90;
        public bool KillBackendOnExit = true;
        public bool EdgeFallback = true;
        public string Title = "DeepSeek Harness";
        public string UserDataFolder = "";

        private static Config _instance;

        public static Config Instance
        {
            get
            {
                if (_instance == null) _instance = Load();
                return _instance;
            }
        }

        public static string ConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DshDesktop", "config.json");
        }

        public string EffectiveUserDataFolder()
        {
            if (!string.IsNullOrEmpty(UserDataFolder)) return UserDataFolder;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DshDesktop", "WebView2");
        }

        public static Config Load()
        {
            Config c = new Config();
            string path = ConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    object raw = ser.DeserializeObject(json);
                    Dictionary<string, object> d = raw as Dictionary<string, object>;
                    if (d != null)
                    {
                        c.NodePath = Str(d, "nodePath", c.NodePath);
                        c.DshLib = Str(d, "dshLib", c.DshLib);
                        c.DshHome = Str(d, "dshHome", c.DshHome);
                        c.Host = Str(d, "host", c.Host);
                        c.Port = Int(d, "port", c.Port);
                        c.StartTimeoutSeconds = Int(d, "startTimeoutSeconds", c.StartTimeoutSeconds);
                        c.KillBackendOnExit = Bool(d, "killBackendOnExit", c.KillBackendOnExit);
                        c.EdgeFallback = Bool(d, "edgeFallback", c.EdgeFallback);
                        c.Title = Str(d, "title", c.Title);
                        c.UserDataFolder = Str(d, "userDataFolder", c.UserDataFolder);
                    }
                }
            }
            catch
            {
                // Corrupt config falls back to defaults.
            }

            // Ensure the directory exists and seed a template on first run.
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path)) File.WriteAllText(path, DefaultJson(), Encoding.UTF8);
            }
            catch { }

            return c;
        }

        private static string DefaultJson()
        {
            return "{"
                + "\n  \"comment\": \"DSH desktop shell settings. Restart the app after editing. Do NOT add // comments.\","
                + "\n  \"nodePath\": \"\","
                + "\n  \"dshLib\": \"\","
                + "\n  \"dshHome\": \"\","
                + "\n  \"host\": \"127.0.0.1\","
                + "\n  \"port\": 3080,"
                + "\n  \"startTimeoutSeconds\": 90,"
                + "\n  \"killBackendOnExit\": true,"
                + "\n  \"edgeFallback\": true,"
                + "\n  \"title\": \"DeepSeek Harness\","
                + "\n  \"userDataFolder\": \"\""
                + "\n}";
        }

        private static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return v.ToString();
            return def;
        }

        private static int Int(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d.TryGetValue(key, out v))
            {
                try { return Convert.ToInt32(v); }
                catch { }
            }
            return def;
        }

        private static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d.TryGetValue(key, out v))
            {
                try { return Convert.ToBoolean(v); }
                catch { }
            }
            return def;
        }
    }
}
