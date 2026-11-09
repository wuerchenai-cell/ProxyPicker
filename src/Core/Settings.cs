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
        // 代理白名单（核心模式）：只有勾选的软件走代理，未勾选的一律直连。
        public const string ModeWhitelist = "whitelist";
        // 订阅兼容（高级模式）：只在规则最前面插勾选项，未勾选的继续走订阅原有规则。
        public const string ModePatch = "patch";

        public sealed class Data
        {
            public string SortMode = "smart";                 // smart / manual / name
            public List<string> Order = new List<string>();
            public Dictionary<string, int> Usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> LastUsed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool FirstRun = true;
            public int Tab;
            // 全新用户的默认模式：只有点名的软件走代理，其余直连 —— 这才是产品定位。
            // 已有 settings.json 的用户不会被这个默认值影响，见 Load() 里的老配置兼容分支。
            public string Mode = ModeWhitelist;
            public bool TrayHintShown;                        // 「—」的托盘气泡只弹一次
            public bool UpdateCheck = true;                   // 检查更新总开关（只发一个 GET，不带任何本机信息）
            public long UpdateCheckedAt;                      // 上次检查的 UTC ticks，0 = 从没查过
            // IPv6 防泄漏是「用户主动开启」的高级选项，默认关。
            // 原因：Clash Verge 新版把 ipv6 收归应用设置接管，扩展配置/推送全文里写它
            // 会被丢弃并弹橙色警告；更不该在用户只想给微信设直连时顺手改全局。
            public bool Ipv6Guard;
            // 开启前记下的内核原值："" 未知 / "true" / "false" / "absent"。
            // 关闭开关时按它把配置推回去，不把用户的原设置永久改掉。
            public string Ipv6Original = "";
            // 第一次保存前记下的 find-process-mode："" 未知 / "always" / "smart" / "strict" / "off" / "absent"。
            // 退出还原必须恢复成用户原值，不能把 always 永久留下（复审 F-04）。
            public string FindProcessOriginal = "";
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
            }
            Save();
        }

        public static bool Ipv6GuardOn { get { lock (Gate) return Current.Ipv6Guard; } }

        /// <summary>用户主动开关「IPv6 防泄漏」。开启前先记下内核原值，关掉时才能推回去。</summary>
        public static void SetIpv6Guard(bool on)
        {
            // 网络请求放在锁外：内核不响应时最多等 4 秒，不能把保存线程一起卡住（评审 N1）。
            if (on) CaptureIpv6Original();
            lock (Gate) { Current.Ipv6Guard = on; }
            if (!Save()) throw new InvalidOperationException("保存 IPv6 设置失败：" + LastSaveError);
        }

        /// <summary>把「改之前」的 IPv6 状态记一次，之后不再覆盖；读不到就留空，不猜。</summary>
        public static void CaptureIpv6Original()
        {
            lock (Gate) { if (!string.IsNullOrEmpty(Current.Ipv6Original)) return; }
            string value;
            try
            {
                var cfg = Mihomo.GetJson("/configs", 4000);
                value = cfg == null ? "" : (Json.Bool(cfg, "ipv6", false) ? "true" : "false");
            }
            catch { value = ""; }
            lock (Gate)
            {
                var d = Current;
                if (!string.IsNullOrEmpty(d.Ipv6Original)) return;
                d.Ipv6Original = value;
            }
            Save();
        }

        /// <summary>
        /// 记下第一次保存前的 find-process-mode。读不到就留空等下次重试，
        /// 绝不能把「读失败」记成 absent —— 那会在退出时删掉用户本来有的设置。
        /// </summary>
        public static void CaptureFindProcessOriginal()
        {
            lock (Gate) { if (!string.IsNullOrEmpty(Current.FindProcessOriginal)) return; }
            string value = null;
            try
            {
                var cfg = Mihomo.GetJson("/configs", 4000);
                if (cfg != null)
                {
                    string v = Json.Str(cfg, "find-process-mode");
                    value = string.IsNullOrEmpty(v) ? "absent" : v;
                }
            }
            catch { value = null; }
            if (value == null) return;
            lock (Gate)
            {
                var d = Current;
                if (!string.IsNullOrEmpty(d.FindProcessOriginal)) return;
                d.FindProcessOriginal = value;
            }
            Save();
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

        public static string LastSaveError = "";

        /// <summary>
        /// 锁内先取一份不可变快照再序列化；UI 同时改排序/集合时不会写坏文件（评审 R6-9）。
        /// 返回是否真的写成功，调用方可以据此提示失败，而不是假装成功。
        /// </summary>
        public static bool Save()
        {
            lock (Gate)
            {
                if (_data == null) return true;
                string temp = null;
                try
                {
                    var d = _data;
                    string sortMode = d.SortMode, mode = d.Mode, ipv6Original = d.Ipv6Original ?? "";
                    string findProcessOriginal = d.FindProcessOriginal ?? "";
                    int tab = d.Tab;
                    long checkedAt = d.UpdateCheckedAt;
                    bool firstRun = d.FirstRun, trayHint = d.TrayHintShown,
                         updateCheck = d.UpdateCheck, ipv6Guard = d.Ipv6Guard;
                    var order = new List<string>(d.Order);
                    var usage = new Dictionary<string, int>(d.Usage, StringComparer.OrdinalIgnoreCase);
                    var lastUsed = new Dictionary<string, long>(d.LastUsed, StringComparer.OrdinalIgnoreCase);
                    var hidden = new List<string>(d.Hidden);

                    var sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"sortMode\": ").Append(Json.Quote(sortMode)).Append(",\n");
                    sb.Append("  \"mode\": ").Append(Json.Quote(mode)).Append(",\n");
                    sb.Append("  \"tab\": ").Append(tab).Append(",\n");
                    sb.Append("  \"firstRun\": ").Append(firstRun ? "true" : "false").Append(",\n");
                    sb.Append("  \"trayHintShown\": ").Append(trayHint ? "true" : "false").Append(",\n");
                    sb.Append("  \"updateCheck\": ").Append(updateCheck ? "true" : "false").Append(",\n");
                    sb.Append("  \"updateCheckedAt\": ").Append(checkedAt).Append(",\n");
                    sb.Append("  \"ipv6Guard\": ").Append(ipv6Guard ? "true" : "false").Append(",\n");
                    sb.Append("  \"ipv6Original\": ").Append(Json.Quote(ipv6Original)).Append(",\n");
                    sb.Append("  \"findProcessOriginal\": ").Append(Json.Quote(findProcessOriginal)).Append(",\n");

                    sb.Append("  \"order\": [");
                    for (int i = 0; i < order.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(Json.Quote(order[i]));
                    }
                    sb.Append("],\n");

                    sb.Append("  \"usage\": {");
                    int n = 0;
                    foreach (var kv in usage)
                    {
                        if (n++ > 0) sb.Append(", ");
                        sb.Append(Json.Quote(kv.Key)).Append(": ").Append(kv.Value);
                    }
                    sb.Append("},\n");

                    sb.Append("  \"lastUsed\": {");
                    n = 0;
                    foreach (var kv in lastUsed)
                    {
                        if (n++ > 0) sb.Append(", ");
                        sb.Append(Json.Quote(kv.Key)).Append(": ").Append(kv.Value);
                    }
                    sb.Append("},\n");

                    sb.Append("  \"hidden\": [");
                    n = 0;
                    foreach (var v in hidden)
                    {
                        if (n++ > 0) sb.Append(", ");
                        sb.Append(Json.Quote(v));
                    }
                    sb.Append("]\n}\n");

                    string path = FilePath;
                    temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    LastSaveError = "";
                    return true;
                }
                catch (Exception ex)
                {
                    LastSaveError = ex.Message;
                    CrashLog.Write(ex, "保存偏好失败");
                    return false;
                }
                finally
                {
                    if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } }
                }
            }
        }

        static Data Load()
        {
            var d = new Data();

            string path;
            try { path = FilePath; } catch { return d; }

            // 没有 settings.json = 全新用户（多半是朋友刚拿到分享包），
            // 用产品定位的默认模式：代理白名单，只有点名的软件走代理。
            if (!File.Exists(path)) return d;

            try
            {
                if (new FileInfo(path).Length > 1024 * 1024)
                    throw new FormatException("偏好文件超过 1 MiB，未加载");
                var root = Json.AsObject(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (root == null) throw new FormatException("偏好必须是 JSON 对象");

                // 已有配置文件才是「老用户」：缺 mode 字段是 v1.0.x 以前留下的配置，
                // 那时默认就是订阅兼容，必须原样保持，绝不因升级悄悄改成代理白名单。
                // 损坏文件也不能悄悄升级成完全接管 —— 直接判失败走下面的保守回退。
                string split = Json.Str(root, "mode");
                if (!root.ContainsKey("mode")) d.Mode = ModePatch;
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
                if (root.TryGetValue("ipv6Guard", out v) && v is bool) d.Ipv6Guard = (bool)v;
                string ipv6Orig = Json.Str(root, "ipv6Original");
                if (ipv6Orig == "true" || ipv6Orig == "false" || ipv6Orig == "absent") d.Ipv6Original = ipv6Orig;
                string fpmOrig = Json.Str(root, "findProcessOriginal");
                if (fpmOrig == "always" || fpmOrig == "smart" || fpmOrig == "strict" ||
                    fpmOrig == "off" || fpmOrig == "absent") d.FindProcessOriginal = fpmOrig;

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
                CrashLog.Write(ex, "读取偏好失败，保留原文件并使用保守默认值");
                // 文件读坏了不能当成全新用户：那会悄悄切成代理白名单，
                // 把用户原本所有未点名的软件全改成直连。保守回退到订阅兼容。
                return new Data { Mode = ModePatch };
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
