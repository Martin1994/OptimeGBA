using System;
using System.Diagnostics;
using System.Linq;

namespace OptimeGBAEmulator
{
    public sealed class TermControl : IDisposable
    {
        private bool disposedValue;
        private readonly string[] colorPalette = new string[0b1000000000000000]; // 15 bit color

        public TermControl(string[] grayscale)
        {
            Debug.Assert(grayscale.All(str => str.Length == grayscale[0].Length));

            Console.WriteLine("\u001B[?47h"); // Enter alternate screen
            Console.WriteLine("\u001B[?25l"); // Hide cursor

            // Build color palette
            for (uint i = 0; i < colorPalette.Length; i++) {
                colorPalette[i] = Rgb555ToMonoChar(i, grayscale);
            }
        }

        public void Display(int width, int height, Span<ushort> buffer)
        {
            Debug.Assert(buffer.Length == width * height);

            Console.Out.Write("\u001B[0;0f"); // Reset cursor

            int charWidth = colorPalette[0].Length;

            Span<char> lineBuffer = stackalloc char[charWidth * width + Console.Out.NewLine.Length];
            Console.Out.NewLine.AsSpan().CopyTo(lineBuffer[^Console.Out.NewLine.Length ..]);

            for (int line = 0; line < height; line++)
            {
                for (int i = 0; i < width; i++)
                {
                    colorPalette[buffer[width * line + i] & 0x7FFF].AsSpan().CopyTo(lineBuffer[(i * charWidth) ..]);
                }
                Console.Out.Write(lineBuffer);
            }
            Console.Out.Flush();
        }

        private static string Rgb555ToMonoChar(uint data, string[] grayscale)
        {
            double r = (double)((data >> 0) & 0b11111) / 0b11111;
            double g = (double)((data >> 5) & 0b11111) / 0b11111;
            double b = (double)((data >> 10) & 0b11111) / 0b11111;

            // https://docs.microsoft.com/en-us/previous-versions/bb332387(v=msdn.10)?redirectedfrom=MSDN#grayscale-conversion
            double luminance = Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0d, 0.99999d);

            return grayscale[(uint)(luminance * grayscale.Length)];
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state
                }

                // free unmanaged resources
                Console.WriteLine("\u001B[?47l"); // Leave alternate screen

                disposedValue = true;
            }
        }

        ~TermControl()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
