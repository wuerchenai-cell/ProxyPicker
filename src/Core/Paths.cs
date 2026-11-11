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
        /// 仅自检使用的名单路径注入点：把 HasSavedIntent 指向一个临时文件，
        /// 让「名单已删除」的端到端断言能在不碰真机名单的前提下真跑一次 Apply。
        /// 生产路径下恒为 null；自检在 try/finally 里临时设置并恢复（收尾批 F-03）。
        /// </summary>
        internal static string ListPathOverride;

        /// <summary>
        /// 勾选名单固定放本程序自己的目录：换客户端 / 重装不会丢（评审 R5-3）。
        /// 只读命令不再在客户端数据目录里创建 pp_rules\（评审 R5-6）。
        /// 老版本把名单写在客户端目录下，这里做一次性迁移，绝不覆盖已有的新名单。
        /// 真正的生效不依赖这个文件——规则是内联进 payload 推送的，它只是账本和还原依据。
        /// </summary>
        public static string ListPath
        {
            get
            {
                return !string.IsNullOrEmpty(ListPathOverride)
                    ? ListPathOverride
                    : Path.Combine(OwnDir, "pp-whitelist.yaml");
            }
        }

        /// <summary>
        /// 老版本把名单写在客户端目录下，这里做一次性迁移，绝不覆盖已有新名单。
        /// 只有 GUI / --apply 会调用；--list / --scan 这类只读命令不能因为读路径就写盘。
        /// </summary>
        public static void MigrateLegacyList()
        {
            string own = ListPath;
            try
            {
                if (File.Exists(own)) return;
                string dir = ClashDir;
                if (string.IsNullOrEmpty(dir)) return;
                string legacy = Path.Combine(dir, "pp_rules", "pp-whitelist.yaml");
                if (File.Exists(legacy)) File.Copy(legacy, own, false);
            }
            catch { }
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
