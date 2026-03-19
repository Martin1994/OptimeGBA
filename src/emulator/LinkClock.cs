using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OptimeGBA
{
    public class LinkClock
    {
        const int CyclesPerFrame = 280896;
        const int SyncChunkCycles = 2048;

        public GbaLink Link;

        readonly List<GbaExecution> executions = new();

        public void Register(Gba gba)
        {
            executions.Add(new GbaExecution { Gba = gba });
        }

        public async Task Run(CancellationToken ct = default)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 60.0));

            while (await timer.WaitForNextTickAsync(ct))
            {
                if (executions.Count == 0)
                    continue;

                bool anyNeedsCycles = false;
                foreach (var e in executions)
                {
                    if (e.CyclesLeft <= 0)
                    {
                        anyNeedsCycles = true;
                        break;
                    }
                }

                if (anyNeedsCycles)
                {
                    foreach (var e in executions)
                        e.CyclesLeft += CyclesPerFrame;
                }

                while (executions[0].CyclesLeft > 0)
                {
                    long minTick = long.MaxValue;
                    foreach (var e in executions)
                    {
                        long t = e.Gba.Scheduler.CurrentTicks;
                        if (t < minTick) minTick = t;
                    }
                    long target = minTick + SyncChunkCycles;

                    foreach (var e in executions)
                    {
                        e.Gba.TickLimit = target;
                        e.CyclesLeft -= e.Gba.StateStepUntil();
                    }

                    if (Link != null && Link.ReadyToTransfer)
                        Link.MultiplayerTransfer();
                }
            }
        }

        class GbaExecution
        {
            public Gba Gba;
            public long CyclesLeft;
        }
    }
}
