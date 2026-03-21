using System;
using System.Diagnostics;
using System.Text;
using OptimeGBA;

namespace OptimeGBAEmulator
{
    public sealed class TermControl : IDisposable
    {
        private bool disposedValue;
        private readonly int scaleX;
        private readonly int scaleY;
        private readonly StringBuilder sb = new();

        public TermControl(int scaleX, int scaleY)
        {
            Debug.Assert(scaleX >= 1 && scaleY >= 1);
            this.scaleX = scaleX;
            this.scaleY = scaleY * 2; // half-block encodes 2 vertical pixels per cell

            Console.Write("\u001B[?1049h"); // Enter alternate screen
            Console.Write("\u001B[?25l"); // Hide cursor
            Console.Out.Flush();
        }

        public void Display(int width, int height, Span<ushort> buffer)
        {
            Debug.Assert(buffer.Length == width * height);

            int outWidth = width / scaleX;
            int outHeight = height / scaleY;

            sb.Clear();
            sb.Append("\u001B[H"); // Reset cursor to top-left

            for (int row = 0; row < outHeight; row++)
            {
                int srcY0 = row * scaleY;
                int srcY1 = srcY0 + scaleY / 2; // midpoint splits top/bottom half-block

                for (int col = 0; col < outWidth; col++)
                {
                    int srcX = col * scaleX;

                    AverageBlock(buffer, width, srcX, srcY0, scaleX, scaleY / 2, out int fgR, out int fgG, out int fgB);
                    AverageBlock(buffer, width, srcX, srcY1, scaleX, scaleY / 2, out int bgR, out int bgG, out int bgB);

                    sb.Append($"\u001B[38;2;{fgR};{fgG};{fgB};48;2;{bgR};{bgG};{bgB}m\u2580");
                }

                sb.Append("\u001B[0m\n");
            }

            Console.Write(sb);
            Console.Out.Flush();
        }

        private static void AverageBlock(Span<ushort> buffer, int stride, int x0, int y0, int w, int h, out int r, out int g, out int b)
        {
            uint[] lut = PpuRenderer.ColorLutCorrected;
            int sumR = 0, sumG = 0, sumB = 0;
            int count = w * h;

            for (int dy = 0; dy < h; dy++)
            {
                int rowBase = (y0 + dy) * stride + x0;
                for (int dx = 0; dx < w; dx++)
                {
                    uint rgb888 = lut[buffer[rowBase + dx] & 0x7FFF];
                    sumR += (int)(rgb888 & 0xFF);
                    sumG += (int)((rgb888 >> 8) & 0xFF);
                    sumB += (int)((rgb888 >> 16) & 0xFF);
                }
            }

            r = sumR / count;
            g = sumG / count;
            b = sumB / count;
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                Console.Write("\u001B[?25h"); // Show cursor
                Console.Write("\u001B[?1049l"); // Leave alternate screen
                Console.Out.Flush();
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
