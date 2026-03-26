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

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 60.0));
            int n = gbas.Count;

            while (await timer.WaitForNextTickAsync(ct))
            {
                bool anyNeedsCycles = false;
                for (int i = 0; i < n; i++)
                {
                    if (cyclesLeft[i] <= 0)
                    {
                        anyNeedsCycles = true;
                        break;
                    }
                }

                if (anyNeedsCycles)
                {
                    for (int i = 0; i < n; i++)
                    {
                        cyclesLeft[i] += CyclesPerFrame;
                    }
                }

                while (cyclesLeft[0] > 0)
                {
                    long minTick = long.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        long t = gbas[i].Scheduler.CurrentTicks;
                        if (t < minTick) minTick = t;
                    }
                    long target = minTick + SyncChunkCycles;

                    for (int i = 0; i < n; i++)
                    {
                        gbas[i].TickLimit = target;
                        cyclesLeft[i] -= gbas[i].StateStepUntil();
                    }

                    if (Link.ReadyToTransfer)
                    {
                        Link.MultiplayerTransfer();
                    }
                }
            }
        }
    }
}
