using System;
using System.IO;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 路径约定：不再写死某一个客户端，全部交给 Discovery 现场探测。
    /// 这样同一个 exe 换到 Nyanpasu / Mihomo Party / 裸 mihomo 的机器上也能用。
    /// </summary>
    internal static class Paths
    {
        /// <summary>当前客户端的用户数据目录（找不到时为空）。</summary>
        public static string ClashDir
        {
            get { return Discovery.Detect().DataDir ?? ""; }
        }

        /// <summary>当前客户端的运行期配置（生成 payload 的 baseline）。</summary>
        public static string ClashYaml
        {
            get { return Discovery.Detect().ConfigPath ?? ""; }
        }

        /// <summary>
        /// 勾选名单文件。优先放在客户端数据目录的 pp_rules\ 下（方便人工核对）；
        /// 目录不可写时退回本程序的 LocalAppData。真正的生效不依赖这个文件——
        /// 规则是内联进 payload 推送的，这份文件只是可读的账本和还原依据。
        /// </summary>
        public static string ListPath
        {
            get
            {
                string dir = ClashDir;
                if (!string.IsNullOrEmpty(dir))
                {
                    try
                    {
                        string target = Path.Combine(dir, "pp_rules");
                        Directory.CreateDirectory(target);
                        return Path.Combine(target, "pp-whitelist.yaml");
                    }
                    catch { }
                }
                return Path.Combine(OwnDir, "pp-whitelist.yaml");
            }
        }

        public static string OwnDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProxyPicker");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>
        /// 日志落在 LocalAppData（和 settings.json 同一个目录），不落 exe 旁边。
        /// 早先放在 exe 同目录，结果桌面那份 exe 一崩就在桌面上冒出一个 log 文件——
        /// 用户看到的第一反应是「这又是什么」。日志属于程序自己的痕迹，不该出现在用户桌面。
        /// exe 目录保留为写入失败时的退路（见 CrashLog）。
        /// </summary>
        public static string LogPath
        {
            get { return Path.Combine(OwnDir, "proxypicker.log"); }
        }

        /// <summary>改动前的原件备份目录：回滚用。</summary>
        public static string BackupDir
        {
            get
            {
                string dir = Path.Combine(OwnDir, "backup");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }
    }
}
