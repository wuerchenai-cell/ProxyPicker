using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 动作账本：程序自己做的每一次「自动补回 / 放弃补回 / 改 IPv6 / 保存」都记一行。
    ///
    /// 为什么要有：自动补回是后台悄悄干的，只弹一个 2 秒的提示，过了就没人知道发生过什么。
    /// 出了问题时最有用的一句话不是「它坏了」，而是「它几点几分改了什么、为什么改」。
    /// 所以这里既在内存留最近若干条给界面和诊断用，也落到磁盘，重启后还能翻。
    ///
    /// 只记录「本程序做了什么」，不记录用户打开了什么软件、访问了什么网站。
    /// </summary>
    internal static class AuditLog
    {
        const int KeepRecent = 40;
        const long MaxBytes = 256 * 1024;
        static readonly object Gate = new object();
        static readonly List<string> _recent = new List<string>();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string FilePath
        {
            get { return Path.Combine(Paths.OwnDir, "actions.log"); }
        }

        /// <summary>记一条。kind 用大写短词：REPAIR / FAIL / IPV6 / SAVE / WATCH。</summary>
        public static void Record(string kind, string text)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] "
                          + (kind ?? "INFO") + " " + (text ?? "");
            // 记之前先脱敏：失败原因里常带本机路径和用户名，而这个账本是要给人看的
            // （--audit 会把它贴出来，崩溃日志那边也是脱敏的，两边保持一致）。
            line = Redactor.Apply(line);
            lock (Gate)
            {
                _recent.Add(line);
                while (_recent.Count > KeepRecent) _recent.RemoveAt(0);
            }
            try { Append(line); } catch { } // 账本写不进去也不能影响主流程
        }

        /// <summary>最近 n 行，按时间正序，每行带换行；没有就返回空串。</summary>
        public static string Recent(int n)
        {
            lock (Gate)
            {
                if (_recent.Count == 0) return "";
                int from = Math.Max(0, _recent.Count - Math.Max(1, n));
                var sb = new StringBuilder();
                for (int i = from; i < _recent.Count; i++) sb.AppendLine(_recent[i]);
                return sb.ToString();
            }
        }

        public static int Count { get { lock (Gate) return _recent.Count; } }

        static void Append(string line)
        {
            string path = FilePath;
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                if (File.Exists(path + ".1")) File.Delete(path + ".1");
                File.Move(path, path + ".1");
            }
            File.AppendAllText(path, line + "\r\n", Utf8);
        }
    }
}
