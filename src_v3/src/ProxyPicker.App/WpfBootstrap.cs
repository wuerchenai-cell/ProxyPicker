using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProxyPicker.App.Theme;
using ProxyPicker.App.ViewModels;
using ProxyPicker.App.Views;
using ProxyPicker.Application;
using ProxyPicker.Domain;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;
using ProxyPicker.Infrastructure.Companion;
using ProxyPicker.Infrastructure.Discovery;
using ProxyPicker.Infrastructure.Mihomo;
using ProxyPicker.Infrastructure.Quality;
using ProxyPicker.Infrastructure.Sidecar;
using ProxyPicker.Infrastructure.Storage;
using ProxyPicker.Infrastructure.Update;
using ProxyPicker.Platform.Windows;

namespace ProxyPicker.App
{
    /// <summary>
    /// 组装依赖注入根（Composition Root）、WPF 双主题应用外壳、单实例激活监听器、CLI 诊断命令以及高 DPI 离屏视觉截图器。
    /// </summary>
    internal static class WpfBootstrap
    {
        public static void Run(string[] args)
        {
            string selfExe = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(selfExe)) selfExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProxyPicker.exe");

            // 1. 先处理无 UI 的只读 CLI 开关与后台守望模式
            if (TryHandleHeadlessCommands(args, selfExe)) return;

            // 2. 离屏企业级视觉验收渲染模式（--render-preview <outDir>），不触碰单实例也不写用户配置
            if (args.Length >= 2 && args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
            {
                RenderVisualVerificationScreens(args[1]);
                return;
            }

            // 3. 解析启动参数（--tray / --follow / --follow-exe）
            bool startMinimized = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
                                                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            bool launchedViaCompanion = false;
            string followTarget = "";
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i].Equals("--follow", StringComparison.OrdinalIgnoreCase) ||
                     args[i].Equals("--follow-exe", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    followTarget = args[i + 1];
                    launchedViaCompanion = true;
                }
            }

            // 4. 单实例互斥锁：如果已有实例在运行，交接 --follow 目标并唤醒主窗口后优雅退出
            string ownDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyPicker");
            var autostartStore = new WindowsAutostartStore(ownDataDir);
            bool createdNew;
            using (var mutex = new Mutex(true, NativeProcessProbe.MainMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!string.IsNullOrEmpty(followTarget))
                        autostartStore.PostFollowHandoffToMain(followTarget);
                    else
                        WindowsAutostartStore.TrySetNamedEvent(WindowsAutostartStore.ShowEventName);
                    return;
                }

