using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyPicker.Application
{
    /// <summary>内核写事务优先级（数字越小优先级越高，退出还原永远最高优先）。</summary>
    public enum KernelWritePriority
    {
        P0_ShutdownRestore = 0,
        P1_UserAction = 1,
        P2_CompanionSync = 2,
        P3_AutoRepairOrFailover = 3,
        P4_BackgroundOptimize = 4
    }

    /// <summary>单次内核事务执行结果。</summary>
    public sealed class KernelOpResult
    {
        public bool Executed { get; set; }
        public bool Ok { get; set; }
        public bool DiscardedByStaleRevision { get; set; }
        public bool RejectedByShutdown { get; set; }
        public string Message { get; set; } = "";
        public long RevisionAtCompletion { get; set; }
    }

    /// <summary>
    /// 全系统唯一的 Mihomo 内核写事务串行协调器（彻底消灭 39 个布尔互斥标志打架与旧快照覆盖）：
    /// 1) 所有内核配置推送、节点热切、退出还原必须经本队列严格串行执行，零重入；
    /// 2) 按 P0→P4 优先级调度，退出还原或用户主动保存天然插队于后台看守与定时优选之前；
    /// 3) 内置乐观版本锁（ConfigRevision CAS）：在途低优先级任务发现配置已变自动作废。
    /// </summary>
    public sealed class KernelCoordinator : IDisposable
    {
        sealed class QueuedWork
        {
            public long Seq;
            public string Name;
            public KernelWritePriority Priority;
            public long? ExpectedRevision;
            public Func<CancellationToken, KernelOpResult> Action;
            public TaskCompletionSource<KernelOpResult> Tcs;
        }

        readonly object _queueGate = new object();
        readonly List<QueuedWork> _queue = new List<QueuedWork>();
        readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        readonly Task _workerTask;

        long _seq;
        long _configRevision;
        volatile bool _shuttingDown;
        bool _disposed;

        public long CurrentRevision => Interlocked.Read(ref _configRevision);
        public bool IsShuttingDown => _shuttingDown;

        public KernelCoordinator()
        {
            _workerTask = Task.Run(ProcessLoopAsync);
        }

        /// <summary>用户保存设置/名单/固定 IP 时调用，递增乐观版本号自动作废在途旧任务。</summary>
        public long BumpConfigRevision()
        {
            return Interlocked.Increment(ref _configRevision);
        }

        /// <summary>标记进入关机/退出还原阶段，自动拒绝后续所有非 P0 写任务。</summary>
        public void BeginShutdown()
        {
            _shuttingDown = true;
            lock (_queueGate)
            {
                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    if (_queue[i].Priority != KernelWritePriority.P0_ShutdownRestore)
                    {
                        var dropped = _queue[i];
                        _queue.RemoveAt(i);
                        dropped.Tcs.TrySetResult(new KernelOpResult
                        {
                            Executed = false,
                            Ok = false,
                            RejectedByShutdown = true,
                            Message = "系统正在退出，已取消排队中的后台任务：" + dropped.Name,
                            RevisionAtCompletion = CurrentRevision
                        });
                    }
                }
            }
        }

        public void CancelShutdownState()
        {
            _shuttingDown = false;
        }

        public Task<KernelOpResult> EnqueueAsync(
            string name,
            KernelWritePriority priority,
            Func<CancellationToken, KernelOpResult> action,
            long? expectedRevision = null)
        {
            if (_disposed)
            {
                return Task.FromResult(new KernelOpResult
                {
                    Executed = false, Ok = false, RejectedByShutdown = true, Message = "协调器已释放"
                });
            }
            if (_shuttingDown && priority != KernelWritePriority.P0_ShutdownRestore)
            {
                return Task.FromResult(new KernelOpResult
                {
                    Executed = false, Ok = false, RejectedByShutdown = true,
                    Message = "正在退出还原，已拒绝写任务：" + name,
                    RevisionAtCompletion = CurrentRevision
                });
            }

            var tcs = new TaskCompletionSource<KernelOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new QueuedWork
            {
                Seq = Interlocked.Increment(ref _seq),
                Name = name ?? "KernelOp",
                Priority = priority,
                ExpectedRevision = expectedRevision,
                Action = action,
                Tcs = tcs
            };

            lock (_queueGate)
            {
                _queue.Add(item);
                // 优先级升序，同优先级 FIFO 保持严格因果顺序
                _queue.Sort((a, b) =>
                {
                    int cmp = ((int)a.Priority).CompareTo((int)b.Priority);
                    return cmp != 0 ? cmp : a.Seq.CompareTo(b.Seq);
                });
            }
            _signal.Release();
            return tcs.Task;
        }

        async Task ProcessLoopAsync()
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                try { await _signal.WaitAsync(_disposeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                QueuedWork work = null;
                lock (_queueGate)
                {
                    if (_queue.Count > 0)
                    {
                        work = _queue[0];
                        _queue.RemoveAt(0);
                    }
                }
                if (work == null) continue;

                if (_shuttingDown && work.Priority != KernelWritePriority.P0_ShutdownRestore)
                {
                    work.Tcs.TrySetResult(new KernelOpResult
                    {
                        Executed = false, Ok = false, RejectedByShutdown = true,
                        Message = "进入退出阶段，跳过任务：" + work.Name,
                        RevisionAtCompletion = CurrentRevision
                    });
                    continue;
                }

                if (work.ExpectedRevision.HasValue && work.ExpectedRevision.Value != CurrentRevision)
                {
                    work.Tcs.TrySetResult(new KernelOpResult
                    {
                        Executed = false, Ok = false, DiscardedByStaleRevision = true,
                        Message = "配置版本已更新，丢弃过期任务：" + work.Name,
                        RevisionAtCompletion = CurrentRevision
                    });
                    continue;
                }

                try
                {
                    var res = work.Action(_disposeCts.Token) ?? new KernelOpResult { Executed = true, Ok = true };
                    res.Executed = true;
                    res.RevisionAtCompletion = CurrentRevision;
                    work.Tcs.TrySetResult(res);
                }
                catch (Exception ex)
                {
                    work.Tcs.TrySetResult(new KernelOpResult
                    {
                        Executed = true,
                        Ok = false,
                        Message = ex.Message,
                        RevisionAtCompletion = CurrentRevision
                    });
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
            try { _workerTask.Wait(1500); } catch { }
            try { _signal.Dispose(); } catch { }
            try { _disposeCts.Dispose(); } catch { }
        }
    }
}
