using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyPicker.Application;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;
using ProxyPicker.Infrastructure.Mihomo;
using ProxyPicker.Infrastructure.Storage;
using ProxyPicker.Platform.Windows;

namespace ProxyPicker.Tests
{
    internal sealed class MockProbe : IProcessProbe
    {
        public readonly List<string> ClosedOrder = new List<string>();
        public ProcessProbeState NextState = ProcessProbeState.Running;
        public bool WatcherAlive = false;
        public bool MainAlive = false;

        public ProcessProbeState ProbeTarget(string exeOrPath, bool requireVisibleWindowForMultiProc) => NextState;
        public bool IsMainInstanceRunning() => MainAlive;
        public bool IsWatcherRunning() => WatcherAlive;
        public Dictionary<string, RunningProcessInfo> ScanRunningProcesses() => new Dictionary<string, RunningProcessInfo>();
        public bool TryCloseGracefully(string exeOrPath, int timeoutMs, out string detail)
        {
            ClosedOrder.Add(CompanionPolicyEngine.ExtractExeName(exeOrPath));
            detail = "ok";
            return true;
        }
    }

    internal sealed class MockLauncher : IAppLauncher
    {
        public readonly List<string> Launched = new List<string>();
        public bool TryLaunch(string fullPath, string arguments, bool useShellExecute, out bool alreadyRunning, out string error)
        {
            alreadyRunning = false;
            error = "";
            Launched.Add(fullPath + (string.IsNullOrEmpty(arguments) ? "" : (" " + arguments)));
            return true;
        }
    }

    internal sealed class MockAutostart : IAutostartStore
    {
        public bool WatcherEnabled;
        public bool ManualExitNotified;
        public bool StopSignaled;
        public bool IsMainAutostartEnabled(string currentExePath) => false;
        public bool SetMainAutostart(bool enable, string currentExePath) => true;
        public bool IsWatcherAutostartEnabled(string currentExePath) => WatcherEnabled;
        public bool SetWatcherAutostart(bool enable, string currentExePath) { WatcherEnabled = enable; return true; }
        public void SignalStopWatcher() => StopSignaled = true;
        public void SignalReloadWatcher() { }
        public void NotifyManualExitToWatcher() => ManualExitNotified = true;
        public bool PostFollowHandoffToMain(string targetPathOrExe) => true;
        public string ConsumeFollowHandoff() => "";
    }

    public sealed class CompanionSecurityAndMigrationTests
    {
        [TestFact]
        public async Task FourWayCompanion_ShutdownMustRestoreKernelBeforeClosingAppsAndProxyClientLast()
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "pp_v3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var redactor = new RedactorService();
                var audit = new FileAuditSink(tmpDir, redactor, suppressDiskWrite: true);
                var repo = new FileConfigRepository(tmpDir, redactor, audit);
                var probe = new MockProbe();
                var launcher = new MockLauncher();
                var autostart = new MockAutostart();
                var clock = new FakeClock();

