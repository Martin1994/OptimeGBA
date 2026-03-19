using System;
using System.Collections.Generic;
using System.Linq;
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

        public static void Main(string[] args)
        {
            LoadNoIntroDatabase();

            if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_VIDEO))
            {
                Console.Error.WriteLine($"SDL_Init failed: {SDL3.SDL_GetError()}");
                return;
            }

            int windowCount = args.Contains("--link") ? 2 : 1;
            var windows = Enumerable.Range(0, windowCount).Select(_ => new EmulatorWindow()).ToArray();

            if (windows.Length > 1)
            {
                // Secondary windows and link on background threads
                var secondaryTasks = Task.WhenAll(windows.Skip(1).Select(w => Task.Run(() => w.Run(args))));
                var linkTask = Task.Run(() => RunLink(windows, () => secondaryTasks.IsCompleted));

                windows[0].Run(args);

                secondaryTasks.Wait();
                linkTask.Wait();
            }
            else
            {
                windows[0].Run(args);
            }

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
                    GameNameDictionary[serialAttr.Value] = nameAttr.Value;
            }
        }

        static void RunLink(EmulatorWindow[] windows, Func<bool> isDone)
        {
            if (windows.Length < 2) return;

            while (!isDone())
            {
                if (windows.All(w => w.Gba != null))
                {
                    var gbas = windows.Select(w => w.Gba).ToArray();
                    System.Threading.Thread.Sleep(100);
                    var link = new GbaLink(gbas[0], gbas[1], gbas.Length > 2 ? gbas[2] : null, gbas.Length > 3 ? gbas[3] : null);
                    MainClock.Link = link;
                    return;
                }
                System.Threading.Thread.Sleep(1000);
            }
        }
    }
}
