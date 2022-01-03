using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OptimeGBA;

namespace OptimeGBAEmulator
{
    public sealed class MainTerm
    {
        static Gba Gba;

        const int GBA_WIDTH = 240;
        const int GBA_HEIGHT = 160;

        const int CyclesPerFrameGba = 280896;
        const double SECONDS_PER_FRAME_GBA = 1D / (16777216D / 280896D);

        static readonly Dictionary<string, string[]> COLOR_PALETTES = new Dictionary<string, string[]>()
        {
            { "dark-block-wide", new string[] { "██", "█▓", "▓▓", "▓▒", "▒▒", "▒░", "░░", "░ ", "  "} },
            { "dark-block", new string[] { "█", "▓", "▒", "░", " "} }
        };

        public static async Task Main(string[] args)
        {
            Console.WriteLine("Loading ROM \"{0}\"", args[0]);
            Gba = LoadGba(args[0]);

            using PeriodicTimer mainClock = new PeriodicTimer(TimeSpan.FromSeconds(SECONDS_PER_FRAME_GBA));
            using TermControl term = new TermControl(COLOR_PALETTES["dark-block-wide"]);

            long cyclesLeft = 0;
            while (true)
            {
                cyclesLeft += CyclesPerFrameGba;
                while (cyclesLeft > 0)
                {
                    cyclesLeft -= Gba.StateStep();
                }

                if (Gba.Ppu.Renderer.RenderingDone)
                {
                    term.Display(GBA_WIDTH, GBA_HEIGHT, Gba.Ppu.Renderer.ScreenFront);
                }

                await mainClock.WaitForNextTickAsync();
            }
        }

        private static Gba LoadGba(string romPath)
        {
            byte[] rom;
            if (!System.IO.File.Exists(romPath))
            {
                throw new InvalidOperationException("The ROM file you provided does not exist.");
            }
            else
            {
                try
                {
                    rom = System.IO.File.ReadAllBytes(romPath);
                }
                catch
                {
                    throw new InvalidOperationException("The ROM file you provided exists, but there was an issue loading it.");
                }
            }

            string savPath = romPath.Substring(0, romPath.Length - 3) + "sav";
            byte[] sav = new byte[0];

            if (System.IO.File.Exists(savPath))
            {
                Console.WriteLine(".sav exists, loading");
                try
                {
                    sav = System.IO.File.ReadAllBytes(savPath);
                }
                catch
                {
                    throw new InvalidOperationException("Failed to load .sav file!");
                }
            }
            else
            {
                Console.WriteLine(".sav not available");
            }

            Console.WriteLine("Loading GBA file");

            string gbaBiosPath = "gba_bios.bin";
            byte[] gbaBios;
            if (!System.IO.File.Exists(gbaBiosPath))
            {
                throw new InvalidOperationException("Please place a valid GBA BIOS in the same directory as OptimeGBA.exe named \"gba_bios.bin\"");
            }
            else
            {
                try
                {
                    gbaBios = System.IO.File.ReadAllBytes(gbaBiosPath);
                }
                catch
                {
                    throw new InvalidOperationException("A GBA BIOS was provided, but there was an issue loading it.");
                }
            }

            var provider = new ProviderGba(gbaBios, rom, savPath, x => {});
            provider.BootBios = true;

            return new Gba(provider);
        }
    }
}
