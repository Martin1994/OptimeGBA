using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OptimeGBA;
using SDL;

namespace OptimeGBASdl3
{
    public unsafe class EmulatorWindow
    {
        const int GbaWidth = 240;
        const int GbaHeight = 160;
        const int NdsWidth = 256;
        const int NdsHeight = 192;

        const int CyclesPerFrameGba = 280896;
        const int CyclesPerFrameNds = 560190;
        const double SecondsPerFrameNds = 1.0 / (33513982.0 / 560190.0);

        const int LogoWidth = 34;
        const int LogoHeight = 21;
        const int LogoBpp = 4;
        const int LogoFrames = 8;
        const double SecondsPerFrameAnimation = 0.1;

        SDL_Window* window;
        SDL_Renderer* renderer;
        SDL_Texture* texture;
        SDL_AudioStream* audioStream;

        public Gba Gba;
        Nds Nds;
        bool ndsMode;
        bool linkMode;

        string romName;
        bool sync = true;
        bool integerScaling;
        bool isFullscreen;
        bool stretched;
        bool colorCorrection = true;

        long displaySeconds;
        double fps;
        double mips;

        Thread emulationThread;
        AutoResetEvent threadSync = new(false);
        int cyclesLeft;
        long cyclesRan;

        bool excepted;
        string exceptionMessage = "";

        uint[] displayBuf = new uint[NdsWidth * NdsHeight];

        bool lCtrl;
        bool lAlt;
        bool holdingSpace;
        bool holdingTab;
        bool resetDue;

        int ScreenWidth => ndsMode ? NdsWidth : GbaWidth;
        int ScreenHeight => ndsMode ? NdsHeight : GbaHeight;

        public SDL_WindowID WindowId { get; private set; }

        public void Init(bool isLinkMode = false)
        {
            linkMode = isLinkMode;

            window = SDL3.SDL_CreateWindow("Optime GBA"u8, GbaWidth * 4, GbaHeight * 4, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL3.SDL_GetError()}");
                return;
            }
            WindowId = SDL3.SDL_GetWindowID(window);
            SDL3.SDL_SetWindowPosition(window, (int)SDL3.SDL_WINDOWPOS_CENTERED, (int)SDL3.SDL_WINDOWPOS_CENTERED);
            SDL3.SDL_SetWindowMinimumSize(window, GbaWidth, GbaHeight);

            renderer = SDL3.SDL_CreateRenderer(window, (byte*)null);
            SDL3.SDL_SetRenderVSync(renderer, 1);

            InitAudio();

            // In link mode, LinkClock drives stepping — no per-window emulation thread
            if (!linkMode)
            {
                emulationThread = new Thread(EmulationThreadHandler) { Name = "Emulation Core" };
                emulationThread.Start();
            }
        }

        public bool Loaded { get; private set; }
        public bool Closed { get; private set; }

        string currentRomPath;
        double fpsEvalTimer;

        public void Run(string romPath)
        {
            if (window == null) { Closed = true; return; }
            currentRomPath = romPath;
        }

        public void Tick()
        {
            if (Closed) return;

            if (!Loaded)
            {
                if (currentRomPath == null)
                {
                    // File drop mode runs its own mini event loop
                    currentRomPath = RunFileDrop();
                    if (currentRomPath == null) { Close(); return; }
                }
                if (!LoadRom(currentRomPath)) { Close(); return; }
                fpsEvalTimer = GetTime();
                Loaded = true;
            }

            TickEmulator();
        }

        void Close()
        {
            Closed = true;
            if (renderer != null) SDL3.SDL_DestroyRenderer(renderer);
            if (window != null) SDL3.SDL_DestroyWindow(window);
            renderer = null;
            window = null;
        }

        // --- Audio ---

        void InitAudio()
        {
            var spec = new SDL_AudioSpec
            {
                channels = 2,
                freq = 32768,
                format = SDL_AudioFormat.SDL_AUDIO_S16LE,
            };
            audioStream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
            if (audioStream != null)
                SDL3.SDL_ResumeAudioStreamDevice(audioStream);
        }

        const int AudioSampleQueueThreshold = 1024;
        const int AudioSampleFlushThreshold = 8192;

