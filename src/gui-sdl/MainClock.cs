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

                // Step all GBAs in lockstep, one instruction at a time.
                // This keeps schedulers synchronized for link cable transfers.
                if (executionList.Count == 0)
                {
                    continue;
                }
                while (executionList[0].CyclesLeft > 0)
                {
                    foreach (var execution in executionList)
                    {
                        execution.CyclesLeft -= execution.Gba.Step();
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
