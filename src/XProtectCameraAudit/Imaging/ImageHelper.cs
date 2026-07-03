using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace XProtectCameraAudit.Imaging
{
    /// <summary>
    /// Port of ANPRMib's ResizeImageBase64 (System.Drawing, JPEG q=70) plus the
    /// self-contained placeholder (§4): inline SVG data URI, no external img src.
    /// </summary>
    public static class ImageHelper
    {
        public static string ResizeToBase64Jpeg(byte[] jpegBytes, int targetWidth, out string sourceResolution)
        {
            sourceResolution = "";
            using (var input = new MemoryStream(jpegBytes))
            using (var image = Image.FromStream(input))
            {
                sourceResolution = image.Width + "x" + image.Height;

                int width = Math.Min(targetWidth, image.Width);
                int height = Math.Max(1, (int)Math.Round(image.Height * (width / (double)image.Width)));

                using (var thumb = new Bitmap(width, height))
                {
                    using (var g = Graphics.FromImage(thumb))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(image, 0, 0, width, height);
                    }

                    var codec = ImageCodecInfo.GetImageEncoders()
                        .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    // Fully qualified: System.Text.Encoder is also in scope via usings.
                    using (var parameters = new EncoderParameters(1))
                    using (var output = new MemoryStream())
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                        thumb.Save(output, codec, parameters);
                        return Convert.ToBase64String(output.ToArray());
                    }
                }
            }
        }

        /// <summary>data:image/svg+xml;base64 camera icon with the failure reason overlaid.</summary>
        public static string PlaceholderDataUri(string reason)
        {
            string safe = XmlEscape(Truncate(reason ?? "", 48));
            string svg =
                "<svg xmlns='http://www.w3.org/2000/svg' width='320' height='180' viewBox='0 0 320 180'>" +
                "<rect width='320' height='180' fill='#2b3238'/>" +
                "<g fill='none' stroke='#6d7a85' stroke-width='4' stroke-linejoin='round'>" +
                "<rect x='108' y='68' width='72' height='44' rx='6'/>" +
                "<path d='M180 82 L208 70 L208 110 L180 98 Z'/>" +
                "</g>" +
                "<line x1='96' y1='128' x2='224' y2='52' stroke='#c62828' stroke-width='5' stroke-linecap='round'/>" +
                "<text x='160' y='152' font-family='Segoe UI,Arial,sans-serif' font-size='13' fill='#aeb9c2' text-anchor='middle'>" +
                safe + "</text></svg>";
            return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        }

        static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        static string XmlEscape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
