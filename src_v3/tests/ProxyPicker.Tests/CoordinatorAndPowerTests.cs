using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProxyPicker.Application;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Tests
{
    internal sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        public DateTime LocalNow => UtcNow.ToLocalTime();
        public long ElapsedMs { get; set; }
    }

    internal sealed class FakePowerMonitor : IPowerMonitor
    {
        public event Action SystemResumed;
        public event Action NetworkChanged;
        public void RaiseResume() => SystemResumed?.Invoke();
        public void RaiseNetworkChanged() => NetworkChanged?.Invoke();
    }

    public sealed class CoordinatorAndPowerTests
    {
        [TestFact]
        public async Task KernelCoordinator_PriorityOrderingAndCasStaleDiscard()
        {
            using (var coord = new KernelCoordinator())
            {
                var gate = new ManualResetEventSlim(false);
                var order = new List<string>();

                // 先占住执行槽，让后续不同优先级的任务在队列里排好队
                var tHold = coord.EnqueueAsync("Hold", KernelWritePriority.P1_UserAction, ct =>
                {
                    gate.Wait(2000);
                    order.Add("Hold");
                    return new KernelOpResult { Ok = true };
                });

                long rev0 = coord.CurrentRevision;
                var tBgStale = coord.EnqueueAsync("BgStale", KernelWritePriority.P4_BackgroundOptimize, ct =>
                {
                    order.Add("BgStale");
                    return new KernelOpResult { Ok = true };
                }, expectedRevision: rev0);

                var tRepair = coord.EnqueueAsync("Repair", KernelWritePriority.P3_AutoRepairOrFailover, ct =>
                {
                    order.Add("Repair");
                    return new KernelOpResult { Ok = true };
                });

                var tUser = coord.EnqueueAsync("UserSave", KernelWritePriority.P1_UserAction, ct =>
                {
                    coord.BumpConfigRevision();
                    order.Add("UserSave");
                    return new KernelOpResult { Ok = true };
                });

                gate.Set();
                await Task.WhenAll(tHold, tBgStale, tRepair, tUser);

                // 验证严格优先级：UserSave(P1) 先于 Repair(P3)，而 BgStale(P4) 因 UserSave 递增了 Revision 自动作废！
                AssertEx.Equal(3, order.Count);
                AssertEx.Equal("Hold", order[0]);
                AssertEx.Equal("UserSave", order[1]);
                AssertEx.Equal("Repair", order[2]);
                AssertEx.True(tBgStale.Result.DiscardedByStaleRevision, "过期的后台优化任务必须被 CAS 自动作废");
                AssertEx.False(tBgStale.Result.Executed);
            }
        }

        [TestFact]
        public async Task KernelCoordinator_ShutdownDropsPendingBackgroundTasks()
        {
            using (var coord = new KernelCoordinator())
            {
                var gate = new ManualResetEventSlim(false);
                var tHold = coord.EnqueueAsync("Hold", KernelWritePriority.P1_UserAction, _ => { gate.Wait(2000); return new KernelOpResult { Ok = true }; });
                var tBg = coord.EnqueueAsync("Bg", KernelWritePriority.P4_BackgroundOptimize, _ => new KernelOpResult { Ok = true });
                coord.BeginShutdown();
                var tRestore = coord.EnqueueAsync("Restore", KernelWritePriority.P0_ShutdownRestore, _ => new KernelOpResult { Ok = true, Message = "restored" });
                gate.Set();

                await Task.WhenAll(tHold, tBg, tRestore);
                AssertEx.True(tBg.Result.RejectedByShutdown);
                AssertEx.True(tRestore.Result.Ok);
                AssertEx.Equal("restored", tRestore.Result.Message);
            }
        }

        [TestFact]
        public void PowerResumeGuard_ShouldTriggerOnResumeEventAndWallClockJump()
        {
            var clock = new FakeClock();
            var pm = new FakePowerMonitor();
            using (var guard = new PowerResumeGuard(pm, clock, 12))
            {
                double rem;
                AssertEx.False(guard.IsInGraceWindow(out rem));

                pm.RaiseResume();
                AssertEx.True(guard.IsInGraceWindow(out rem) && rem > 10);

                clock.UtcNow = clock.UtcNow.AddSeconds(13);
                AssertEx.False(guard.IsInGraceWindow(out rem));

                // 模拟系统合盖 2 小时但未收到 PowerMode 事件：墙钟跳变自检应自动进入 12s 宽限窗！
                clock.UtcNow = clock.UtcNow.AddHours(2);
                AssertEx.True(guard.IsInGraceWindow(out rem) && rem > 10);

                clock.UtcNow = clock.UtcNow.AddSeconds(13);
                AssertEx.False(guard.IsInGraceWindow(out rem));
            }
        }
    }
}
