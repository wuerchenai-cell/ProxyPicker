using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Services
{
    /// <summary>
    /// 独立固定 IP 通道稳态优选算法、CAS 安全合并（P0-01 架构级免疫）与快速粘贴解析器。
    /// </summary>
    public static class StaticChannelAlgorithms
    {
        public const int ScheduledRefreshMinutes = 10;
        public const int DegradedCooldownSeconds = 180;
        public const int EmergencyCooldownSeconds = 120;

        /// <summary>综合稳态分 = 平均延迟 + 抖动惩罚（jitterMs / 2），越小越优。</summary>
        public static int ComputeStabilityScore(int avgDelayMs, int jitterMs)
        {
            if (avgDelayMs <= 0) return int.MaxValue;
            return avgDelayMs + Math.Max(0, jitterMs) / 2;
        }

        /// <summary>按「多次采样全通优先 → 稳态分升序 → 平均延迟升序」排列候选跳板。</summary>
        public static void RankVerifiedCandidates(List<HopCandidateMetric> candidates)
        {
            if (candidates == null || candidates.Count <= 1) return;
            candidates.Sort((a, b) =>
            {
                int cmp = b.SuccessSamples.CompareTo(a.SuccessSamples);
                if (cmp != 0) return cmp;
                cmp = a.StabilityScore.CompareTo(b.StabilityScore);
                if (cmp != 0) return cmp;
                return a.DelayMs.CompareTo(b.DelayMs);
            });
        }

        /// <summary>判定当前固定 IP 延迟是否发生明显劣化（>=750ms，或 >=420ms 且较历史基线暴涨 >=65%）。</summary>
        public static bool IsDelayDegraded(int liveDelayMs, int baselineDelayMs)
        {
            if (liveDelayMs <= 0) return false;
            if (liveDelayMs >= 750) return true;
            if (liveDelayMs >= 420 && baselineDelayMs > 0 && liveDelayMs >= (int)Math.Round(baselineDelayMs * 1.65))
                return true;
            return false;
        }

        /// <summary>15% 且 >=60ms 防抖切跳板决策：平稳期不横跳，手动或故障自愈立刻切。</summary>
        public static bool ShouldSwitchAutoHop(
            string currentHop,
            int currentDelayMs,
            string bestNode,
            int bestDelayMs,
            bool emergencyOrTimedOut,
            bool manual)
        {
            if (string.IsNullOrWhiteSpace(bestNode) || bestDelayMs <= 0) return false;
            if (YamlRuleEngine.IsManagedStaticName(bestNode)) return false;
            if (string.Equals((currentHop ?? "").Trim(), bestNode.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (manual || emergencyOrTimedOut || string.IsNullOrWhiteSpace(currentHop) || currentDelayMs <= 0) return true;
            int gain = currentDelayMs - bestDelayMs;
            return gain >= 60 && gain * 100 >= currentDelayMs * 15;
        }

        /// <summary>决定此刻是否应触发新一轮后台零干扰跳板稳速巡检（10 分钟定时 / 2 轮劣化 3 分钟冷却 / 超时 2 分钟急救）。</summary>
        public static bool ShouldRunScheduledRefresh(
            bool isAutoModeActive,
            bool alreadyOptimizing,
            DateTime lastOptimizeUtc,
            long savedUpdatedAtTicks,
            DateTime nowUtc,
            int slowStreak,
            bool isTimedOut)
        {
            if (!isAutoModeActive || alreadyOptimizing) return false;
            DateTime lastRef = lastOptimizeUtc;
            if (savedUpdatedAtTicks > 0)
            {
                var savedDt = new DateTime(savedUpdatedAtTicks, DateTimeKind.Utc);
                if (savedDt > lastRef) lastRef = savedDt;
            }
            if (lastRef == DateTime.MinValue) return true;

            double elapsedSec = (nowUtc - lastRef).TotalSeconds;
            if (isTimedOut && elapsedSec >= EmergencyCooldownSeconds) return true;
            if (slowStreak >= 2 && elapsedSec >= DegradedCooldownSeconds) return true;
            return elapsedSec >= ScheduledRefreshMinutes * 60;
        }

        /// <summary>
        /// 架构级防旧快照覆盖合并（永久免疫 P0-01）：
        /// 仅当通道仍启用且服务器/端口/账密/UDP/模式（自动场景）在测速期间未被用户改动时，
        /// 才基于最新持久化配置 merged 写入优选跳板字段；否则安全丢弃旧测速结果。
        /// </summary>
        public static bool TryMergeAutoHopResult(
            StaticChannelProfile startSnapshot,
            StaticChannelProfile latestCurrent,
            bool manual,
            bool switchIt,
            string bestNode,
            int bestDelayMs,
            string keptFallbackNode,
            int keptDelayMs,
            IList<string> hopPool,
            long updatedAtTicks,
            out StaticChannelProfile merged,
            out string discardReason)
        {
            merged = null;
            discardReason = "";
            if (startSnapshot == null || latestCurrent == null)
            {
                discardReason = "通道配置不存在";
                return false;
            }
            if (!latestCurrent.IsActive())
            {
                discardReason = "测速期间该固定 IP 通道已停用，放弃写回";
                return false;
            }
            if (!string.Equals(startSnapshot.Protocol, latestCurrent.Protocol, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals((startSnapshot.Host ?? "").Trim(), (latestCurrent.Host ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ||
                startSnapshot.Port != latestCurrent.Port ||
                !string.Equals(startSnapshot.Username ?? "", latestCurrent.Username ?? "", StringComparison.Ordinal) ||
                !string.Equals(startSnapshot.Password ?? "", latestCurrent.Password ?? "", StringComparison.Ordinal) ||
                startSnapshot.AllowUdp != latestCurrent.AllowUdp)
            {
                discardReason = "测速期间服务器、凭据或 UDP 设置已变更，丢弃旧测速结果";
                return false;
            }
            if (latestCurrent.HopMode == StaticHopMode.Direct)
            {
                discardReason = "测速期间已切为直连模式，放弃写回";
                return false;
            }
            if (!manual && latestCurrent.HopMode != StaticHopMode.Auto)
            {
                discardReason = "测速期间跳板模式已不再是智能优选，放弃写回";
                return false;
            }

            var next = latestCurrent.Clone();
            if (manual) next.HopMode = StaticHopMode.Auto;
            next.AutoBestUpdatedAtTicks = updatedAtTicks;
            next.AutoBestHopPool = hopPool != null
                ? hopPool.Where(n => !string.IsNullOrWhiteSpace(n) && !YamlRuleEngine.IsManagedStaticName(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList()
                : new List<string>();
            if (switchIt)
            {
                if (!string.IsNullOrWhiteSpace(bestNode)) next.AutoBestHopNode = bestNode.Trim();
                if (bestDelayMs > 0) next.AutoBestDelayMs = bestDelayMs;
            }
            else
            {
                if (string.IsNullOrEmpty(next.AutoBestHopNode))
                    next.AutoBestHopNode = !string.IsNullOrEmpty(keptFallbackNode) ? keptFallbackNode.Trim() : (bestNode ?? "").Trim();
                if (keptDelayMs > 0) next.AutoBestDelayMs = keptDelayMs;
            }
            merged = next;
            return true;
        }

        /// <summary>一行粘贴智能解析 SOCKS5/HTTP 连接串（支持 URI 与 host:port:user:pass 等多种格式）。</summary>
        public static bool TryParseQuickLine(string rawLine, out string protocol, out string host, out int port, out string user, out string pass)
        {
            protocol = "socks5";
            host = "";
            port = 0;
            user = "";
            pass = "";
            if (string.IsNullOrWhiteSpace(rawLine)) return false;
            string s = rawLine.Trim();

            // 格式 1：socks5://user:pass@host:port 或 http://...
            var uriMatch = Regex.Match(s, @"^(socks5h?|https?)://(?:([^:@/\s]+)(?::([^@/\s]*))?@)?(\[[^\]]+\]|[^:/\s]+):(\d{1,5})/?$", RegexOptions.IgnoreCase);
            if (uriMatch.Success)
            {
                protocol = uriMatch.Groups[1].Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "http" : "socks5";
                user = Uri.UnescapeDataString(uriMatch.Groups[2].Value ?? "");
                pass = Uri.UnescapeDataString(uriMatch.Groups[3].Value ?? "");
                host = uriMatch.Groups[4].Value.Trim('[', ']');
                return int.TryParse(uriMatch.Groups[5].Value, out port) && port >= 1 && port <= 65535;
            }

            // 格式 2：host:port:user:pass 或 host:port
            string[] parts = s.Split(':');
            if (parts.Length == 2 || parts.Length == 4)
            {
                if (int.TryParse(parts[1].Trim(), out port) && port >= 1 && port <= 65535 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    host = parts[0].Trim();
                    if (parts.Length == 4)
                    {
                        user = parts[2].Trim();
                        pass = parts[3].Trim();
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
