using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 本机偏好：排序方式、手动顺序、每个软件被勾选的次数与最近时间。
    ///
    /// 存放位置优先「exe 同目录的 settings.json」（绿色/便携，拷走连习惯一起走），
    /// 没有就退回 %LOCALAPPDATA%\ProxyPicker\settings.json。
    /// 换机器时把这个文件一起拷过去，列表顺序和常用习惯就跟着走。
    /// </summary>
    internal static class Settings
    {
        public const string ModeWhitelist = "whitelist";   // 完全接管：不勾的一律直连，订阅原规则整段让位
        public const string ModePatch = "patch";           // 只改勾的：在规则最前面插几行，其余原样保留

        public sealed class Data
        {
            public string SortMode = "smart";                 // smart / manual / name
            public List<string> Order = new List<string>();
            public Dictionary<string, int> Usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> LastUsed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool FirstRun = true;
            public int Tab;
            public string Mode = ModePatch;                   // 默认只改勾的：新手/别人电脑上不破坏原有订阅
            public bool TrayHintShown;                        // 「—」的托盘气泡只弹一次
            public bool UpdateCheck = true;                   // 检查更新总开关（只发一个 GET，不带任何本机信息）
            public long UpdateCheckedAt;                      // 上次检查的 UTC ticks，0 = 从没查过
        }

        static Data _data;
        static readonly object Gate = new object();

        public static Data Current
        {
            get
            {
                lock (Gate)
                {
                    if (_data == null) _data = Load();
                    return _data;
                }
            }
        }

        /// <summary>true = 只改勾选的，其余按订阅原规则走；false = 完全接管，不勾的一律直连。</summary>
        public static bool PatchOnly
        {
            get { return !string.Equals(Current.Mode, ModeWhitelist, StringComparison.OrdinalIgnoreCase); }
        }

        public static void SetMode(string mode)
        {
            lock (Gate)
            {
                Current.Mode = string.Equals(mode, ModeWhitelist, StringComparison.OrdinalIgnoreCase)
                    ? ModeWhitelist : ModePatch;
                Save();
            }
        }

        public static string FilePath
        {
            get
            {
                try
                {
                    string exeDir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        string portable = Path.Combine(exeDir, "settings.json");
                        if (File.Exists(portable)) return portable;
                    }
                }
                catch { }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProxyPicker");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "settings.json");
            }
        }

        public static void Save()
        {
            lock (Gate)
            {
                if (_data == null) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"sortMode\": ").Append(Json.Quote(_data.SortMode)).Append(",\n");
                    sb.Append("  \"mode\": ").Append(Json.Quote(_data.Mode)).Append(",\n");
                    sb.Append("  \"tab\": ").Append(_data.Tab).Append(",\n");
                    sb.Append("  \"firstRun\": ").Append(_data.FirstRun ? "true" : "false").Append(",\n");
                    sb.Append("  \"trayHintShown\": ").Append(_data.TrayHintShown ? "true" : "false").Append(",\n");
                    sb.Append("  \"updateCheck\": ").Append(_data.UpdateCheck ? "true" : "false").Append(",\n");
                    sb.Append("  \"updateCheckedAt\": ").Append(_data.UpdateCheckedAt).Append(",\n");

                    sb.Append("  \"order\": [");
                    for (int i = 0; i < _data.Order.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(Json.Quote(_data.Order[i]));
                    }
                    sb.Append("],\n");

                    sb.Append("  \"usage\": {");
                    int n = 0;
                    foreach (var kv in _data.Usage)
                    {
                        if (n++ > 0) sb.Append(", ");
                        sb.Append(Json.Quote(kv.Key)).Append(": ").Append(kv.Value);
                    }
                    sb.Append("},\n");

                    sb.Append("  \"lastUsed\": {");
                    n = 0;
                    foreach (var kv in _data.LastUsed)
                    {
                        if (n++ > 0) sb.Append(", ");
                        sb.Append(Json.Quote(kv.Key)).Append(": ").Append(kv.Value);
                    }
                    sb.Append("},\n");

                    sb.Append("  \"hidden\": [");
                    n = 0;
                    foreach (var v in _data.Hidden)
                    {
                        if (n++ > 0) sb.Append(", ");
                        sb.Append(Json.Quote(v));
                    }
                    sb.Append("]\n}\n");

                    string path = FilePath;
                    string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(true);
                        }
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
                catch (Exception ex) { CrashLog.Write(ex, "保存偏好失败"); }
            }
        }

        static Data Load()
        {
            var d = new Data();

            string path;
            try { path = FilePath; } catch { return d; }

            // 没有 settings.json = 这台电脑第一次跑（多半是朋友拿到分享包）。
            // 这种场景下默认「只改勾的」，不去动别人订阅里原有的几百条规则。
            if (!File.Exists(path)) return d;

            try
            {
                if (new FileInfo(path).Length > 1024 * 1024)
                    throw new FormatException("偏好文件超过 1 MiB，未加载");
                var root = Json.AsObject(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (root == null) throw new FormatException("偏好必须是 JSON 对象");

                // 只兼容解析成功且缺 mode 的旧对象；损坏文件不能悄悄升级成完全接管。
                string split = Json.Str(root, "mode");
                if (!root.ContainsKey("mode")) d.Mode = ModeWhitelist;
                else if (split == ModePatch || split == ModeWhitelist) d.Mode = split;
                else throw new FormatException("偏好的分流模式无效");

                string mode = Json.Str(root, "sortMode");
                if (mode == "smart" || mode == "manual" || mode == "name") d.SortMode = mode;

                object v;
                if (root.TryGetValue("tab", out v) && v is double)
                    d.Tab = Math.Max(0, Math.Min(3, (int)(double)v));
                if (root.TryGetValue("firstRun", out v) && v is bool) d.FirstRun = (bool)v;
                if (root.TryGetValue("trayHintShown", out v) && v is bool) d.TrayHintShown = (bool)v;
                if (root.TryGetValue("updateCheck", out v) && v is bool) d.UpdateCheck = (bool)v;
                if (root.TryGetValue("updateCheckedAt", out v) && v is double) d.UpdateCheckedAt = (long)(double)v;

                Dictionary<string, object> obj;
                if (root.TryGetValue("order", out v))
                {
                    var arr = Json.AsArray(v);
                    if (arr != null)
                        foreach (var item in arr)
                        {
                            var s = item as string;
                            if (!string.IsNullOrEmpty(s)) d.Order.Add(s);
                        }
                }
                if (root.TryGetValue("usage", out v))
                {
                    obj = Json.AsObject(v);
                    if (obj != null)
                        foreach (var kv in obj)
                            if (kv.Value is double) d.Usage[kv.Key] = (int)(double)kv.Value;
                }
                if (root.TryGetValue("lastUsed", out v))
                {
                    obj = Json.AsObject(v);
                    if (obj != null)
                        foreach (var kv in obj)
                            if (kv.Value is double) d.LastUsed[kv.Key] = (long)(double)kv.Value;
                }
                if (root.TryGetValue("hidden", out v))
                {
                    var arr = Json.AsArray(v);
                    if (arr != null)
                        foreach (var item in arr)
                        {
                            var s = item as string;
                            if (!string.IsNullOrEmpty(s)) d.Hidden.Add(s);
                        }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "读取偏好失败，保留原文件并使用默认值");
                return new Data();
            }
            return d;
        }

        /// <summary>记一次「被勾上」：用于智能排序，越常勾的越靠前。</summary>
        public static void Touch(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            lock (Gate)
            {
                var d = Current;
                int n;
                d.Usage.TryGetValue(exe, out n);
                d.Usage[exe] = n == int.MaxValue ? n : n + 1;
                d.LastUsed[exe] = DateTime.UtcNow.Ticks;
                Save();
            }
        }

        public static void SetOrder(IEnumerable<string> exes)
        {
            lock (Gate)
            {
                var d = Current;
                d.Order.Clear();
                foreach (var e in exes) if (!string.IsNullOrEmpty(e)) d.Order.Add(e);
                d.SortMode = "manual";
                Save();
            }
        }

        public static int UsageOf(string exe)
        {
            lock (Gate)
            {
                int n;
                return Current.Usage.TryGetValue(exe, out n) ? n : 0;
            }
        }

        public static long LastUsedOf(string exe)
        {
            lock (Gate)
            {
                long n;
                return Current.LastUsed.TryGetValue(exe, out n) ? n : 0;
            }
        }

        public static bool HiddenOf(string exe) { lock (Gate) return Current.Hidden.Contains(exe); }

        public static void Hide(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            lock (Gate) { Current.Hidden.Add(exe); Save(); }
        }

        public static void UnhideAll()
        {
            lock (Gate) { Current.Hidden.Clear(); Save(); }
        }

        /// <summary>点「—」时要不要弹一次「窗口去哪了」的气泡。只弹一次，之后用户已经知道了。</summary>
        public static bool TakeTrayHint()
        {
            lock (Gate)
            {
                var d = Current;
                if (d.TrayHintShown) return false;
                d.TrayHintShown = true;
                Save();
                return true;
            }
        }

        /// <summary>
        /// 现在该不该自动查一次新版本。每天最多一次；关掉开关就一次都不查。
        /// 失败也算「查过」，否则离线状态下会每隔几分钟重试一轮，白白拖慢启动。
        /// </summary>
        public static bool ShouldCheckUpdate(double hours)
        {
            lock (Gate)
            {
                var d = Current;
                if (!d.UpdateCheck) return false;
                if (d.UpdateCheckedAt <= 0) return true;
                long elapsed = DateTime.UtcNow.Ticks - d.UpdateCheckedAt;
                return elapsed < 0 || elapsed > TimeSpan.FromHours(hours).Ticks;   // 系统时间被改过就再查一次
            }
        }

        public static void MarkUpdateChecked()
        {
            lock (Gate) { Current.UpdateCheckedAt = DateTime.UtcNow.Ticks; Save(); }
        }
    }
}
