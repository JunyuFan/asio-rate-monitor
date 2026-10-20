using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AsioRateMonitor
{
    internal static class LayeredReadoutRenderer
    {
        [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend
        { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, ImageSize;
            public int XPixelsPerMeter, YPixelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(
            IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
            IntPtr window, IntPtr screenDC, IntPtr destination, ref Size size, IntPtr sourceDC,
            ref TaskbarNative.Point source, uint colorKey, ref Blend blend, uint flags);

        internal static Bitmap Draw(int width, int height, string text, Font font, Color color)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                // Alpha 0 would let mouse input pass to Explorer. Alpha 1 keeps
                // the whole rectangle clickable with only 1/255 background tint.
                graphics.Clear(Color.FromArgb(1, 0, 0, 0));
                // Grayscale antialiasing works on a transparent surface; ClearType
                // assumes an opaque background and would leave colored edges.
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(text, font, brush, new Rectangle(0, 0, width, height), format);
            }
            return bitmap;
        }

        internal static void Present(IntPtr window, Bitmap bitmap)
        {
            IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                memory = CreateCompatibleDC(screen);
                BitmapInfo info = new BitmapInfo {
                    Size = (uint)Marshal.SizeOf(typeof(BitmapInfo)), Width = bitmap.Width, Height = -bitmap.Height,
                    Planes = 1, BitCount = 32
                };
                IntPtr pixels;
                dib = CreateDIBSection(screen, ref info, 0, out pixels, IntPtr.Zero, 0);
                if (memory == IntPtr.Zero || dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(memory, dib);
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    byte[] row = new byte[bitmap.Width * 4];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(pixels, y * row.Length), row.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                Size size = new Size { Width = bitmap.Width, Height = bitmap.Height };
                TaskbarNative.Point source = new TaskbarNative.Point();
                Blend blend = new Blend { ConstantAlpha = 255, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(window, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
