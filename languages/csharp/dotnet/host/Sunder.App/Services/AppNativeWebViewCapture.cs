using Avalonia;
using Avalonia.Media.Imaging;

namespace Sunder.App.Services;

internal static class AppNativeWebViewCapture
{
    internal static void ValidateRegion(Rect bounds, Size viewport, int edge)
    {
        if (edge is < 1 or > 2048 || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height)
            || viewport.Width <= 0 || viewport.Height <= 0
            || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0 || !new Rect(viewport).Contains(bounds))
            throw new ArgumentException("Invalid native capture region.");
    }

    internal static void RequirePixels(int width, int height)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8 * 1024 * 1024)
            throw new InvalidDataException("Native viewport exceeds its pixel budget.");
    }

    internal static byte[] Crop(byte[] png, Rect bounds, Size viewport, int edge)
    {
        ValidateRegion(bounds, viewport, edge);
        AppDevelopmentViewOperations.ValidatePng(png);
        using var input = new MemoryStream(png, false);
        using var bitmap = new Bitmap(input);
        var scale = Math.Min(1, edge / Math.Max(bounds.Width, bounds.Height));
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)));
        using var output = new RenderTargetBitmap(pixels, new(96, 96));
        using (var drawing = output.CreateDrawingContext())
        {
            var source = new Rect(bounds.X * bitmap.Size.Width / viewport.Width, bounds.Y * bitmap.Size.Height / viewport.Height,
                bounds.Width * bitmap.Size.Width / viewport.Width, bounds.Height * bitmap.Size.Height / viewport.Height);
            drawing.DrawImage(bitmap, source, new Rect(pixels.ToSize(1)));
        }
        using var bytes = new PngStream();
        output.Save(bytes, PngBitmapEncoderOptions.Default);
        var result = bytes.ToArray();
        AppDevelopmentViewOperations.ValidatePng(result);
        return result;
    }

    internal class PngStream : MemoryStream
    {
        internal const int MaximumBytes = 2 * 1024 * 1024;
        internal bool LimitExceeded { get; private set; }
        internal PngStream() : base(MaximumBytes) { }
        private void RequireLength(long length)
        {
            if (length is < 0 or > MaximumBytes)
            {
                LimitExceeded = true;
                throw new InvalidDataException("Native image exceeds its 2 MiB capture budget.");
            }
        }
        public override int Capacity { get => base.Capacity; set { RequireLength(value); base.Capacity = value; } }
        public override long Position { get => base.Position; set { RequireLength(value); base.Position = value; } }
        public override void SetLength(long value) { RequireLength(value); base.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { RequireLength(checked(Position + count)); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { RequireLength(checked(Position + buffer.Length)); base.Write(buffer); }
        public override void WriteByte(byte value) { RequireLength(checked(Position + 1)); base.WriteByte(value); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var start = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            RequireLength(checked(start + offset));
            return base.Seek(offset, origin);
        }
    }
}
