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

                int div = 3000;
                for (int i = 0 ; i < div; i++)
                {
                    foreach (var execution in executionList)
                    {
                        var cyclesLeft = execution.CyclesLeft;
                        if (shouldMoveToNextFrame)
                        {
                            cyclesLeft += 280896 / div;
                        }
                        while (cyclesLeft > 0)
                        {
                            uint elapsedCycles = execution.Gba.StateStep();
                            cyclesLeft -= elapsedCycles;
                            // if (Link.ReadyToTransfer)
                        }
                        execution.CyclesLeft = cyclesLeft;
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
