using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OptimeGBA
{
    // Each frame (~280896 cycles) is divided into ~137 sync chunks of 2048 cycles each.
    // At each sync point, all GBA instances must reach the same tick before proceeding,
    // and the link transfer state is checked. The sync strategy controls how this
    // cross-instance synchronization is implemented.
    //
    // Observed lag (frame timing jitter) in the real GUI frontend is ordered:
    //   SingleThread (smoothest) < Spin < Barrier (most laggy)
    //
    // This ordering reflects how much each strategy depends on the OS scheduler:
    //
    // - SingleThread: Zero OS scheduler involvement. The emulation thread never yields
    //   or context-switches between sync points — it's a tight loop on a single core.
    //   Produces the smoothest frame pacing, but cannot utilize multiple cores. If a
    //   single core can't sustain 2x60fps, this will drop below 60fps.
    //
    // - Spin: Workers spin on Volatile.Read and never sleep, so they respond to signals
    //   within ~100ns (inter-core cache coherence latency). No OS scheduler involvement
    //   for wake-up. However, spinning burns 100% CPU on each worker core, which can
    //   cause thermal throttling or contend with GUI/audio threads for CPU time.
    //
    // - Barrier: Workers block in the kernel (pthread_cond_wait / futex). Wake-up goes
    //   through the OS scheduler, adding ~5-10μs per wake. Under real GUI workloads
    //   where SDL rendering, audio, and event threads also compete for CPU, the scheduler
    //   may deprioritize or delay waking worker threads, causing frame timing jitter.
    //   However, this is the most CPU-efficient multi-threaded option and can achieve
    //   60fps on machines where a single core can't sustain 2x60fps.
    public enum LinkSyncStrategy
    {
        Barrier,
        SingleThread,
        Spin,
    }

    public class LinkClock
    {
        const int CyclesPerFrame = 280896;
        const int SyncChunkCycles = 2048;

        public GbaLink Link;
        public LinkSyncStrategy Strategy = LinkSyncStrategy.Barrier;

        readonly List<Gba> gbas = new();
        long[] cyclesLeft = Array.Empty<long>();

        public void Register(Gba gba)
        {
            gbas.Add(gba);
            cyclesLeft = new long[gbas.Count];
        }

        public async Task Run(CancellationToken ct = default)
        {
            while (gbas.Count < 2 || Link == null)
            {
                await Task.Delay(100, ct);
            }

            switch (Strategy)
            {
                case LinkSyncStrategy.Barrier:
                    await RunBarrier(ct);
                    break;
                case LinkSyncStrategy.SingleThread:
                    await RunSingleThread(ct);
                    break;
                case LinkSyncStrategy.Spin:
                    await RunSpin(ct);
                    break;
            }
        }

        async Task RunBarrier(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 60.0));
            int n = gbas.Count;
            long[] workerStepped = new long[n];
            bool workerStop = false;

            using var chunkBarrier = new Barrier(n);

            var workers = new Thread[n - 1];
            for (int wi = 0; wi < workers.Length; wi++)
            {
                int idx = wi + 1;
                workers[wi] = new Thread(() =>
                {
                    while (true)
                    {
                        chunkBarrier.SignalAndWait();
                        if (Volatile.Read(ref workerStop))
                        {
                            return;
                        }
                        long stepped = gbas[idx].StateStepUntil();
                        Volatile.Write(ref workerStepped[idx], stepped);
                        chunkBarrier.SignalAndWait();
                    }
                });
                workers[wi].Name = $"GBA-P{idx}";
                workers[wi].Start();
            }

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    AddFrameCycles(n);

                    while (cyclesLeft[0] > 0)
                    {
                        long target = ComputeTarget(n);

                        for (int i = 0; i < n; i++)
                        {
                            gbas[i].TickLimit = target;
                        }

                        chunkBarrier.SignalAndWait();
                        cyclesLeft[0] -= gbas[0].StateStepUntil();
                        chunkBarrier.SignalAndWait();

                        for (int i = 1; i < n; i++)
                        {
                            cyclesLeft[i] -= Volatile.Read(ref workerStepped[i]);
                        }

                        CheckTransfer();
                    }
                }
            }
            finally
            {
                Volatile.Write(ref workerStop, true);
                chunkBarrier.SignalAndWait();
                foreach (var w in workers)
                {
                    w.Join();
                }
            }
        }

        async Task RunSingleThread(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 60.0));
            int n = gbas.Count;

            while (await timer.WaitForNextTickAsync(ct))
            {
                AddFrameCycles(n);

                while (cyclesLeft[0] > 0)
                {
                    long target = ComputeTarget(n);

                    for (int i = 0; i < n; i++)
                    {
                        gbas[i].TickLimit = target;
                        cyclesLeft[i] -= gbas[i].StateStepUntil();
                    }

                    CheckTransfer();
                }
            }
        }

        async Task RunSpin(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 60.0));
            int n = gbas.Count;

            long go = 0;
            int workersDone = 0;
            bool workerStop = false;

            var workers = new Thread[n - 1];
            for (int wi = 0; wi < workers.Length; wi++)
            {
                int idx = wi + 1;
                workers[wi] = new Thread(() =>
                {
                    long myGen = 0;
                    while (true)
                    {
                        while (Volatile.Read(ref go) == myGen)
                        {
                            if (Volatile.Read(ref workerStop))
                            {
                                return;
                            }
                        }
                        myGen = Volatile.Read(ref go);

                        gbas[idx].StateStepUntil();
                        Interlocked.Increment(ref workersDone);
                    }
                });
                workers[wi].Name = $"GBA-P{idx}";
                workers[wi].Start();
            }

            int workerCount = n - 1;

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    AddFrameCycles(n);

                    while (cyclesLeft[0] > 0)
                    {
                        long target = ComputeTarget(n);

                        for (int i = 1; i < n; i++)
                        {
                            gbas[i].TickLimit = target;
                        }
                        Volatile.Write(ref workersDone, 0);
                        Interlocked.Increment(ref go);

                        gbas[0].TickLimit = target;
                        cyclesLeft[0] -= gbas[0].StateStepUntil();

                        while (Volatile.Read(ref workersDone) < workerCount) { }

                        CheckTransfer();
                    }
                }
            }
            finally
            {
                Volatile.Write(ref workerStop, true);
                Interlocked.Increment(ref go);
                foreach (var w in workers)
                {
                    w.Join();
                }
            }
        }

        void AddFrameCycles(int n)
        {
            bool anyNeeds = false;
            for (int i = 0; i < n; i++)
            {
                if (cyclesLeft[i] <= 0)
                {
                    anyNeeds = true;
                    break;
                }
            }
            if (anyNeeds)
            {
                for (int i = 0; i < n; i++)
                {
                    cyclesLeft[i] += CyclesPerFrame;
                }
            }
        }

        long ComputeTarget(int n)
        {
            long minTick = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                long t = gbas[i].Scheduler.CurrentTicks;
                if (t < minTick) minTick = t;
            }
            return minTick + SyncChunkCycles;
        }

        void CheckTransfer()
        {
            if (Link.ReadyToTransfer)
            {
                Link.MultiplayerTransfer();
            }
        }
    }
}
