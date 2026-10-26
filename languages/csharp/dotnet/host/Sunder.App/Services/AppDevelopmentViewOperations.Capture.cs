using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.App.Services;

internal sealed partial class AppDevelopmentViewOperations
{
    internal Task<SunderContent> CaptureAsync(AppDevelopmentViewBinding binding, PackageDevelopmentViewQuery scope, int edge, CancellationToken token)
    {
        scope.Validate();
        if (edge is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(edge));
        token.ThrowIfCancellationRequested();
        return binding.Dispatcher.InvokeAsync(async () =>
        {
            RequireView(binding, token);
            var selected = scope.ElementReference is not null ? SelectedElement(binding, scope.SnapshotId!, scope.ElementReference) : null;
            var root = binding.Root;
            Rect Position(Control control)
            {
                if (control.TransformToVisual(root) is not { } transform || Math.Abs(transform.M11 - 1) > 0.000001 || Math.Abs(transform.M22 - 1) > 0.000001
                    || Math.Abs(transform.M12) > 0.000001 || Math.Abs(transform.M21) > 0.000001) throw new NotSupportedException("Capture requires untransformed package surfaces.");
                var origin = control.TranslatePoint(default, root) ?? throw Invalid("Capture geometry changed.");
                return new(origin, control.Bounds.Size);
            }
            void RequireNativeComposition(Control owner)
            {
                // Native snapshots are painted over the managed bitmap. Custom
                // visual effects/masks cannot be reproduced by that composition,
                // even when the affected visual does not enable ClipToBounds.
                foreach (var visual in owner.GetSelfAndVisualDescendants().Concat(owner.GetVisualAncestors()))
                    if (visual.Clip is not null || visual.Opacity != 1 || visual.OpacityMask is not null || visual.Effect is not null)
                        throw new NotSupportedException("Native capture cannot prove custom clip/effect composition.");
            }
            var crop = (selected is null ? new Rect(root.Bounds.Size) : Position(selected.Control)).Intersect(new(root.Bounds.Size));
            var natives = new List<(Control Control, byte[] Bytes, Rect Bounds, Rect OwnerBounds)>();
            foreach (var control in root.GetSelfAndVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control is IAppDevelopmentDocumentOwner))
            {
                if (!Position(control).Intersects(crop)) continue;
                RequireNativeComposition(control);
                var before = Position(control);
                var document = ((IAppDevelopmentDocumentOwner)control).DevelopmentDocument;
                var image = await document.CaptureAsync(selected?.Document is not null ? scope.SnapshotId : null,
                    selected?.Document?.ElementReference, edge, token);
                RequireView(binding, token);
                RequireNativeComposition(control);
                var position = Position(control);
                if (position != before) throw Invalid("The native package geometry changed during capture.");
                var bounds = image.Bounds.Translate(new Vector(position.X, position.Y));
                if (selected?.Document is not null) crop = crop.Intersect(bounds);
                ValidatePng(image.Png);
                natives.Add((control, image.Png, bounds, before));
            }
            foreach (var native in natives)
                foreach (var other in root.GetSelfAndVisualDescendants().OfType<Control>())
                    if (other.IsEffectivelyVisible && other != native.Control && other != root
                        && !other.GetVisualAncestors().Contains(native.Control) && !native.Control.GetVisualAncestors().Contains(other)
                        && other.Bounds.Width > 0 && other.Bounds.Height > 0 && Position(other).Intersects(native.Bounds.Intersect(crop)))
                        throw new NotSupportedException("Native capture cannot prove the ordering of overlapping package surfaces.");
            if (root.GetSelfAndVisualDescendants().OfType<Control>().Any(visual => visual.IsEffectivelyVisible && visual is NativeControlHost
                && Position(visual).Intersects(crop) && !visual.GetVisualAncestors().Any(ancestor => ancestor is IAppDevelopmentDocumentOwner)))
                throw new NotSupportedException("An unsupported native surface prevents complete package capture.");
            if (crop.Width <= 0 || crop.Height <= 0) throw Invalid("The capture region is not visible.");
            var scale = Math.Min(1, edge / Math.Max(crop.Width, crop.Height));
            var rootScale = Math.Min(1, 2048 / Math.Max(root.Bounds.Width, root.Bounds.Height));
            using var software = new RenderTargetBitmap(Pixels(root.Bounds.Size, rootScale), new(96 * rootScale, 96 * rootScale));
            software.Render(root);
            using var output = new RenderTargetBitmap(Pixels(crop.Size, scale), new(96 * scale, 96 * scale));
            using (var drawing = output.CreateDrawingContext())
            using (drawing.PushTransform(Matrix.CreateTranslation(-crop.X, -crop.Y)))
            {
                drawing.DrawImage(software, new Rect(software.Size), new Rect(root.Bounds.Size));
                foreach (var native in natives)
                {
                    RequireNativeComposition(native.Control);
                    if (Position(native.Control) != native.OwnerBounds) throw Invalid("The native package geometry changed during capture.");
                    using var source = new MemoryStream(native.Bytes, false);
                    using var bitmap = new Bitmap(source);
                    var masks = new Stack<IDisposable>();
                    try
                    {
                        foreach (var ancestor in native.Control.GetVisualAncestors().OfType<Control>().Prepend(native.Control).Where(control => control.ClipToBounds))
                        {
                            var position = Position(ancestor);
                            masks.Push(drawing.PushClip(ancestor is Border border ? new RoundedRect(position, border.CornerRadius) : new RoundedRect(position)));
                        }
                        drawing.DrawImage(bitmap, new Rect(bitmap.Size), native.Bounds);
                    }
                    finally { while (masks.TryPop(out var mask)) mask.Dispose(); }
                }
            }
            using var bytes = new MemoryStream();
            output.Save(bytes, PngBitmapEncoderOptions.Default);
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Package image exceeds its 2 MiB budget. Capture one element instead.");
            RequireView(binding, token);
            if (selected is not null) _ = SelectedElement(binding, scope.SnapshotId!, scope.ElementReference!);
            token.ThrowIfCancellationRequested();
            return SunderContent.FromBytes(bytes.ToArray(), "image/png", "package-view.png");
        });
    }

    private static PixelSize Pixels(Size size, double scale) => new(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
    internal static (int Width, int Height) ValidatePng(byte[] bytes)
    {
        if (bytes.Length is < 33 or > 2 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Invalid bounded PNG capture.");
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8 * 1024 * 1024) throw new InvalidDataException("Capture exceeds its pixel budget.");
        return (width, height);
    }
}
