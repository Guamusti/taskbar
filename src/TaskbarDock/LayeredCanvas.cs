using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TaskbarDock
{
    /// <summary>
    /// A premultiplied 32-bit DIB that GDI+ draws into and UpdateLayeredWindow presents.
    /// </summary>
    internal sealed class LayeredCanvas : IDisposable
    {
        private IntPtr memoryDc;
        private IntPtr dib;
        private IntPtr previousObject;
        private Bitmap bitmap;
        private Graphics graphics;
        private int capacityWidth;
        private int capacityHeight;
        private int width;
        private int height;

        public Graphics Begin(int requiredWidth, int requiredHeight)
        {
            if (requiredWidth > capacityWidth || requiredHeight > capacityHeight)
                Allocate(Math.Max(requiredWidth + 160, capacityWidth),
                         Math.Max(requiredHeight + 80, capacityHeight));
            width = requiredWidth;
            height = requiredHeight;
            graphics.ResetClip();
            graphics.SetClip(new Rectangle(0, 0, width, height));
            graphics.Clear(Color.Transparent);
            return graphics;
        }

        public void Present(IntPtr window, Point screenLocation, byte opacity = 255)
        {
            graphics.Flush(FlushIntention.Sync);
            var destination = new NativeMethods.POINT { X = screenLocation.X, Y = screenLocation.Y };
            var size = new NativeMethods.SIZE { Width = width, Height = height };
            var source = new NativeMethods.POINT();
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                SourceConstantAlpha = opacity,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };
            NativeMethods.UpdateLayeredWindow(
                window, IntPtr.Zero, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }

        private void Allocate(int newWidth, int newHeight)
        {
            Release();
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER)),
                Width = newWidth,
                Height = -newHeight,
                Planes = 1,
                BitCount = 32
            };
            IntPtr bits;
            memoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            dib = NativeMethods.CreateDIBSection(
                memoryDc, ref header, NativeMethods.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            previousObject = NativeMethods.SelectObject(memoryDc, dib);
            bitmap = new Bitmap(newWidth, newHeight, newWidth * 4, PixelFormat.Format32bppPArgb, bits);
            graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            capacityWidth = newWidth;
            capacityHeight = newHeight;
        }

        private void Release()
        {
            if (graphics != null) { graphics.Dispose(); graphics = null; }
            if (bitmap != null) { bitmap.Dispose(); bitmap = null; }
            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previousObject);
                NativeMethods.DeleteDC(memoryDc);
                memoryDc = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
                dib = IntPtr.Zero;
            }
            capacityWidth = 0;
            capacityHeight = 0;
        }

        public void Dispose()
        {
            Release();
        }
    }
}
