using System;
using System.CommandLine;
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
        const double GBA_FPS = 16777216D / 280896D;
        const double DISPLAY_FPS = 20D;
        const double DISPLAY_INTERVAL = 1D / DISPLAY_FPS;
        const int GBA_FRAMES_PER_DISPLAY = (int)(GBA_FPS / DISPLAY_FPS + 0.5);
        const int KEY_RELEASE_FRAMES = 3;

        const int SCALE_X = 2;
        const int SCALE_Y = 2;

        public static int Main(string[] args)
        {
            var romOption = new Option<string>("--rom") { Description = "Path to the ROM file to load", Required = true };

            var rootCommand = new RootCommand("OptimeGBA Terminal Frontend");
            rootCommand.Add(romOption);
            rootCommand.SetAction(parseResult =>
            {
                string rom = parseResult.GetValue(romOption);
                Run(rom).GetAwaiter().GetResult();
            });

            return rootCommand.Parse(args).Invoke();
        }

        static async Task Run(string romPath)
        {
            Console.WriteLine("Loading ROM \"{0}\"", romPath);
            Gba = LoadGba(romPath);

            using PeriodicTimer displayClock = new PeriodicTimer(TimeSpan.FromSeconds(DISPLAY_INTERVAL));
            using TermControl term = new TermControl(SCALE_X, SCALE_Y);

            int[] keyFrameCounters = new int[10]; // one per GBA button

            long cyclesLeft = 0;
            while (true)
            {
                cyclesLeft += CyclesPerFrameGba * GBA_FRAMES_PER_DISPLAY;
                while (cyclesLeft > 0)
                {
                    cyclesLeft -= Gba.StateStep();
                }

                PollInput(keyFrameCounters);
                UpdateKeypad(keyFrameCounters);

                if (Gba.Ppu.Renderer.RenderingDone)
                {
                    Gba.Ppu.Renderer.RenderingDone = false;
                    term.Display(GBA_WIDTH, GBA_HEIGHT, Gba.Ppu.Renderer.ScreenFront);
                }

                await displayClock.WaitForNextTickAsync();
            }
        }

        static void PollInput(int[] keyFrameCounters)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                int index = MapKeyToButton(key);
                if (index >= 0)
                {
                    keyFrameCounters[index] = KEY_RELEASE_FRAMES;
                }
            }
        }

        static int MapKeyToButton(ConsoleKeyInfo key)
        {
            return key.Key switch
            {
                ConsoleKey.Z => 0,         // B
                ConsoleKey.X => 1,         // A
                ConsoleKey.Backspace => 2,  // Select
                ConsoleKey.Enter => 3,      // Start
                ConsoleKey.LeftArrow => 4,  // Left
                ConsoleKey.RightArrow => 5, // Right
                ConsoleKey.UpArrow => 6,    // Up
                ConsoleKey.DownArrow => 7,  // Down
                ConsoleKey.Q => 8,          // L
                ConsoleKey.E => 9,          // R
                _ => -1,
            };
        }

        static void UpdateKeypad(int[] keyFrameCounters)
        {
            Gba.Keypad.B = DecrementAndCheck(keyFrameCounters, 0);
            Gba.Keypad.A = DecrementAndCheck(keyFrameCounters, 1);
            Gba.Keypad.Select = DecrementAndCheck(keyFrameCounters, 2);
            Gba.Keypad.Start = DecrementAndCheck(keyFrameCounters, 3);
            Gba.Keypad.Left = DecrementAndCheck(keyFrameCounters, 4);
            Gba.Keypad.Right = DecrementAndCheck(keyFrameCounters, 5);
            Gba.Keypad.Up = DecrementAndCheck(keyFrameCounters, 6);
            Gba.Keypad.Down = DecrementAndCheck(keyFrameCounters, 7);
            Gba.Keypad.L = DecrementAndCheck(keyFrameCounters, 8);
            Gba.Keypad.R = DecrementAndCheck(keyFrameCounters, 9);
        }

        static bool DecrementAndCheck(int[] counters, int index)
        {
            if (counters[index] > 0)
            {
                counters[index]--;
                return true;
            }
            return false;
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
