using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskbarDock
{
    internal static class ShellIconLoader
    {
        private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
        private const uint SIIGBF_ICONONLY = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path,
            IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

        public static Bitmap Load(string appId, string displayName, int size)
        {
            string target = ResolveTarget(appId);
            Bitmap image = TryLoadShellImage(target, size);
            if (image != null)
                return TrimPadding(image, size);

            string shortcut = FindPinnedShortcut(displayName);
            image = TryLoadShellImage(shortcut, size);
            if (image != null)
                return TrimPadding(image, size);

            if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                try
                {
                    using (Icon icon = Icon.ExtractAssociatedIcon(target))
                        return new Bitmap(icon.ToBitmap(), new Size(size, size));
                }
                catch (Exception) { }
            }

            using (Icon fallback = (Icon)SystemIcons.Application.Clone())
                return new Bitmap(fallback.ToBitmap(), new Size(size, size));
        }

        private static string ResolveTarget(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return null;
            if (File.Exists(appId))
                return appId;

            if (appId.StartsWith("*PID", StringComparison.OrdinalIgnoreCase))
            {
                int processId;
                if (int.TryParse(
                    appId.Substring(4),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out processId))
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(processId))
                            return process.MainModule.FileName;
                    }
                    catch (Exception) { }
                }
            }
            return "shell:AppsFolder\\" + appId;
        }

        private static Bitmap TryLoadShellImage(string target, int size)
        {
            if (string.IsNullOrEmpty(target))
                return null;

            IShellItemImageFactory factory = null;
            try
            {
                Guid interfaceId = typeof(IShellItemImageFactory).GUID;
                int result = SHCreateItemFromParsingName(
                    target, IntPtr.Zero, interfaceId, out factory);
                if (result != 0 || factory == null)
                    return null;

                IntPtr bitmapHandle;
                result = factory.GetImage(
                    new NativeSize { Width = size, Height = size },
                    SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
                    out bitmapHandle);
                if (result != 0 || bitmapHandle == IntPtr.Zero)
                    return null;

                try
                {
                    return FromHBitmapWithAlpha(bitmapHandle);
                }
                finally
                {
                    NativeMethods.DeleteObject(bitmapHandle);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (factory != null && Marshal.IsComObject(factory))
                    Marshal.FinalReleaseComObject(factory);
            }
        }

        /// <summary>
        /// Some apps only ship a small icon, which the shell centres inside a large transparent
        /// canvas. Enlarge the visible part so it matches the size of its neighbours.
        /// </summary>
        private static Bitmap TrimPadding(Bitmap image, int size)
        {
            // Above the faint 1px frame the shell paints around small legacy icons.
            const int VisibleAlpha = 96;
            int left = image.Width, top = image.Height, right = -1, bottom = -1;
            BitmapData data = image.LockBits(
                new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[image.Width];
                for (int y = 0; y < image.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, image.Width);
                    for (int x = 0; x < image.Width; x++)
                    {
                        if (((row[x] >> 24) & 0xFF) < VisibleAlpha)
                            continue;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }

            int extent = Math.Max(right - left + 1, bottom - top + 1);
            if (right < 0 || extent >= image.Width * 0.6f)
                return image;

            // Keep the content centred in a square crop with a small margin.
            int side = Math.Min(image.Width, (int)Math.Ceiling(extent * 1.12f));
            int cropX = Math.Max(0, Math.Min(image.Width - side, (left + right + 1 - side) / 2));
            int cropY = Math.Max(0, Math.Min(image.Height - side, (top + bottom + 1 - side) / 2));
            var result = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(image, new Rectangle(0, 0, size, size),
                    new Rectangle(cropX, cropY, side, side), GraphicsUnit.Pixel);
            }
            image.Dispose();
            return result;
        }

        /// <summary>
        /// Image.FromHbitmap drops the alpha channel, which turns transparent icon areas
        /// black. Copy the DIB bits ourselves and keep the alpha.
        /// </summary>
        private static Bitmap FromHBitmapWithAlpha(IntPtr bitmapHandle)
        {
            NativeMethods.BITMAP info;
            if (NativeMethods.GetObject(bitmapHandle, Marshal.SizeOf(typeof(NativeMethods.BITMAP)), out info) == 0 ||
                info.BitsPixel != 32 || info.Width <= 0 || info.Height <= 0)
            {
                using (Image source = Image.FromHbitmap(bitmapHandle))
                    return new Bitmap(source);
            }

            int width = info.Width;
            int height = info.Height;
            var pixels = new int[width * height];
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER)),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            };
            IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (NativeMethods.GetDIBits(dc, bitmapHandle, 0, (uint)height, pixels,
                        ref header, NativeMethods.DIB_RGB_COLORS) == 0)
                    return null;
            }
            finally
            {
                NativeMethods.DeleteDC(dc);
            }

            bool hasAlpha = false;
            bool premultiplied = true;
            foreach (int pixel in pixels)
            {
                int alpha = (pixel >> 24) & 0xFF;
                if (alpha != 0)
                    hasAlpha = true;
                if (((pixel >> 16) & 0xFF) > alpha || ((pixel >> 8) & 0xFF) > alpha || (pixel & 0xFF) > alpha)
                    premultiplied = false;
            }
            if (!hasAlpha)
            {
                for (int index = 0; index < pixels.Length; index++)
                    pixels[index] |= unchecked((int)0xFF000000);
                premultiplied = false;
            }

            PixelFormat format = premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb;
            var result = new Bitmap(width, height, format);
            BitmapData data = result.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, format);
            try
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * width, data.Scan0 + row * data.Stride, width);
            }
            finally
            {
                result.UnlockBits(data);
            }
            return result;
        }

        private static string FindPinnedShortcut(string displayName)
        {
            try
            {
                string pinned = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(pinned))
                    return null;

                string normalizedName = Normalize(displayName);
                foreach (string shortcut in Directory.GetFiles(pinned, "*.lnk"))
                {
                    string shortcutName = Normalize(Path.GetFileNameWithoutExtension(shortcut));
                    if (shortcutName == normalizedName ||
                        shortcutName.Contains(normalizedName) ||
                        normalizedName.Contains(shortcutName))
                        return shortcut;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace(" ", string.Empty)
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
        }
    }
}
