using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Rage.Monitor
{
    /// <summary>Marca do RAGE! desenhada em vetor (mesmo traçado de assets/rage-logo.svg), nítida em qualquer DPI.</summary>
    static class Brand
    {
        const string LogoPath = "M759.9,741.9c-4.9,0.2-7.8,0.5-10.8,0.5c-45.3,0-90.6-0.3-135.9,0.2c-16.8,0.2-29.7-6.5-40-18.8c-48.5-57.6-96.7-115.4-145-173.1c-2.1-2.5-4.4-4.8-7.5-8.1c-10.9,12.9-21.1,24.9-31.3,37c-16,19-32,37.8-47.7,57c-4.2,5.2-6.6,4.4-10.5-0.3c-17.6-21.2-35.6-42-53.2-63.2c-17-20.4-16.5-45.6,1.2-67.1c15.5-18.9,31.5-37.5,47.2-56.3c2.7-3.2,5.4-6.5,8.5-10.3c-27.8-33.3-55.3-66.2-84.4-101c5.4-0.4,8.3-0.8,11.2-0.8c158,0,315.9,0,473.9-0.1c16.9,0,30.1,7,40.7,19.5c13.8,16.4,27.4,32.9,41.1,49.4c17,20.4,16.3,46.9-0.7,67.1c-37.3,44.2-74.2,88.7-111.3,133.1c-7.5,8.9-14.7,18-22.6,26.5c-5.5,5.9-5.3,10.3,0,16.4c24.5,28.6,48.5,57.6,72.7,86.4C756.5,737.1,757.4,738.6,759.9,741.9z M424.6,537.7c1.8,0.7,2.6,1.3,3.4,1.3c51.7,0.1,103.4,0.2,155.1-0.2c3.8,0,8.6-3,11.3-6.1c14.3-16.3,28.1-33.1,42.1-49.7c11.3-13.5,22.5-27.1,34.5-41.5c-2.5-0.5-3.6-0.9-4.7-0.9c-51.4,0-102.8-0.1-154.2,0.3c-3.6,0-8.1,2.7-10.6,5.5c-13.6,15.4-26.6,31.1-39.8,46.8C449.4,507.9,437.2,522.6,424.6,537.7z";

        static GraphicsPath mark;

        /// <summary>Traçado do "R" em coordenadas 0..1080 (só usa M, c, C e z).</summary>
        static GraphicsPath Mark()
        {
            if (mark != null) return mark;
            var p = new GraphicsPath(FillMode.Alternate);
            float x = 0, y = 0;
            foreach (Match seg in Regex.Matches(LogoPath, @"([McCz])([^McCz]*)"))
            {
                var cmd = seg.Groups[1].Value[0];
                var n = new List<float>();
                foreach (Match num in Regex.Matches(seg.Groups[2].Value, @"-?\d*\.?\d+"))
                    n.Add(float.Parse(num.Value, CultureInfo.InvariantCulture));
                if (cmd == 'M') { p.StartFigure(); x = n[0]; y = n[1]; }
                else if (cmd == 'z') p.CloseFigure();
                else
                    for (int i = 0; i + 5 < n.Count; i += 6)
                    {
                        bool rel = cmd == 'c';
                        float x1 = n[i] + (rel ? x : 0), y1 = n[i + 1] + (rel ? y : 0);
                        float x2 = n[i + 2] + (rel ? x : 0), y2 = n[i + 3] + (rel ? y : 0);
                        float x3 = n[i + 4] + (rel ? x : 0), y3 = n[i + 5] + (rel ? y : 0);
                        p.AddBezier(x, y, x1, y1, x2, y2, x3, y3);
                        x = x3; y = y3;
                    }
            }
            return mark = p;
        }

        /// <summary>Ícone oficial: quadrado arredondado em degradê laranja com o "R" branco.</summary>
        public static void DrawLogo(Graphics g, RectangleF r)
        {
            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var box = Round(r, r.Width * 0.22f))
                using (var grad = new LinearGradientBrush(new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Top), Color.FromArgb(255, 127, 7), Color.FromArgb(255, 60, 50)))
                    g.FillPath(grad, box);
                g.TranslateTransform(r.X, r.Y);
                g.ScaleTransform(r.Width / 1080f, r.Height / 1080f);
                using (var white = new SolidBrush(Color.White)) g.FillPath(white, Mark());
            }
            finally { g.Restore(state); }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            var d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Cor de um valor em % no padrão do app: neutro, âmbar a partir de warn, vermelho a partir de danger.</summary>
        public static Color Zone(double? pct, double warn = 75, double danger = 90)
        {
            if (pct == null) return Color.FromArgb(150, 156, 168);
            if (pct >= danger) return Color.FromArgb(255, 92, 92);
            if (pct >= warn) return Color.FromArgb(255, 176, 84);
            return Color.FromArgb(245, 246, 248);
        }
    }
}
