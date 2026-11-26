using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Platform.Windows
{
    /// <summary>COM IShellLinkW 桌面原生图标快捷方式生成器。</summary>
    public sealed class NativeShortcutService : IShortcutCreator
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCom { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static string BuildShortcutFileName(string appDisplayName, string targetExePath)
        {
            string baseName = !string.IsNullOrWhiteSpace(appDisplayName)
                ? appDisplayName.Trim()
                : Path.GetFileNameWithoutExtension(targetExePath ?? "App");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "App";
            foreach (char c in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(c, '_');
            baseName = baseName.Trim().TrimEnd('.');
            if (baseName.Length > 48) baseName = baseName.Substring(0, 48).Trim();
            return baseName + " (联动分流).lnk";
        }

        public bool CreateCompanionShortcut(
            string targetAppPath, string appDisplayName, string shortcutDir,
            string proxyPickerExePath, out string createdPath, out string error)
        {
            createdPath = "";
            error = "";
            if (!CompanionPolicyEngine.ValidateTargetExeOrPath(targetAppPath, true, out error)) return false;
            string cleanTarget = targetAppPath.Trim().Trim('"');
            if (!File.Exists(cleanTarget)) { error = "目标软件文件不存在"; return false; }
            string selfExe = (proxyPickerExePath ?? "").Trim().Trim('"');
            if (!File.Exists(selfExe)) { error = "找不到当前 ProxyPicker 程序路径"; return false; }

            string dir = string.IsNullOrWhiteSpace(shortcutDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : shortcutDir;
            try
            {
                Directory.CreateDirectory(dir);
                string lnkPath = Path.Combine(dir, BuildShortcutFileName(appDisplayName, cleanTarget));
                var link = (IShellLinkW)new ShellLinkCom();
                try
                {
                    link.SetPath(selfExe);
                    link.SetArguments("--follow \"" + cleanTarget + "\"");
                    link.SetWorkingDirectory(Path.GetDirectoryName(selfExe) ?? "");
                    link.SetIconLocation(cleanTarget, 0);
                    link.SetDescription("同步启停 " + (string.IsNullOrWhiteSpace(appDisplayName) ? Path.GetFileNameWithoutExtension(cleanTarget) : appDisplayName.Trim()) + " 与 ProxyPicker 分流");
                    ((IPersistFile)link).Save(lnkPath, false);
                }
                finally { try { Marshal.FinalReleaseComObject(link); } catch { } }
                createdPath = lnkPath;
                return File.Exists(lnkPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>HKCU\...\Run 开机自启、守望自启与跨进程事件交接管理（零管理员权限）。</summary>
    public sealed class WindowsAutostartStore : IAutostartStore
    {
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string MainRunName = "ProxyPicker";
        public const string WatchRunName = "ProxyPickerCompanion";

        public const string ShowEventName = @"Local\ProxyPicker.ShowPanel";
        public const string WatchStopEventName = @"Local\ProxyPicker.StopCompanionWatch";
        public const string WatchReloadEventName = @"Local\ProxyPicker.ReloadCompanionWatch";
        public const string ManualExitEventName = @"Local\ProxyPicker.ManualExitNotice";
        public const string CompanionHandoffEventName = @"Local\ProxyPicker.CompanionHandoff";

        readonly string _ownDataDir;

        public WindowsAutostartStore(string ownDataDir)
        {
            _ownDataDir = ownDataDir;
        }

        string HandoffFilePath => Path.Combine(_ownDataDir, "companion_handoff.tmp");

        public bool IsMainAutostartEnabled(string currentExePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k != null && CompanionPolicyEngine.CommandPointsToExe(Convert.ToString(k.GetValue(MainRunName)), currentExePath);
            }
            catch { return false; }
        }

        public bool SetMainAutostart(bool enable, string currentExePath)
        {
            try
            {
                using (var k = enable ? Registry.CurrentUser.CreateSubKey(RunKeyPath) : Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return !enable;
                    if (enable) k.SetValue(MainRunName, "\"" + currentExePath + "\" --tray");
                    else
                    {
                        object v = k.GetValue(MainRunName);
                        if (v == null) return true;
                        if (!CompanionPolicyEngine.CommandPointsToExe(Convert.ToString(v), currentExePath)) return false;
                        k.DeleteValue(MainRunName, false);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        public bool IsWatcherAutostartEnabled(string currentExePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k != null && CompanionPolicyEngine.CommandPointsToExe(Convert.ToString(k.GetValue(WatchRunName)), currentExePath);
            }
            catch { return false; }
        }

        public bool SetWatcherAutostart(bool enable, string currentExePath)
        {
            try
            {
                using (var k = enable ? Registry.CurrentUser.CreateSubKey(RunKeyPath) : Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return !enable;
                    if (enable) k.SetValue(WatchRunName, "\"" + currentExePath + "\" --watch-companion");
                    else
                    {
                        object v = k.GetValue(WatchRunName);
                        if (v == null) return true;
                        if (CompanionPolicyEngine.IsOwnedWatchCommand(Convert.ToString(v), currentExePath))
                            k.DeleteValue(WatchRunName, false);
                        else
                            return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        public void SignalStopWatcher() => TrySetNamedEvent(WatchStopEventName);
        public void SignalReloadWatcher() => TrySetNamedEvent(WatchReloadEventName);
        public void NotifyManualExitToWatcher() => TrySetNamedEvent(ManualExitEventName);

        public bool PostFollowHandoffToMain(string targetPathOrExe)
        {
            string clean = (targetPathOrExe ?? "").Trim().Trim('"');
            if (clean.Length == 0) return false;
            try
            {
                Directory.CreateDirectory(_ownDataDir);
                string tmp = HandoffFilePath + "." + Process.GetCurrentProcess().Id;
                File.WriteAllText(tmp, clean, new UTF8Encoding(false));
                if (File.Exists(HandoffFilePath))
                {
                    try { File.Replace(tmp, HandoffFilePath, null); }
                    catch { File.Copy(tmp, HandoffFilePath, true); try { File.Delete(tmp); } catch { } }
                }
                else File.Move(tmp, HandoffFilePath);
                return TrySetNamedEvent(CompanionHandoffEventName);
            }
            catch { return false; }
        }

        public string ConsumeFollowHandoff()
        {
            try
            {
                if (!File.Exists(HandoffFilePath)) return "";
                string txt = File.ReadAllText(HandoffFilePath, Encoding.UTF8).Trim().Trim('"');
                try { File.Delete(HandoffFilePath); } catch { }
                return txt;
            }
            catch { return ""; }
        }

        public static bool TrySetNamedEvent(string eventName)
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(eventName))
                {
                    ev.Set();
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
