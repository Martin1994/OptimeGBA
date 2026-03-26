using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using OptimeGBA;
using SDL;

namespace OptimeGBASdl3
{
    public static class Program
    {
        public static readonly Dictionary<string, string> GameNameDictionary = new();
        public static readonly LinkClock MainClock = new();

        public static int Main(string[] args)
        {
            var romOption = new Option<string>("--rom") { Description = "Path to the ROM file to load" };
            var linkOption = new Option<int?>("--link") { Description = "Enable link play with 2-4 windows (default: 2)", Arity = ArgumentArity.ZeroOrOne };
            var linkStrategyOption = new Option<string>("--link-strategy") { Description = "Link sync strategy: barrier, single (default), spin", DefaultValueFactory = _ => "single" };

            var rootCommand = new RootCommand("OptimeGBA SDL3 Frontend");
            rootCommand.Add(romOption);
            rootCommand.Add(linkOption);
            rootCommand.Add(linkStrategyOption);

            var parseResult = rootCommand.Parse(args);
            if (parseResult.Errors.Count > 0 || parseResult.Action is System.CommandLine.Help.HelpAction)
            {
                return parseResult.Invoke();
            }

            string rom = parseResult.GetValue(romOption);
            int? linkRaw = parseResult.GetValue(linkOption);
            string linkStrategyStr = parseResult.GetValue(linkStrategyOption);

            int link;
            if (linkRaw.HasValue)
            {
                link = linkRaw.Value;
                if (link < 2 || link > 4)
                {
                    Console.Error.WriteLine($"--link must be between 2 and 4, got {link}.");
                    return 1;
                }
            }
            else if (args.Contains("--link"))
            {
                link = 2;
            }
            else
            {
                link = 1;
            }

            LinkSyncStrategy linkStrategy = linkStrategyStr switch
            {
                "single" => LinkSyncStrategy.SingleThread,
                "spin" => LinkSyncStrategy.Spin,
                _ => LinkSyncStrategy.Barrier,
            };

            Run(rom, link, linkStrategy);
            return 0;
        }

        static void Run(string rom, int windowCount, LinkSyncStrategy linkStrategy)
        {
            LoadNoIntroDatabase();

            if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_VIDEO))
            {
                Console.Error.WriteLine($"SDL_Init failed: {SDL3.SDL_GetError()}");
                return;
            }

            bool isLink = windowCount > 1;
            var windows = new EmulatorWindow[windowCount];
            for (int i = 0; i < windowCount; i++)
            {
                windows[i] = new EmulatorWindow();
                windows[i].Init(isLink);
                windows[i].Run(rom);
            }

            if (isLink)
            {
                MainClock.Strategy = linkStrategy;
                Console.WriteLine($"[Link] Sync strategy: {MainClock.Strategy}");
                _ = MainClock.Run();
                Task.Run(() => RunLink(windows));
            }

            RunEventLoop(windows);

            SDL3.SDL_Quit();
            Environment.Exit(0);
        }

        static void LoadNoIntroDatabase()
        {
            var stream = typeof(Program).Assembly.GetManifestResourceStream("OptimeGBA-SDL3.resources.no-intro.dat");
            if (stream == null) return;

            var doc = new XmlDocument();
            doc.Load(stream);
            foreach (XmlNode node in doc.GetElementsByTagName("game"))
            {
                var romNode = node.SelectNodes("rom")?[0];
                var serialAttr = romNode?.Attributes?["serial"];
                var nameAttr = node.Attributes?["name"];
                if (serialAttr != null && nameAttr != null)
                {
                    GameNameDictionary[serialAttr.Value] = nameAttr.Value;
                }
            }
        }

        static unsafe void RunEventLoop(EmulatorWindow[] windows)
        {
            while (windows.Any(w => !w.Closed))
            {
                SDL_Event evt;
                while (SDL3.SDL_PollEvent(&evt))
                {
                    var evtType = (SDL_EventType)evt.type;

                    if (evtType == SDL_EventType.SDL_EVENT_QUIT)
                    {
                        return;
                    }

                    var windowId = evt.window.windowID;
                    foreach (var w in windows)
                    {
                        if (!w.Closed && w.WindowId == windowId)
                        {
                            w.HandleEvent(&evt);
                            break;
                        }
                    }
                }

                foreach (var w in windows)
                {
                    w.Tick();
                }
            }
        }

        static void RunLink(EmulatorWindow[] windows)
        {
            while (true)
            {
                if (windows.All(w => w.Gba != null))
                {
                    var gbas = windows.Select(w => w.Gba).ToArray();
                    Thread.Sleep(100);
                    var link = new GbaLink(
                        gbas[0],
                        gbas[1],
                        gbas.Length > 2 ? gbas[2] : null,
                        gbas.Length > 3 ? gbas[3] : null
                    );
                    MainClock.Link = link;
                    return;
                }
                Thread.Sleep(1000);
            }
        }
    }
}
