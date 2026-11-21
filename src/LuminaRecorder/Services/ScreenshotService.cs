using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace LuminaRecorder.Services
{
    public static class ScreenshotService
    {
        /// <summary>Captures the target area (physical pixels) to a PNG file and optionally the clipboard.</summary>
        public static string Capture(CaptureTarget target, AppSettings settings)
        {
            var bounds = target.Bounds;
            if (bounds.IsEmpty) throw new InvalidOperationException("ไม่พบพื้นที่ที่จะจับภาพ");

            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    // Copy monitor by monitor so gaps between screens of different sizes stay black.
                    foreach (var m in target.Monitors.Where(m => m.Bounds.IntersectsWith(bounds)))
                    {
                        var part = m.Bounds.Intersect(bounds);
                        g.CopyFromScreen(part.X, part.Y, part.X - bounds.X, part.Y - bounds.Y,
                            new Size(part.Width, part.Height), CopyPixelOperation.SourceCopy);
                    }
                }

                var path = RecorderService.NewFileName(settings.EffectiveOutputFolder, "png");
                bmp.Save(path, ImageFormat.Png);

                if (settings.CopyScreenshotToClipboard)
                {
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            ms.Position = 0;
                            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(ms,
                                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                            System.Windows.Clipboard.SetImage(decoder.Frames[0]);
                        }
                    }
                    catch { /* clipboard busy */ }
                }
                return path;
            }
        }
    }
}