        void AudioReady(short[] data)
        {
            if (audioStream == null) return;

            var flags = SDL3.SDL_GetWindowFlags(window);
            if ((flags & SDL_WindowFlags.SDL_WINDOW_MOUSE_FOCUS) == 0) return;

            int queuedSamples = SDL3.SDL_GetAudioStreamQueued(audioStream) / sizeof(short);

            // Flush the stream if audio drifted too far ahead of playback
            if (queuedSamples > AudioSampleFlushThreshold)
            {
                SDL3.SDL_ClearAudioStream(audioStream);
                queuedSamples = 0;
            }

            if (sync || queuedSamples < AudioSampleQueueThreshold)
            {
                fixed (short* ptr = data)
                    SDL3.SDL_PutAudioStreamData(audioStream, (IntPtr)ptr, data.Length * sizeof(short));
            }
        }

        int GetAudioSamplesQueued()
        {
            if (audioStream == null) return 0;
            return SDL3.SDL_GetAudioStreamQueued(audioStream) / sizeof(short);
        }

        // --- Boot animation (file drop screen) ---

        string RunFileDrop()
        {
            byte[][] frames = ReadAnimationFrames();
            var iconTexture = SDL3.SDL_CreateTexture(renderer, SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888, SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, LogoWidth, LogoHeight);
            SDL3.SDL_SetTextureScaleMode(iconTexture, SDL_ScaleMode.SDL_SCALEMODE_NEAREST);
            IntPtr data = Marshal.AllocHGlobal(frames[0].Length);

            int frame = 0;
            double timeNextFrame = 0;
            bool fileLoaded = false;
            string filename = null;

            SDL3.SDL_SetEventEnabled(SDL_EventType.SDL_EVENT_DROP_FILE, true);

            while (true)
            {
                Marshal.Copy(frames[frame], 0, data, frames[0].Length);
                SDL3.SDL_UpdateTexture(iconTexture, null, data, LogoWidth * LogoBpp);

                SDL_Event evt;
                while (SDL3.SDL_PollEvent(&evt))
                {
                    var evtType = (SDL_EventType)evt.type;
                    switch (evtType)
                    {
                        case SDL_EventType.SDL_EVENT_QUIT:
                            Marshal.FreeHGlobal(data);
                            return null;
                        case SDL_EventType.SDL_EVENT_DROP_FILE:
                            filename = Marshal.PtrToStringUTF8((IntPtr)evt.drop.data);
                            fileLoaded = true;
                            break;
                    }
                }

                int w, h;
                SDL3.SDL_GetWindowSize(window, &w, &h);
                var dest = FitRect(w, h, LogoWidth, LogoHeight, false, false);

                SDL3.SDL_RenderClear(renderer);
                SDL3.SDL_RenderTexture(renderer, iconTexture, null, &dest);
                SDL3.SDL_RenderPresent(renderer);

                if (!fileLoaded) continue;

                double now = GetTime();
                if (now - timeNextFrame >= SecondsPerFrameAnimation)
                    timeNextFrame = now;

                if (now >= timeNextFrame)
                {
                    timeNextFrame += SecondsPerFrameAnimation;
                    frame++;
                    if (frame == LogoFrames)
                    {
                        Marshal.FreeHGlobal(data);
                        return filename;
                    }
                }
            }
        }

        // --- Emulation per-tick (called from unified loop) ---

        public void HandleEvent(SDL_Event* evt)
        {
            if (Closed) return;
            var evtType = (SDL_EventType)evt->type;
            switch (evtType)
            {
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    Close();
                    break;
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    if (Loaded) HandleKeyEvent(evt->key);
                    break;
                case SDL_EventType.SDL_EVENT_DROP_FILE:
                    var path = Marshal.PtrToStringUTF8((IntPtr)evt->drop.data);
                    if (path != null)
                    {
                        currentRomPath = path;
                        Loaded = false;
                    }
                    break;
            }
        }

