using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
namespace AsioRateMonitor {
    internal static class RateIcon
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        public static Color ColorFor(RateReading reading)
        {
            return reading.State != ReadingState.Available ? Color.FromArgb(170, 179, 191)
                : reading.Hertz == 48000 ? Color.FromArgb(108, 227, 165) : Color.FromArgb(255, 195, 106);
        }
        public static Icon Create(RateReading reading, int size)
        {
            string number = reading.State == ReadingState.Available ? reading.Kilohertz : "--";
            if (reading.Hertz >= 1000000) number = (reading.Hertz / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            using (Bitmap bitmap = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Brush background = new SolidBrush(ColorFor(reading)))
            using (Brush foreground = new SolidBrush(Color.FromArgb(16, 25, 35)))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.FillRectangle(background, 0, 1, size, size - 2);
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                float fontSize = size * 0.78f;
                while (fontSize > size * 0.26f)
                {
                    using (Font measure = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        if (g.MeasureString(number, measure, 1000, StringFormat.GenericTypographic).Width <= size - 1) break;
                    fontSize -= 0.5f;
                }
                using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    g.DrawString(number, font, foreground, new RectangleF(0, 0, size, size), format);
                IntPtr nativeIcon = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(nativeIcon).Clone(); }
                finally { DestroyIcon(nativeIcon); }
            }
        }
    }

}
