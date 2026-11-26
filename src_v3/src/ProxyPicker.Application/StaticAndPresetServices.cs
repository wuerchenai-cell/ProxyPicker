using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Application
{
    /// <summary>
    /// 多固定 IP 通道定时稳速巡检与 CAS 安全回写调度器（架构级根治 P0-01，支持多通道池独立测速与热切）。
    /// </summary>
    public sealed class StaticChannelScheduler
    {
        readonly IConfigRepository _repo;
        readonly IMihomoClient _mihomo;
        readonly IProbeSidecar _sidecar;
        readonly KernelCoordinator _coordinator;
        readonly PowerResumeGuard _powerGuard;
        readonly IClock _clock;
        readonly IAuditSink _audit;
        readonly AppStateStore _store;

        readonly object _optGate = new object();
        readonly HashSet<string> _optimizingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public StaticChannelScheduler(
            IConfigRepository repo,
            IMihomoClient mihomo,
            IProbeSidecar sidecar,
            KernelCoordinator coordinator,
            PowerResumeGuard powerGuard,
            IClock clock,
            IAuditSink audit,
            AppStateStore store)
        {
            _repo = repo;
            _mihomo = mihomo;
            _sidecar = sidecar;
            _coordinator = coordinator;
            _powerGuard = powerGuard;
            _clock = clock;
            _audit = audit;
            _store = store;
        }

        public bool IsOptimizing(string channelId)
        {
            lock (_optGate) return _optimizingIds.Contains(channelId ?? StaticChannelProfile.DefaultChannelId);
        }

        /// <summary>执行指定固定 IP 通道的旁路 3 次稳态跳板优选，并在返回时经 CAS 重读最新保险箱防旧快照覆盖。</summary>
        public async Task<bool> OptimizeChannelAsync(
            string channelId,
            string configPath,
            bool manual,
            bool emergencyRecovery,
            Func<CancellationToken, KernelOpResult> fullReapplyFallback,
            Action<string> progress,
            CancellationToken ct)
        {
            double rem;
            if (!manual && _powerGuard != null && _powerGuard.IsInGraceWindow(out rem))
                return false;

            string cid = string.IsNullOrWhiteSpace(channelId) ? StaticChannelProfile.DefaultChannelId : channelId;
            lock (_optGate)
            {
                if (!_optimizingIds.Add(cid)) return false;
            }

            try
            {
                var startVault = _repo.LoadStaticVault();
                var startCh = startVault.FindById(cid);
                if (startCh == null || !startCh.IsActive() || startCh.HopMode == StaticHopMode.Direct)
                    return false;

                var startSnapshot = startCh.Clone();
                long startRev = _coordinator.CurrentRevision;
                UpdateRuntimeOptimizing(cid, true);

                string curHop = ReadLiveHop(startCh.KernelHopGroupName, configPath);
                var bench = await Task.Run(() => _sidecar.BenchmarkStaticHops(startSnapshot, configPath, progress, ct, curHop), ct).ConfigureAwait(false);
                if (bench == null || !bench.Ok || string.IsNullOrEmpty(bench.BestNode))
                {
                    if (manual) _store.EmitToast("固定 IP 跳板优选未完成", bench != null ? bench.Error : "未找到可用跳板", "warn");
                    return false;
                }

                // 关键 P0-01 防线：重读磁盘最新保险箱配置，验证期间用户未停用、未改主机/端口/账密/UDP/模式！
                var latestVault = _repo.LoadStaticVault();
                var latestCh = latestVault.FindById(cid);
                if (latestCh == null) return false;

                int curBenchMs = -1;
                var matchCur = bench.TopCandidates.FirstOrDefault(x => x.NodeName.Equals(curHop, StringComparison.OrdinalIgnoreCase));
                if (matchCur != null) curBenchMs = matchCur.DelayMs;

                bool wasAuto = latestCh.HopMode == StaticHopMode.Auto;
                bool switchIt = !wasAuto || StaticChannelAlgorithms.ShouldSwitchAutoHop(
                    curHop, curBenchMs, bench.BestNode, bench.BestDelayMs, emergencyRecovery, manual);

                var pool = bench.TopCandidates.Select(x => x.NodeName).ToList();
                StaticChannelProfile merged;
                string discardWhy;
                if (!StaticChannelAlgorithms.TryMergeAutoHopResult(
                        startSnapshot, latestCh, manual, switchIt,
                        bench.BestNode, bench.BestDelayMs,
                        string.IsNullOrEmpty(curHop) ? bench.BestNode : curHop,
                        curBenchMs > 0 ? curBenchMs : bench.BestDelayMs,
                        pool, _clock.UtcNow.Ticks, out merged, out discardWhy))
                {
                    _audit?.Record("STATIC", "固定 IP 优选结果安全跳过写回：" + discardWhy);
                    return false;
                }

                // 仅更新该单通道回最新保险箱，保留其它通道及用户刚刚改动的所有字段
                for (int i = 0; i < latestVault.Channels.Count; i++)
                {
                    if (string.Equals(latestVault.Channels[i].Id, cid, StringComparison.OrdinalIgnoreCase))
                    {
                        latestVault.Channels[i] = merged;
                        break;
                    }
                }
                string saveErr;
                if (!_repo.SaveStaticVault(latestVault, out saveErr)) return false;

                if (switchIt)
                {
                    var prio = manual ? KernelWritePriority.P1_UserAction : KernelWritePriority.P4_BackgroundOptimize;
                    var opRes = await _coordinator.EnqueueAsync(
                        "SwitchStaticHop:" + cid, prio,
                        token =>
                        {
                            if (wasAuto)
                            {
                                var sw = _mihomo.SwitchProxyNode(merged.KernelHopGroupName, bench.BestNode, 5000, configPath);
                                if (sw.Ok) return new KernelOpResult { Ok = true, Message = "热切换成功" };
                            }
                            return fullReapplyFallback != null
                                ? fullReapplyFallback(token)
                                : new KernelOpResult { Ok = false, Message = "需要重推配置" };
                        },
                        manual ? (long?)null : startRev).ConfigureAwait(false);

                    if (opRes.Ok)
                    {
                        _audit?.Record("STATIC", "固定 IP 稳速优选切换前置跳板：" + bench.BestNode + "（" + bench.BestDelayMs + "ms，" + bench.ReachableCount + "/" + bench.TestedCount + " 可达）");
                        UpdateRuntimeDelay(cid, bench.BestNode, bench.BestDelayMs, false);
                    }
                    return opRes.Ok;
                }
                else
                {
                    int keptMs = curBenchMs > 0 ? curBenchMs : bench.BestDelayMs;
                    _audit?.Record("STATIC", "固定 IP 定时稳速巡检完成：当前跳板「" + (string.IsNullOrEmpty(curHop) ? bench.BestNode : curHop) + "」保持最优（" + keptMs + "ms）");
                    UpdateRuntimeDelay(cid, string.IsNullOrEmpty(curHop) ? bench.BestNode : curHop, keptMs, false);
                    return true;
                }
            }
            finally
            {
                lock (_optGate) _optimizingIds.Remove(cid);
                UpdateRuntimeOptimizing(cid, false);
            }
        }

        string ReadLiveHop(string groupName, string configPath)
        {
            try
            {
                var r = _mihomo.FetchJson("/proxies/" + Uri.EscapeDataString(groupName), 3000, configPath);
                return r.Ok && r.Data != null ? Domain.Json.JsonCodec.Str(r.Data, "now", "") : "";
            }
            catch { return ""; }
        }

        void UpdateRuntimeOptimizing(string cid, bool optimizing)
        {
            _store.Update(s =>
            {
                var rt = s.StaticRuntimes.FirstOrDefault(x => string.Equals(x.ChannelId, cid, StringComparison.OrdinalIgnoreCase));
                if (rt != null)
                {
                    rt.Optimizing = optimizing;
                    if (optimizing) rt.LastOptimizedUtc = _clock.UtcNow;
                }
            });
        }

        void UpdateRuntimeDelay(string cid, string hop, int delayMs, bool timedOut)
        {
            _store.Update(s =>
            {
                var rt = s.StaticRuntimes.FirstOrDefault(x => string.Equals(x.ChannelId, cid, StringComparison.OrdinalIgnoreCase));
                if (rt != null)
                {
                    if (!string.IsNullOrEmpty(hop)) rt.LiveHopNode = hop;
                    rt.DelayMs = delayMs;
                    rt.TimedOut = timedOut;
                    rt.SlowStreak = 0;
                    rt.TimeoutStreak = 0;
                    rt.LastCheckedUtc = _clock.UtcNow;
                }
            });
        }
    }

    /// <summary>v3.0 命名场景预设服务（保存当前状态为预设 / 一键整套切换预设）。</summary>
    public sealed class WorkspacePresetService
    {
        readonly IConfigRepository _repo;
        readonly KernelCoordinator _coordinator;
        readonly IAuditSink _audit;

        public WorkspacePresetService(IConfigRepository repo, KernelCoordinator coordinator, IAuditSink audit)
        {
            _repo = repo;
            _coordinator = coordinator;
            _audit = audit;
        }

        public bool SaveCurrentAsPreset(string name, string description, out WorkspacePreset created, out string error)
        {
            created = null;
            error = "";
            if (string.IsNullOrWhiteSpace(name)) { error = "预设名称不能为空"; return false; }

            var settings = _repo.LoadSettings();
            var rules = _repo.LoadRouteRules();
            var vault = _repo.LoadStaticVault();

            var existing = settings.Presets.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            var preset = existing ?? new WorkspacePreset { Id = Guid.NewGuid().ToString("N").Substring(0, 8) };
            preset.Name = name.Trim();
            preset.Description = (description ?? "").Trim();
            preset.Mode = settings.Mode;
            preset.DefaultStaticChannelId = vault.ActiveDefaultChannelId;
            preset.Rules = rules.Select(r => r.Clone()).ToList();
            preset.UpdatedAtUtc = DateTime.UtcNow;

            if (existing == null) settings.Presets.Add(preset);
            settings.ActivePresetId = preset.Id;
            if (!_repo.SaveSettings(settings, out error)) return false;

            created = preset;
            _audit?.Record("PRESET", "已保存场景预设「" + preset.Name + "」（" + preset.Rules.Count + " 条规则 · " + preset.Mode + "）");
            return true;
        }

        public async Task<KernelOpResult> ActivatePresetAsync(
            string presetId,
            Func<SplitMode, IList<AppRouteRule>, CancellationToken, KernelOpResult> applyToKernelAction)
        {
            var settings = _repo.LoadSettings();
            var preset = settings.Presets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return new KernelOpResult { Ok = false, Message = "未找到指定场景预设" };

            settings.Mode = preset.Mode;
            settings.ActivePresetId = preset.Id;
            string err;
            if (!_repo.SaveSettings(settings, out err))
                return new KernelOpResult { Ok = false, Message = "保存设置失败：" + err };
            if (!_repo.SaveRouteRules(preset.Rules, out err))
                return new KernelOpResult { Ok = false, Message = "写入规则账本失败：" + err };

            var vault = _repo.LoadStaticVault();
            if (!string.IsNullOrEmpty(preset.DefaultStaticChannelId) && vault.FindById(preset.DefaultStaticChannelId) != null)
            {
                vault.ActiveDefaultChannelId = preset.DefaultStaticChannelId;
                _repo.SaveStaticVault(vault, out err);
            }

            _coordinator.BumpConfigRevision();
            var res = await _coordinator.EnqueueAsync(
                "ActivatePreset:" + preset.Name,
                KernelWritePriority.P1_UserAction,
                ct => applyToKernelAction(preset.Mode, preset.Rules, ct)).ConfigureAwait(false);

            if (res.Ok)
                _audit?.Record("PRESET", "已切换至场景预设「" + preset.Name + "」");
            return res;
        }

        public bool DeletePreset(string presetId, out string error)
        {
            error = "";
            var settings = _repo.LoadSettings();
            int removed = settings.Presets.RemoveAll(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) { error = "未找到该场景预设"; return false; }
            if (string.Equals(settings.ActivePresetId, presetId, StringComparison.OrdinalIgnoreCase))
                settings.ActivePresetId = "";
            if (!_repo.SaveSettings(settings, out error)) return false;
            _audit?.Record("PRESET", "已删除场景预设 (" + presetId + ")");
            return true;
        }
    }
}