                using (var coord = new KernelCoordinator())
                using (var pg = new PowerResumeGuard(null, clock))
                {
                    var orch = new CompanionOrchestrator(probe, launcher, autostart, repo, audit, coord, pg, @"C:\Apps\ProxyPicker.exe");
                    var cfg = new CompanionConfig
                    {
                        FollowEnabled = true,
                        FollowAppExe = "clash-verge.exe",
                        FollowAppPath = @"D:\Clash Verge\clash-verge.exe",
                        FollowAutoExit = true,
                        FollowWatchAtLogon = true,
                        LaunchAppsEnabled = true,
                        LaunchAppPaths = new List<string> { @"D:\Clash Verge\clash-verge.exe", @"C:\Apps\chrome.exe" },
                        CloseLinkedAppsOnExit = true
                    };

                    var trace = new List<string>();
                    var outcome = await orch.ExecuteShutdownAsync(cfg, triggeredByHostExit: false, ct =>
                    {
                        trace.Add("KERNEL_RESTORED");
                        return new KernelOpResult { Ok = true, Message = "clean" };
                    });

                    AssertEx.True(outcome.KernelRestored);
                    AssertEx.True(autostart.ManualExitNotified);
                    AssertEx.Equal(1, trace.Count);
                    // 方向 4 严格顺序验证：内核已还原后，先优雅关浏览器，最后优雅关 Clash Verge！
                    AssertEx.Equal(2, probe.ClosedOrder.Count);
                    AssertEx.Equal("chrome.exe", probe.ClosedOrder[0]);
                    AssertEx.Equal("clash-verge.exe", probe.ClosedOrder[1]);

                    // 重入保护：第二次调用应直接短路返回 AlreadyInProgress
                    var second = await orch.ExecuteShutdownAsync(cfg, triggeredByHostExit: true, ct => new KernelOpResult { Ok = true });
                    AssertEx.True(second.AlreadyInProgress);
                }
            }
            finally { try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { } }
        }

        [TestFact]
        public async Task FourWayCompanion_BootGateMustBlockBusinessAppWhenKernelNotReady()
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "pp_v3_gate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var redactor = new RedactorService();
                var audit = new FileAuditSink(tmpDir, redactor, suppressDiskWrite: true);
                var repo = new FileConfigRepository(tmpDir, redactor, audit);
                string err;
                repo.SaveRouteRules(new List<AppRouteRule> { new AppRouteRule { Exe = "chrome.exe", Channel = RouteChannel.PrimaryProxy } }, out err);

                var probe = new MockProbe();
                var launcher = new MockLauncher();
                var autostart = new MockAutostart();
                var clock = new FakeClock();
                using (var coord = new KernelCoordinator())
                using (var pg = new PowerResumeGuard(null, clock))
                {
                    var orch = new CompanionOrchestrator(probe, launcher, autostart, repo, audit, coord, pg, @"C:\Apps\ProxyPicker.exe");
                    var cfg = new CompanionConfig
                    {
                        LaunchAppsEnabled = true,
                        LaunchAppPaths = new List<string> { @"D:\Clash Verge\clash-verge.exe", @"C:\Apps\chrome.exe" }
                    };
                    var rep = await orch.RunBootLaunchAsync(cfg, false,
                        ct => Task.FromResult(false), // 模拟内核超时未就绪
                        ct => Task.FromResult(false),
                        CancellationToken.None);

                    AssertEx.True(rep.BusinessAppsBlockedByGate);
                    AssertEx.Equal(0, rep.StartedBusinessApps.Count);
                    AssertEx.Equal(1, rep.StartedClients.Count);
                }
            }
            finally { try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { } }
        }

        [TestFact]
        public void CompanionPolicy_RegistryExactMatchAndRetryCooldownAndPathMatching()
        {
            AssertEx.True(CompanionPolicyEngine.CommandPointsToExe("\"C:\\App\\ProxyPicker.exe\" --watch-companion", @"C:\App\ProxyPicker.exe"));
            AssertEx.False(CompanionPolicyEngine.CommandPointsToExe("\"C:\\App\\ProxyPicker.exe.bak\" --watch-companion", @"C:\App\ProxyPicker.exe"));
            AssertEx.True(CompanionPolicyEngine.IsOwnedWatchCommand("\"D:\\Old\\ProxyPicker.exe\" --watch-companion", @"C:\New\ProxyPicker.exe"));
            AssertEx.False(CompanionPolicyEngine.MatchesTargetProcessPath(@"D:\Clash Verge\clash-verge.exe", @"C:\Other\clash-verge.exe"));
            AssertEx.True(CompanionPolicyEngine.MatchesTargetProcessPath(@"D:\Clash Verge\clash-verge.exe", @"d:\clash verge\clash-verge.exe"));

            var sm = new CompanionPolicyEngine.CompanionWatchStateMachine();
            var t0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
            AssertEx.True(sm.ShouldLaunch(true, "clash-verge.exe", true, false, t0));
            sm.RecordLaunchFailed(t0);
            AssertEx.False(sm.ShouldLaunch(true, "clash-verge.exe", true, false, t0.AddSeconds(10)));
            AssertEx.True(sm.ShouldLaunch(true, "clash-verge.exe", true, false, t0.AddSeconds(21)));
            sm.CommitLaunchSucceeded();
            AssertEx.False(sm.ShouldLaunch(true, "clash-verge.exe", true, false, t0.AddSeconds(60)));
        }

        [TestFact]
        public void MihomoSecurityGate_UnknownMustRequireMihomoJsonUnauthorizedAndRejectBasic()
        {
            AssertEx.True(MihomoClient.AllowSecretForVerdict(ListenerKind.Kernel, false));
            AssertEx.False(MihomoClient.AllowSecretForVerdict(ListenerKind.Other, true));
            AssertEx.False(MihomoClient.AllowSecretForVerdict(ListenerKind.Unknown, false));
            AssertEx.True(MihomoClient.AllowSecretForVerdict(ListenerKind.Unknown, true));
            AssertEx.True(MihomoClient.LooksLikeMihomoUnauthorized(401, "{\"message\":\"Unauthorized\"}", ""));
            AssertEx.False(MihomoClient.LooksLikeMihomoUnauthorized(401, "", ""));
            AssertEx.False(MihomoClient.LooksLikeMihomoUnauthorized(401, "{\"message\":\"Unauthorized\"}", "Basic realm=\"x\""));
        }

        [TestFact]
        public void Storage_LegacyV2MigrationAndPortableEncryptedExportImport()
        {
            string dir1 = Path.Combine(Path.GetTempPath(), "pp_v3_mig1_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string dir2 = Path.Combine(Path.GetTempPath(), "pp_v3_mig2_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir1);
                // 模拟写入真实的 v2.6.1 磁盘文件（settings.json + pp-whitelist.yaml + DPAPI static_proxy.dat）
                File.WriteAllText(Path.Combine(dir1, "settings.json"),
                    "{\"mode\":\"whitelist\",\"followEnabled\":true,\"followAppExe\":\"clash-verge.exe\",\"followAppPath\":\"D:\\\\Clash Verge\\\\clash-verge.exe\",\"followAutoExit\":true}",
                    new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir1, "pp-whitelist.yaml"),
                    "payload:\n  - PROCESS-NAME,chrome.exe,PP-STATIC-OUT\n  - PROCESS-NAME,antigravity.exe,PP-FIXED\n",
                    new UTF8Encoding(false));
                byte[] legacyStaticPlain = Encoding.UTF8.GetBytes("{\"enabled\":true,\"label\":\"旧固定IP\",\"host\":\"198.51.100.77\",\"port\":1080,\"username\":\"u1\",\"password\":\"pw99\",\"hopMode\":\"auto\",\"autoBestHopNode\":\"Node-US-02\"}");
                File.WriteAllBytes(Path.Combine(dir1, "static_proxy.dat"), NativeCryptoVault.ProtectForCurrentUser(legacyStaticPlain));

                var red = new RedactorService();
                var aud = new FileAuditSink(dir1, red, true);
                var repo1 = new FileConfigRepository(dir1, red, aud);

                var st = repo1.LoadSettings();
                var rules = repo1.LoadRouteRules();
                var vault = repo1.LoadStaticVault();

                AssertEx.Equal(SplitMode.Whitelist, st.Mode);
                AssertEx.True(st.Companion.FollowEnabled);
                AssertEx.Equal("clash-verge.exe", st.Companion.FollowAppExe);
                AssertEx.Equal(2, rules.Count);
                AssertEx.Equal(RouteChannel.StaticChannel, rules[0].Channel);
                AssertEx.Equal(RouteChannel.PrimaryProxy, rules[1].Channel);
                AssertEx.True(vault.GetDefaultOrPrimary().IsActive());
                AssertEx.Equal("198.51.100.77", vault.GetDefaultOrPrimary().Host);

                // 导出加密迁移包 → 导入到全新机器目录 dir2
                string bundle = repo1.ExportEncryptedBundle("MyStrongPass!2026", includeStaticCredentials: true);
                AssertEx.DoesNotContain("198.51.100.77", bundle);
                AssertEx.DoesNotContain("pw99", bundle);

                var repo2 = new FileConfigRepository(dir2, new RedactorService(), new FileAuditSink(dir2, red, true));
                string sum, err;
                // 错误口令必须拒绝
                AssertEx.False(repo2.ImportEncryptedBundle(bundle, "WrongPass", out sum, out err));
                // 正确口令完整恢复
                AssertEx.True(repo2.ImportEncryptedBundle(bundle, "MyStrongPass!2026", out sum, out err), err);
                AssertEx.Equal(2, repo2.LoadRouteRules().Count);
                AssertEx.Equal("198.51.100.77", repo2.LoadStaticVault().GetDefaultOrPrimary().Host);
                AssertEx.Equal("pw99", repo2.LoadStaticVault().GetDefaultOrPrimary().Password);
            }
            finally
            {
                try { if (Directory.Exists(dir1)) Directory.Delete(dir1, true); } catch { }
                try { if (Directory.Exists(dir2)) Directory.Delete(dir2, true); } catch { }
            }
        }
    }
}