                StartInteractiveWorkbench(selfExe, startMinimized, followTarget, launchedViaCompanion, autostartStore);
            }
        }

        static bool TryHandleHeadlessCommands(string[] args, string selfExe)
        {
            if (args == null || args.Length == 0) return false;
            string cmd = args[0];

            if (cmd.Equals("--version", StringComparison.OrdinalIgnoreCase))
            {
                WriteCliOutput(args.Length >= 2 ? args[1] : null, ProductInfo.Name + " " + ProductInfo.Version + "\r\n");
                return true;
            }

            string ownDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyPicker");
            if (cmd.Equals("--watch-companion", StringComparison.OrdinalIgnoreCase))
            {
                var redactor = new RedactorService();
                var audit = new FileAuditSink(ownDataDir, redactor);
                var repo = new FileConfigRepository(ownDataDir, redactor, audit);
                var probe = new NativeProcessProbe();
                var host = new CompanionWatcherHost(repo, probe, probe, new SystemClock(), audit, selfExe);
                host.RunLoop();
                return true;
            }

            if (cmd.Equals("--diag", StringComparison.OrdinalIgnoreCase) ||
                cmd.Equals("--audit", StringComparison.OrdinalIgnoreCase) ||
                cmd.Equals("--delay", StringComparison.OrdinalIgnoreCase) ||
                cmd.Equals("--scan", StringComparison.OrdinalIgnoreCase) ||
                cmd.Equals("--checkupdate", StringComparison.OrdinalIgnoreCase))
            {
                string outFile = args.Length >= 2 ? args[1] : null;
                string result = ExecuteReadOnlyCliTool(cmd, selfExe, ownDataDir);
                WriteCliOutput(outFile, result);
                return true;
            }
            return false;
        }

        static string ExecuteReadOnlyCliTool(string cmd, string selfExe, string ownDataDir)
        {
            var redactor = new RedactorService();
            var audit = new FileAuditSink(ownDataDir, redactor);
            var repo = new FileConfigRepository(ownDataDir, redactor, audit);
            var discovery = new ClientDiscoveryService();
            var mihomo = new MihomoClient(discovery);
            var probe = new NativeProcessProbe();
            var updater = new UpdateCheckerService();

            if (cmd.Equals("--checkupdate", StringComparison.OrdinalIgnoreCase))
            {
                var u = updater.CheckLatest(6000);
                return "ProxyPicker v" + ProductInfo.Version + " 更新检查:\r\n" +
                       "Ok=" + u.Ok + " | HasNewer=" + u.HasNewer + " | Latest=" + u.LatestVersion +
                       " | Url=" + u.ReleaseUrl + (string.IsNullOrEmpty(u.Error) ? "" : (" | Error=" + u.Error)) + "\r\n";
            }

            using (var engine = new ProxyPickerEngine(
                mihomo, repo, probe, probe, new WindowsAutostartStore(repo.OwnDataDir),
                new ProbeSidecarService(repo.OwnDataDir, audit), new WindowsPowerMonitor(),
                new SystemClock(), audit, redactor,
                force => discovery.Detect(force),
                () => ClientDiscoveryService.ScanInstalledApps(),
                exe => ClientDiscoveryService.DisplayNameFor(exe),
                cb => { bool en; string srv; ClientDiscoveryService.ReadSystemProxyState(out en, out srv); cb(en, srv); },
                selfExe, new NodeQualityService(mihomo, repo.OwnDataDir), updater))
            {
                engine.RefreshFullStateAsync(true).GetAwaiter().GetResult();
                var snap = engine.Store.GetSnapshot();

                if (cmd.Equals("--diag", StringComparison.OrdinalIgnoreCase) || cmd.Equals("--audit", StringComparison.OrdinalIgnoreCase))
                    return engine.BuildSanitizedDiagnosticReport();

                if (cmd.Equals("--delay", StringComparison.OrdinalIgnoreCase))
                {
                    engine.MeasurePrimaryDelayAsync().GetAwaiter().GetResult();
                    engine.MeasureAllActiveStaticDelaysAsync().GetAwaiter().GetResult();
                    var s2 = engine.Store.GetSnapshot();
                    var sb = new StringBuilder();
                    sb.AppendLine("ProxyPicker v" + ProductInfo.Version + " 通道延迟自检:");
                    sb.AppendLine("主代理节点: " + (s2.Kernel.PrimaryDelayMs > 0 ? (s2.Kernel.PrimaryDelayMs + "ms") : "超时/未测"));
                    foreach (var rt in s2.StaticRuntimes)
                        sb.AppendLine("固定IP通道[" + rt.ChannelId + "]: Enabled=" + rt.Enabled + " | Delay=" + (rt.DelayMs > 0 ? (rt.DelayMs + "ms") : "--"));
                    return redactor.Apply(sb.ToString());
                }

                if (cmd.Equals("--scan", StringComparison.OrdinalIgnoreCase))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("ProxyPicker v" + ProductInfo.Version + " 进程与已安装软件扫描: 共 " + snap.Apps.Count + " 项");
                    foreach (var a in snap.Apps.Take(40))
                        sb.AppendLine("- " + a.DisplayName + " (" + a.Exe + ") | Running=" + a.Running + " | Channel=" + a.Channel);
                    return redactor.Apply(sb.ToString());
                }
            }
            return "";
        }

        static void StartInteractiveWorkbench(
            string selfExe, bool startMinimized, string followTarget, bool launchedViaCompanion,
            WindowsAutostartStore autostartStore)
        {
            string ownDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyPicker");
            var redactor = new RedactorService();
            var audit = new FileAuditSink(ownDataDir, redactor);
            var repo = new FileConfigRepository(ownDataDir, redactor, audit);
            var discovery = new ClientDiscoveryService();
            var mihomo = new MihomoClient(discovery);
            var probe = new NativeProcessProbe();
            var sidecar = new ProbeSidecarService(repo.OwnDataDir, audit);
            var quality = new NodeQualityService(mihomo, repo.OwnDataDir);
            var updater = new UpdateCheckerService();
            var powerMonitor = new WindowsPowerMonitor();
            var shortcut = new NativeShortcutService();

            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            InitAppResources(app, repo.LoadSettings().Theme);

            using (var engine = new ProxyPickerEngine(
                mihomo, repo, probe, probe, autostartStore, sidecar, powerMonitor,
                new SystemClock(), audit, redactor,
                force => discovery.Detect(force),
                () => ClientDiscoveryService.ScanInstalledApps(),
                exe => ClientDiscoveryService.DisplayNameFor(exe),
                cb => { bool en; string srv; ClientDiscoveryService.ReadSystemProxyState(out en, out srv); cb(en, srv); },
                selfExe, quality, updater))
            {
                var vm = new ShellViewModel(engine, shortcut, selfExe, pref => ThemeManager.Apply(app.Resources, pref));
                var mainWin = new MainWindow(vm);
                CompactOverlayWindow compactWin = null;
                app.MainWindow = mainWin;

                Action showMain = () => app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (compactWin != null && compactWin.IsVisible) compactWin.Hide();
                    if (!mainWin.IsVisible) mainWin.Show();
                    if (mainWin.WindowState == WindowState.Minimized) mainWin.WindowState = WindowState.Normal;
                    mainWin.Activate();
                }));

                Action toggleCompact = () => app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (compactWin == null)
                    {
                        compactWin = new CompactOverlayWindow(vm);
                        compactWin.ExpandRequested += () => showMain();
                    }
                    if (compactWin.IsVisible) { compactWin.Hide(); showMain(); }
                    else { mainWin.Hide(); compactWin.Show(); }
                }));
                vm.CompactModeRequested += toggleCompact;

                int exiting = 0;
                Action<bool> performExit = triggeredByHost =>
                {
                    if (Interlocked.Exchange(ref exiting, 1) != 0) return;
                    _ = Task.Run(async () =>
                    {
                        try { await engine.ShutdownAsync(triggeredByHost).ConfigureAwait(false); }
                        catch (Exception ex) { audit.ReportCrash(ex, "Shutdown"); }
                        finally
                        {
                            _ = app.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                mainWin.AllowRealClose();
                                try { compactWin?.Close(); } catch { }
                                try { mainWin.Close(); } catch { }
                                app.Shutdown();
                            }));
                        }
                    });
                };

                engine.Companion.HostClosedAutoExitTriggered += () => performExit(true);

                // 监听二次双击激活事件与 --follow 跨进程交接
                var activateCts = new CancellationTokenSource();
                StartActivationListener(autostartStore, engine, showMain, activateCts.Token);

                using (var tray = new TrayHost(
                    showMain,
                    toggleCompact,
                    () => _ = engine.RefreshFullStateAsync(true),
                    () => _ = engine.RestoreCleanKernelAsync(true),
                    () => performExit(false)))
                {
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        audit.ReportCrash(e.Exception, "DispatcherUnhandled");
                        e.Handled = true;
                        vm.ShowToast("已拦截界面异常", e.Exception.Message, "danger");
                    };

                    if (!startMinimized) mainWin.Show();
                    _ = Task.Run(() => engine.InitializeAsync(followTarget, launchedViaCompanion));

                    app.Run();
                    activateCts.Cancel();
                }
            }
        }

        static void StartActivationListener(WindowsAutostartStore store, ProxyPickerEngine engine, Action showMain, CancellationToken ct)
        {
            Task.Run(() =>
            {
                try
                {
                    bool s1, s2;
                    using (var evShow = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsAutostartStore.ShowEventName, out s1))
                    using (var evHandoff = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsAutostartStore.CompanionHandoffEventName, out s2))
                    {
                        WaitHandle[] handles = { evShow, evHandoff };
                        while (!ct.IsCancellationRequested)
                        {
                            int idx = WaitHandle.WaitAny(handles, 500);
                            if (idx == 0)
                            {
                                showMain();
                            }
                            else if (idx == 1)
                            {
                                string handoff = store.ConsumeFollowHandoff();
                                if (!string.IsNullOrEmpty(handoff))
                                {
                                    string msg;
                                    engine.Companion.AttachFollowSession(handoff, engine.Store.GetSnapshot().Settings.Companion, out msg);
                                }
                            }
                        }
                    }
                }
                catch { }
            }, ct);
        }

        /// <summary>在离线环境中以真实 WPF 视觉树（含 100% / 150% DPI 与深/浅双主题）渲染全部 6 大工作区截图以供人工视觉审查。</summary>
        static void RenderVisualVerificationScreens(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            InitAppResources(app, ThemePreference.Light);

            var vm = new ShellViewModel(null, null, "", pref => ThemeManager.Apply(app.Resources, pref));
            vm.LoadVisualVerificationPreview();

            var win = new MainWindow(vm)
            {
                Width = 1160,
                Height = 720,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false
            };

            // 推入高仿真三路曲线走势样本
            double[] proxyCurve = { 120, 260, 410, 880, 1120, 940, 1520, 1890, 1340, 1620, 1420, 1710 };
            double[] staticCurve = { 45, 90, 140, 280, 320, 210, 460, 390, 510, 380, 420, 386 };
            double[] directCurve = { 300, 240, 510, 460, 720, 650, 580, 620, 740, 680, 610, 625 };

            win.Show();
            for (int r = 0; r < 4; r++)
                for (int i = 0; i < proxyCurve.Length; i++)
                    win.PageApps.PushTrafficPoint(proxyCurve[i] * 1024, staticCurve[i] * 1024, directCurve[i] * 1024);

            string[] pageNames = { "01_apps_routing", "02_nodes_static", "03_connections", "04_diagnostics", "05_automation", "06_settings_audit" };
            var configs = new[]
            {
                Tuple.Create("light_100dpi", ThemePreference.Light, 96.0),
                Tuple.Create("dark_100dpi", ThemePreference.Dark, 96.0),
                Tuple.Create("dark_150dpi", ThemePreference.Dark, 144.0)
            };

            foreach (var cfg in configs)
            {
                ThemeManager.Apply(app.Resources, cfg.Item2);
                for (int p = 0; p < pageNames.Length; p++)
                {
                    // 150% DPI 抽检关键复杂页面 01 / 02 / 05；100% DPI 深浅全量 6 页
                    if (cfg.Item3 > 100 && p != 0 && p != 1 && p != 4) continue;
                    vm.SelectedNavIndex = p;
                    win.SyncPageVisibility(p);
                    win.InvalidateVisual();
                    win.UpdateLayout();
                    app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    win.UpdateLayout();
                    app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                    double scale = cfg.Item3 / 96.0;
                    int pxW = (int)Math.Round(win.ActualWidth * scale);
                    int pxH = (int)Math.Round(win.ActualHeight * scale);
                    var rtb = new RenderTargetBitmap(pxW, pxH, cfg.Item3, cfg.Item3, PixelFormats.Pbgra32);
                    rtb.Render(win);

                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    string file = Path.Combine(outDir, cfg.Item1 + "_" + pageNames[p] + ".png");
                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
                        enc.Save(fs);
                }
            }

            win.AllowRealClose();
            win.Close();
            app.Shutdown();
        }

        static void InitAppResources(System.Windows.Application app, ThemePreference pref)
        {
            ThemeManager.Apply(app.Resources, pref);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/ProxyPicker;component/Theme/FluentControls.xaml", UriKind.Absolute)
            });
        }

        static void WriteCliOutput(string outFile, string text)
        {
            if (!string.IsNullOrWhiteSpace(outFile))
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, text ?? "", new UTF8Encoding(false));
            }
            else
            {
                Console.Write(text ?? "");
            }
        }
    }
}
