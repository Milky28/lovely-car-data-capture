using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// Copies one rectangle of the screen into memory, over and over, without allocating each time.
    /// </summary>
    /// <remarks>
    /// This reads the composited desktop, so the game has to be in borderless or windowed mode;
    /// exclusive fullscreen hands its frames straight to the display and nothing else can see them.
    /// The rectangle is in real screen pixels, which is what the box window reports, so a display
    /// scaled to something other than 100% needs no correction here.
    /// </remarks>
    internal sealed class ScreenGrabber : IDisposable
    {
        private Bitmap _bitmap;
        private Graphics _graphics;
        private byte[] _buffer;
        private int _width, _height;

        /// <summary>Copies the region and returns it, or null when it can't be read (locked screen, bad region).</summary>
        public PixelFrame Grab(PixelRect region, out string problem)
        {
            problem = null;
            if (region.Width < 8 || region.Height < 4)
            {
                problem = "the capture box is too small";
                return null;
            }

            try
            {
                if (_bitmap == null || _width != region.Width || _height != region.Height)
                {
                    _graphics?.Dispose();
                    _bitmap?.Dispose();
                    _bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
                    _graphics = Graphics.FromImage(_bitmap);
                    _width = region.Width;
                    _height = region.Height;
                    _buffer = null;
                }

                _graphics.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);

                var data = _bitmap.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = data.Stride * _height;
                    if (_buffer == null || _buffer.Length != bytes) _buffer = new byte[bytes];
                    Marshal.Copy(data.Scan0, _buffer, 0, bytes);
                    return PixelFrame.Bgra32(_buffer, _width, _height, data.Stride);
                }
                finally { _bitmap.UnlockBits(data); }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        public void Dispose()
        {
            _graphics?.Dispose();
            _bitmap?.Dispose();
            _graphics = null;
            _bitmap = null;
            _buffer = null;
        }
    }
}