        void TickEmulator()
        {
            if (resetDue)
            {
                resetDue = false;
                ResetEmulation();
            }

            if (ndsMode)
                StepNdsFrame(ref fpsEvalTimer);
            else
                StepGbaFrame();

            BlitScreen();

            if (!ndsMode && Gba.Mem.SaveProvider.Dirty)
            {
                Gba.Mem.SaveProvider.Dirty = false;
                try { File.WriteAllBytesAsync(Gba.Provider.SavPath, Gba.Mem.SaveProvider.GetSave()); }
                catch { Console.WriteLine("Failed to write .sav file!"); }
            }

            if (excepted)
            {
                SDL3.SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "Exception Caught"u8, exceptionMessage, window);
                Close();
            }
        }

        bool LoadRom(string romPath)
        {
            if (!File.Exists(romPath))
            {
                Log("The ROM file you provided does not exist.");
                return false;
            }

            byte[] rom;
            try { rom = File.ReadAllBytes(romPath); }
            catch { Log("The ROM file you provided exists, but there was an issue loading it."); return false; }

            string savPath = romPath[..^3] + "sav";
            byte[] sav = Array.Empty<byte>();
            if (File.Exists(savPath))
            {
                Log(".sav exists, loading");
                try { sav = File.ReadAllBytes(savPath); }
                catch { Log("Failed to load .sav file!"); }
            }
            else
            {
                Log(".sav not available");
            }

            ndsMode = romPath[^3..].Equals("nds", StringComparison.OrdinalIgnoreCase);

            if (ndsMode)
                return LoadNds(rom, savPath);
            else
                return LoadGba(rom, sav, savPath, romPath);
        }

        bool LoadGba(byte[] rom, byte[] sav, string savPath, string romPath)
        {
            const string biosPath = "gba_bios.bin";
            if (!File.Exists(biosPath))
            {
                ShowMessage("Please place a valid GBA BIOS named \"gba_bios.bin\" in the working directory.");
                return false;
            }

            byte[] bios;
            try { bios = File.ReadAllBytes(biosPath); }
            catch { ShowMessage("A GBA BIOS was provided, but there was an issue loading it."); return false; }

            var provider = new ProviderGba(bios, rom, savPath, AudioReady) { BootBios = true };
            Gba = new Gba(provider);

            if (linkMode)
            {
                Program.MainClock.Register(Gba);
            }

            romName = Program.GameNameDictionary.TryGetValue(Gba.Provider.RomId, out var name) ? name : Path.GetFileName(romPath);
            Gba.Mem.SaveProvider.LoadSave(sav);

            RecreateTexture(GbaWidth, GbaHeight);
            return true;
        }

        bool LoadNds(byte[] rom, string savPath)
        {
            const string bios7Path = "bios7.bin";
            const string bios9Path = "bios9.bin";
            const string fwPath = "firmware.bin";

            if (!File.Exists(bios7Path) || !File.Exists(bios9Path) || !File.Exists(fwPath))
            {
                ShowMessage("Please place valid NDS BIOSes and firmware named \"bios7.bin\", \"bios9.bin\", and \"firmware.bin\" in the working directory.");
                return false;
            }

            byte[] bios7, bios9, firmware;
            try
            {
                bios7 = File.ReadAllBytes(bios7Path);
                bios9 = File.ReadAllBytes(bios9Path);
                firmware = File.ReadAllBytes(fwPath);
            }
            catch { ShowMessage("NDS BIOSes/firmware were provided, but there was an issue loading them."); return false; }

            var provider = new ProviderNds(bios7, bios9, firmware, rom, savPath, AudioReady) { DirectBoot = true };
            Nds = new Nds(provider);

            RecreateTexture(NdsWidth, NdsHeight);
            return true;
        }

        void RecreateTexture(int w, int h)
        {
            if (texture != null)
                SDL3.SDL_DestroyTexture(texture);
            texture = SDL3.SDL_CreateTexture(renderer, SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888, SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, w, h);
            SDL3.SDL_SetTextureScaleMode(texture, SDL_ScaleMode.SDL_SCALEMODE_NEAREST);
            SDL3.SDL_SetWindowMinimumSize(window, w, h);
            SDL3.SDL_SetWindowSize(window, w * 4, h * 4);
        }

        void ResetEmulation()
        {
            if (ndsMode)
            {
                var p = Nds.Provider;
                Nds = new Nds(p);
            }
            else
            {
                byte[] save = Gba.Mem.SaveProvider.GetSave();
                var p = Gba.Provider;
                Gba = new Gba(p);
                Gba.Mem.SaveProvider.LoadSave(save);
            }
        }

        // --- Per-frame stepping ---

        const double SecondsPerFrameGba = 1.0 / (16777216.0 / 280896.0);

        double gbaNextFrameAt;
        double gbaFpsEvalTimer;
        long gbaFrameCount;

        void StepGbaFrame()
        {
            double now = GetTime();

            // In link mode, LinkClock drives stepping — just poll for rendered frames
            if (!linkMode)
            {
                if (now - gbaNextFrameAt >= SecondsPerFrameGba * 2)
                    gbaNextFrameAt = now;

                if (now >= gbaNextFrameAt)
                {
                    gbaNextFrameAt += SecondsPerFrameGba;
                    threadSync.Set();
                }
            }

            if (Gba.Ppu.Renderer.RenderingDone)
            {
                Gba.Ppu.Renderer.RenderingDone = false;
                gbaFrameCount++;
                CopyPixels(Gba.Ppu.Renderer.ScreenFront, GbaWidth * GbaHeight, colorCorrection);
            }

            if (gbaFpsEvalTimer == 0) gbaFpsEvalTimer = now;
            if (now >= gbaFpsEvalTimer + 1.0)
            {
                double elapsed = now - gbaFpsEvalTimer;
                fps = Math.Floor(gbaFrameCount / elapsed * 100) / 100;
                double ran = Gba.Cpu.InstructionsRan / 1_000_000.0;
                Gba.Cpu.InstructionsRan = 0;
                mips = Math.Floor(ran / elapsed * 100) / 100;
                gbaFrameCount = 0;
                gbaFpsEvalTimer = now;
                displaySeconds += (long)elapsed;
                UpdateTitle();
            }
        }

        double ndsNextFrameAt;

        void StepNdsFrame(ref double fpsEvalTimer)
        {
            double now = GetTime();

            if (now - ndsNextFrameAt >= SecondsPerFrameNds)
                ndsNextFrameAt = now;

            if (now >= ndsNextFrameAt)
            {
                ndsNextFrameAt += SecondsPerFrameNds;
                threadSync.Set();
            }

            if (now >= fpsEvalTimer)
            {
                double diff = now - fpsEvalTimer + 1;
                double frames = cyclesRan / (double)CyclesPerFrameNds;
                cyclesRan = 0;

                long ran = Nds.Cpu7.InstructionsRan + Nds.Cpu9.InstructionsRan;
                Nds.Cpu7.InstructionsRan = 0;
                Nds.Cpu9.InstructionsRan = 0;

                fps = Math.Floor(frames / diff * 100) / 100;
                mips = Math.Floor(ran / 1_000_000.0 / diff * 100) / 100;
                UpdateTitle();
                displaySeconds++;
                fpsEvalTimer += 1;
            }

            if (Nds.Ppu.Renderers[0].RenderingDone)
            {
                Nds.Ppu.Renderers[0].RenderingDone = false;
                CopyPixels(Nds.Ppu.Renderers[0].ScreenFront, NdsWidth * NdsHeight, false);
            }
        }

        // --- Rendering ---

        void CopyPixels(ushort[] src, int count, bool corrected)
        {
            var lut = corrected ? PpuRenderer.ColorLutCorrected : PpuRenderer.ColorLut;
            for (int i = 0; i < count; i++)
                displayBuf[i] = lut[src[i] & 0x7FFF];
        }

        void CopyPixels(ushort* src, int count, bool corrected)
        {
            var lut = corrected ? PpuRenderer.ColorLutCorrected : PpuRenderer.ColorLut;
            for (int i = 0; i < count; i++)
                displayBuf[i] = lut[src[i] & 0x7FFF];
        }

        void BlitScreen()
        {
            fixed (uint* ptr = displayBuf)
                SDL3.SDL_UpdateTexture(texture, null, (IntPtr)ptr, ScreenWidth * 4);

            int w, h;
            SDL3.SDL_GetWindowSize(window, &w, &h);
            var dest = FitRect(w, h, ScreenWidth, ScreenHeight, integerScaling, stretched);

            SDL3.SDL_RenderClear(renderer);
            SDL3.SDL_RenderTexture(renderer, texture, null, &dest);
            SDL3.SDL_RenderPresent(renderer);
        }

        static SDL_FRect FitRect(int windowW, int windowH, int contentW, int contentH, bool integer, bool stretch)
        {
            if (stretch)
                return new SDL_FRect { x = 0, y = 0, w = windowW, h = windowH };

            double ratio = Math.Min((double)windowH / contentH, (double)windowW / contentW);
            int fillW, fillH;
            if (integer)
            {
                fillW = ((int)(ratio * contentW) / contentW) * contentW;
                fillH = ((int)(ratio * contentH) / contentH) * contentH;
            }
            else
            {
                fillW = (int)(ratio * contentW);
                fillH = (int)(ratio * contentH);
            }
            return new SDL_FRect
            {
                x = (windowW - fillW) / 2f,
                y = (windowH - fillH) / 2f,
                w = fillW,
                h = fillH,
            };
        }

        // --- Input ---

        void HandleKeyEvent(SDL_KeyboardEvent kb)
        {
            bool pressed = kb.down;
            var key = kb.scancode;

            if (ndsMode)
                HandleNdsInput(key, pressed);
            else
                HandleGbaInput(key, pressed);

            HandleGlobalInput(key, pressed);
        }

        void HandleGbaInput(SDL_Scancode key, bool pressed)
        {
            switch (key)
            {
                case SDL_Scancode.SDL_SCANCODE_Z: Gba.Keypad.B = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_X: Gba.Keypad.A = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_BACKSPACE: Gba.Keypad.Select = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_RETURN when !lAlt: Gba.Keypad.Start = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_KP_ENTER when !lAlt: Gba.Keypad.Start = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_LEFT: Gba.Keypad.Left = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_RIGHT: Gba.Keypad.Right = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_UP: Gba.Keypad.Up = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_DOWN: Gba.Keypad.Down = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_Q: Gba.Keypad.L = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_E: Gba.Keypad.R = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_LCTRL: lCtrl = pressed; break;
            }

            if (!pressed) return;

            switch (key)
            {
                case SDL_Scancode.SDL_SCANCODE_F1: colorCorrection = !colorCorrection; break;
                case SDL_Scancode.SDL_SCANCODE_F2: Gba.Ppu.Renderer.DebugEnableRendering = !Gba.Ppu.Renderer.DebugEnableRendering; break;
                case SDL_Scancode.SDL_SCANCODE_F3: Gba.GbaAudio.DebugEnableA = !Gba.GbaAudio.DebugEnableA; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_F4: Gba.GbaAudio.DebugEnableB = !Gba.GbaAudio.DebugEnableB; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_F5: Gba.GbaAudio.GbAudio.enable1Out = !Gba.GbaAudio.GbAudio.enable1Out; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_F6: Gba.GbaAudio.GbAudio.enable2Out = !Gba.GbaAudio.GbAudio.enable2Out; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_F7: Gba.GbaAudio.GbAudio.enable3Out = !Gba.GbaAudio.GbAudio.enable3Out; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_F8: Gba.GbaAudio.GbAudio.enable4Out = !Gba.GbaAudio.GbAudio.enable4Out; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_F9: Gba.GbaAudio.Resample = !Gba.GbaAudio.Resample; UpdateTitle(); break;
                case SDL_Scancode.SDL_SCANCODE_LEFTBRACKET:
                    if (Gba.GbaAudio.GbAudio.PsgFactor > 0) { Gba.GbaAudio.GbAudio.PsgFactor--; UpdateTitle(); }
                    break;
                case SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET:
                    Gba.GbaAudio.GbAudio.PsgFactor++;
                    UpdateTitle();
                    break;
            }
        }

        void HandleNdsInput(SDL_Scancode key, bool pressed)
        {
            switch (key)
            {
                case SDL_Scancode.SDL_SCANCODE_Z: Nds.Keypad.B = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_X: Nds.Keypad.A = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_BACKSPACE: Nds.Keypad.Select = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_RETURN when !lAlt: Nds.Keypad.Start = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_KP_ENTER when !lAlt: Nds.Keypad.Start = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_LEFT: Nds.Keypad.Left = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_RIGHT: Nds.Keypad.Right = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_UP: Nds.Keypad.Up = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_DOWN: Nds.Keypad.Down = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_Q: Nds.Keypad.L = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_E: Nds.Keypad.R = pressed; break;
                case SDL_Scancode.SDL_SCANCODE_LCTRL: lCtrl = pressed; break;
            }
        }

        void HandleGlobalInput(SDL_Scancode key, bool pressed)
        {
            switch (key)
            {
                case SDL_Scancode.SDL_SCANCODE_SPACE when !lCtrl:
                    holdingSpace = pressed;
                    sync = !(holdingSpace || holdingTab);
                    break;
                case SDL_Scancode.SDL_SCANCODE_TAB when !lCtrl:
                    holdingTab = pressed;
                    sync = !(holdingSpace || holdingTab);
                    break;
                case SDL_Scancode.SDL_SCANCODE_TAB when lCtrl && pressed:
                    sync = !sync;
                    break;
                case SDL_Scancode.SDL_SCANCODE_LALT:
                    lAlt = pressed;
                    break;
                case SDL_Scancode.SDL_SCANCODE_R when lCtrl:
                    resetDue = true;
                    break;
                case SDL_Scancode.SDL_SCANCODE_I when pressed:
                    integerScaling = !integerScaling;
                    break;
                case SDL_Scancode.SDL_SCANCODE_U when pressed:
                    stretched = !stretched;
                    break;
                case SDL_Scancode.SDL_SCANCODE_RETURN when pressed && lAlt:
                case SDL_Scancode.SDL_SCANCODE_KP_ENTER when pressed && lAlt:
                case SDL_Scancode.SDL_SCANCODE_F11 when pressed:
                    ToggleFullscreen();
                    break;
            }
        }

        void ToggleFullscreen()
        {
            isFullscreen = !isFullscreen;
            SDL3.SDL_SetWindowFullscreen(window, isFullscreen);
        }

        // --- Window title ---

        void UpdateTitle()
        {
            string title;
            if (ndsMode)
            {
                title = $"Optime GBA (DS) - {fps} fps - {mips} MIPS - {GetAudioSamplesQueued()} samples queued";
            }
            else
            {
                string flags =
                    (Gba.GbaAudio.DebugEnableA ? "A " : "- ") +
                    (Gba.GbaAudio.DebugEnableB ? "B " : "- ") +
                    (Gba.GbaAudio.GbAudio.enable1Out ? "1 " : "- ") +
                    (Gba.GbaAudio.GbAudio.enable2Out ? "2 " : "- ") +
                    (Gba.GbaAudio.GbAudio.enable3Out ? "3 " : "- ") +
                    (Gba.GbaAudio.GbAudio.enable4Out ? "4 " : "- ") +
                    (Gba.GbaAudio.Resample ? "RE " : "-- ") +
                    $"PSG {Gba.GbaAudio.GbAudio.PsgFactor}X";
                title = $"Optime GBA [{Gba.Serial.SioMultiplayerFlags.PlayerId}] - {fps} fps - {mips} MIPS - {GetAudioSamplesQueued()} samples queued | {flags}";
            }
            SDL3.SDL_SetWindowTitle(window, title);
        }

        // --- Emulation thread ---

        void EmulationThreadHandler()
        {
            try
            {
                if (linkMode)
                {
                    // Wait for ROM and link to be established before running.
                    // If we run frames before Link is set, the game tries to
                    // handshake with Link==null and gives up.
                    while (Gba == null || Gba.Serial.Link == null)
                    {
                        Thread.Sleep(50);
                    }
                }

                while (true)
                {
                    threadSync.WaitOne();
                    RunFrame();
                    while (!sync) RunFrame();
                }
            }
            catch (Exception e)
            {
                exceptionMessage = e.ToString();
                excepted = true;
            }
        }

        void RunFrame()
        {
            if (ndsMode)
            {
                cyclesRan += CyclesPerFrameNds;
                cyclesLeft += CyclesPerFrameNds;
                while (cyclesLeft > 0)
                    cyclesLeft -= (int)Nds.Step();
            }
            else
            {
                cyclesRan += CyclesPerFrameGba;
                cyclesLeft += CyclesPerFrameGba;
                while (cyclesLeft > 0)
                {
                    cyclesLeft -= (int)Gba.StateStep();
                }
            }
        }

        // --- Utilities ---

        static double GetTime()
        {
            return (double)SDL3.SDL_GetPerformanceCounter() / SDL3.SDL_GetPerformanceFrequency();
        }

        void ShowMessage(string msg)
        {
            SDL3.SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "Error"u8, msg, window);
        }

        void Log(string msg)
        {
            Console.WriteLine($"[Optime GBA] {msg}");
        }

        static byte[][] ReadAnimationFrames()
        {
            var buf = new byte[LogoFrames][];
            for (int i = 0; i < LogoFrames; i++)
                buf[i] = ReadResource($"OptimeGBA-SDL3.resources.animation.{i}.raw");
            return buf;
        }

        static byte[] ReadResource(string name)
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(name);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
