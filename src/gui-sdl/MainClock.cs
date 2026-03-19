using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OptimeGBA;

namespace OptimeGBASdl
{
    public class MainClock
    {
        public GbaLink Link;

        private class GbaExecution
        {
            public Gba Gba { get; init; }
            public long CyclesLeft { get; set; }
        }

        private readonly List<GbaExecution> executionList = new();
        public void Register(Gba gba)
        {
            executionList.Add(new GbaExecution()
            {
                Gba = gba,
                CyclesLeft = 0,
            });
        }

        public async Task Run()
        {
            using PeriodicTimer mainClock = new PeriodicTimer(TimeSpan.FromSeconds(1D / 60D));

            while (true)
            {
                await mainClock.WaitForNextTickAsync();

                bool shouldMoveToNextFrame = false;
                foreach (var execution in executionList)
                {
                    if (execution.CyclesLeft <= 0)
                    {
                        shouldMoveToNextFrame = true;
                        break;
                    }
                }

                if (shouldMoveToNextFrame)
                {
                    foreach (var execution in executionList)
                    {
                        execution.CyclesLeft += 280896;
                    }
                }

                if (executionList.Count == 0)
                {
                    continue;
                }
                // Step GBAs in bounded chunks. TickLimit caps both instruction
                // execution and HaltSkip, preventing any GBA from drifting ahead.
                // Must be < min transfer delay (6044 cycles at 115200 baud, 2 players).
                const int SyncCycles = 2048;
                while (executionList[0].CyclesLeft > 0)
                {
                    long minTick = long.MaxValue;
                    foreach (var execution in executionList)
                    {
                        long t = execution.Gba.Scheduler.CurrentTicks;
                        if (t < minTick) minTick = t;
                    }
                    long target = minTick + SyncCycles;

                    foreach (var execution in executionList)
                    {
                        execution.Gba.TickLimit = target;
                        execution.CyclesLeft -= execution.Gba.StateStepUntil();
                    }

                    if (Link != null && Link.ReadyToTransfer)
                    {
                        Link.MultiplayerTransfer();
                    }
                }
            }
        }
    }
}
