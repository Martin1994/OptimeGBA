using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OptimeGBA;

namespace OptimeGBA.LinkTest
{
    public static class Program
    {
        const int CyclesPerFrame = 280896;
        const long CyclesPerSecond = 16777216;
        const int GbaWidth = 240;
        const int GbaHeight = 160;
        const int KeyHoldFrames = 4;

        public static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: OptimeGBA-LinkTest <rom-path> [screenshot-output-folder]");
                return 1;
            }

            string romPath = args[0];
            string screenshotFolder = args.Length > 1 ? args[1] : null;

            if (!File.Exists(romPath))
            {
                Console.Error.WriteLine($"ROM not found: {romPath}");
                return 1;
            }

            if (!File.Exists("gba_bios.bin"))
            {
                Console.Error.WriteLine("gba_bios.bin not found in current directory");
                return 1;
            }

            Run(romPath, screenshotFolder);
            return 0;
        }

        static void Run(string romPath, string screenshotFolder)
        {
            byte[] rom = File.ReadAllBytes(romPath);
            byte[] bios = File.ReadAllBytes("gba_bios.bin");

            string savPath1 = Path.GetTempFileName();
            string savPath2 = Path.GetTempFileName();

            var provider1 = new ProviderGba(bios, rom, savPath1, _ => { }) { BootBios = true };
            var provider2 = new ProviderGba(bios, rom, savPath2, _ => { }) { BootBios = true };

            var gba1 = new Gba(provider1);
            var gba2 = new Gba(provider2);

            var link = new GbaLink(gba1, gba2);

            Console.WriteLine("[LinkTest] Link established. Starting emulation.");

            var script = BuildKeyScript();
            int scriptIndex = 0;

            bool done = false;
            Gba[] gbas = { gba1, gba2 };

            var emuThread = new Thread(() => EmulationLoop(gbas, link, ref done));
            emuThread.Name = "LinkClock";
            emuThread.Start();

            while (!done && scriptIndex < script.Length)
            {
                long p0Ticks = gba1.Scheduler.CurrentTicks;
                var (targetTicks, action, key, press) = script[scriptIndex];

                if (p0Ticks >= targetTicks)
                {
                    if (press)
                    {
                        Console.WriteLine($"[KeyScript] @{p0Ticks / (double)CyclesPerSecond:F2}s Press {key} (both) [{action}]");
                    }
                    SetKey(gba1.Keypad, key, press);
                    SetKey(gba2.Keypad, key, press);
                    scriptIndex++;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            long endTicks = gba1.Scheduler.CurrentTicks + CyclesPerSecond * 2;
            Console.WriteLine("[LinkTest] Key script finished. Running 2 more emulated seconds...");
            while (gba1.Scheduler.CurrentTicks < endTicks)
            {
                Thread.Sleep(10);
            }

            done = true;
            emuThread.Join();

            if (screenshotFolder != null)
            {
                Directory.CreateDirectory(screenshotFolder);
                SaveScreenshot(gba1, Path.Combine(screenshotFolder, "p0.ppm"));
                SaveScreenshot(gba2, Path.Combine(screenshotFolder, "p1.ppm"));
                Console.WriteLine($"[LinkTest] Screenshots saved to {screenshotFolder}/");
            }

            try { File.Delete(savPath1); } catch { }
            try { File.Delete(savPath2); } catch { }

            Console.WriteLine("[LinkTest] Done.");
        }

        const int SyncChunkCycles = 2048;

        static void EmulationLoop(Gba[] gbas, GbaLink link, ref bool done)
        {
            int n = gbas.Length;
            long[] cyclesLeft = new long[n];

            // FPS measurement
            var stopwatch = Stopwatch.StartNew();
            long frameCount = 0;
            double lastFpsReport = 0;

            while (!Volatile.Read(ref done))
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
                    frameCount++;

                    // Report FPS every second
                    double elapsed = stopwatch.Elapsed.TotalSeconds;
                    if (elapsed - lastFpsReport >= 1.0)
                    {
                        double fps = frameCount / elapsed;
                        Console.WriteLine($"[Perf] {fps:F1} fps ({frameCount} frames in {elapsed:F1}s)");
                        lastFpsReport = elapsed;
                    }
                }

                // Single-threaded interleaved: run each GBA for a small chunk, alternating
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

                    if (link.ReadyToTransfer)
                    {
                        link.MultiplayerTransfer();
                    }
                }
            }

            Console.WriteLine($"[Perf] Final: {frameCount} frames in {stopwatch.Elapsed.TotalSeconds:F1}s = {frameCount / stopwatch.Elapsed.TotalSeconds:F1} fps");
        }

        enum KeyName { A, B, Start, Select, Up, Down, Left, Right }

        static (long ticks, string action, KeyName key, bool press)[] BuildKeyScript()
        {
            var events = new System.Collections.Generic.List<(long, string, KeyName, bool)>();
            long holdCycles = KeyHoldFrames * CyclesPerFrame;

            void AddPress(ref long t, string action, KeyName key)
            {
                events.Add((t, action, key, true));
                events.Add((t + holdCycles, action, key, false));
            }

            long time = 10 * CyclesPerSecond;
            AddPress(ref time, "menu A #1", KeyName.A);
            time += 1 * CyclesPerSecond;
            AddPress(ref time, "menu A #2", KeyName.A);
            time += 1 * CyclesPerSecond;
            AddPress(ref time, "menu A #3", KeyName.A);
            time += 1 * CyclesPerSecond;
            AddPress(ref time, "menu A #4", KeyName.A);
            time += 1 * CyclesPerSecond;
            AddPress(ref time, "menu Down", KeyName.Down);
            time += 1 * CyclesPerSecond;
            AddPress(ref time, "menu A #4 (start link)", KeyName.A);
            time += 5 * CyclesPerSecond;
            AddPress(ref time, "finish handshake A", KeyName.A);

            events.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return events.ToArray();
        }

        static void SetKey(Keypad keypad, KeyName key, bool pressed)
        {
            switch (key)
            {
                case KeyName.A: keypad.A = pressed; break;
                case KeyName.B: keypad.B = pressed; break;
                case KeyName.Start: keypad.Start = pressed; break;
                case KeyName.Select: keypad.Select = pressed; break;
                case KeyName.Up: keypad.Up = pressed; break;
                case KeyName.Down: keypad.Down = pressed; break;
                case KeyName.Left: keypad.Left = pressed; break;
                case KeyName.Right: keypad.Right = pressed; break;
            }
        }

        static unsafe void SaveScreenshot(Gba gba, string path)
        {
            var lut = PpuRenderer.ColorLut;
            using var fs = File.Create(path);
            using var sw = new StreamWriter(fs);

            sw.WriteLine("P3");
            sw.WriteLine($"{GbaWidth} {GbaHeight}");
            sw.WriteLine("255");

            for (int i = 0; i < GbaWidth * GbaHeight; i++)
            {
                uint pixel = lut[gba.Ppu.Renderer.ScreenFront[i] & 0x7FFF];
                byte r = (byte)(pixel & 0xFF);
                byte g = (byte)((pixel >> 8) & 0xFF);
                byte b = (byte)((pixel >> 16) & 0xFF);
                sw.Write($"{r} {g} {b} ");
                if (i % GbaWidth == GbaWidth - 1)
                {
                    sw.WriteLine();
                }
            }
        }
    }
}
