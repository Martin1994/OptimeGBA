using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace OptimeGBA
{
    // Each frame (~280896 cycles) is divided into ~137 sync chunks of 2048 cycles each.
    // At each sync point, all GBA instances must reach the same tick before proceeding,
    // and the link transfer state is checked. The sync strategy controls how this
    // cross-instance synchronization is implemented.
    //
    // When using a PeriodicTimer as the clock source, observed lag was ordered:
    //   SingleThread (smoothest) < Spin < Barrier (most laggy)
    // After switching to vsync + wall-clock time budgeting, all three strategies
    // perform comparably well. This is because:
    //
    // 1. Vsync is a hardware-driven clock source (GPU/display driver interrupt),
    //    far more precise than PeriodicTimer which depends on OS timer resolution.
    //
    // 2. Wall-clock time budgeting is self-correcting. With the old fixed
    //    "1 frame per tick" approach, if a PeriodicTimer tick arrived late, the
    //    frame still only got 1 frame of cycles — timing errors accumulated.
    //    Now, elapsed wall time is measured each tick and converted to cycles,
    //    so a late tick simply gets more cycles to compensate. Per-chunk Barrier
    //    wake-up jitter (~5-10μs × 137 chunks ≈ ~1ms total) averages out over
    //    the frame and doesn't visibly affect frame pacing.
    //
    // Strategy differences:
    //
    // - SingleThread: Runs all GBA instances sequentially on one core. Zero
    //   synchronization overhead. Cannot utilize multiple cores.
    //
    // - Spin: Workers spin on Volatile.Read (~100ns wake latency). No kernel
    //   involvement, but burns 100% CPU on each worker core.
    //
    // - Barrier: Workers block in the kernel (~5-10μs wake per chunk). Most
    //   CPU-efficient multi-threaded option. Can utilize multiple cores for
    //   machines where a single core can't sustain 2×60fps.
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

        // GBA CPU clock: 16777216 Hz. One GBA frame = 280896 cycles ≈ 59.7275 fps.
        const long CyclesPerSecond = 16777216;

        public GbaLink Link;
        public LinkSyncStrategy Strategy = LinkSyncStrategy.Barrier;

        readonly List<Gba> gbas = new();
        readonly List<AutoResetEvent> vsyncSignals = new();
        long[] cyclesLeft = Array.Empty<long>();

        volatile bool running;

        public AutoResetEvent Register(Gba gba)
        {
            gbas.Add(gba);
            cyclesLeft = new long[gbas.Count];
            var signal = new AutoResetEvent(false);
            vsyncSignals.Add(signal);
            return signal;
        }

        public bool Ready => gbas.Count >= 2 && Link != null;

        // Runs the emulation loop on the calling thread. Blocks until Stop() is called.
        // Paced by vsync signals from windows via the per-GBA AutoResetEvents.
        public void Run()
        {
            if (!Ready)
            {
                return;
            }
            running = true;

            switch (Strategy)
            {
                case LinkSyncStrategy.Barrier:
                    RunBarrier();
                    break;
                case LinkSyncStrategy.SingleThread:
                    RunSingleThread();
                    break;
                case LinkSyncStrategy.Spin:
                    RunSpin();
                    break;
            }
        }

        public void Stop()
        {
            running = false;
            // Unblock any WaitOne() calls so Run() can exit
            foreach (var s in vsyncSignals)
            {
                s.Set();
            }
        }

        void WaitForVsync()
        {
            // For SingleThread, only the first window's vsync paces emulation.
            // For multi-threaded, wait for all windows.
            if (Strategy == LinkSyncStrategy.SingleThread)
            {
                vsyncSignals[0].WaitOne();
            }
            else
            {
                for (int i = 0; i < vsyncSignals.Count; i++)
                {
                    vsyncSignals[i].WaitOne();
                }
            }
        }

        void RunBarrier()
        {
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
                while (running)
                {
                    WaitForVsync();
                    if (!running) break;
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

        void RunSingleThread()
        {
            int n = gbas.Count;

            while (running)
            {
                WaitForVsync();
                if (!running) break;
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

        void RunSpin()
        {
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
                while (running)
                {
                    WaitForVsync();
                    if (!running) break;
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

        readonly Stopwatch wallClock = Stopwatch.StartNew();
        long lastWallClockTicks;

        // Adds cycles based on elapsed wall-clock time, so the emulation runs at
        // the correct speed regardless of the monitor refresh rate (30/60/75/120Hz).
        void AddFrameCycles(int n)
        {
            long now = wallClock.ElapsedTicks;
            long delta = now - lastWallClockTicks;
            lastWallClockTicks = now;

            long cyclesToAdd = delta * CyclesPerSecond / Stopwatch.Frequency;

            if (cyclesToAdd > CyclesPerFrame * 2)
            {
                cyclesToAdd = CyclesPerFrame * 2;
            }

            for (int i = 0; i < n; i++)
            {
                cyclesLeft[i] += cyclesToAdd;
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
