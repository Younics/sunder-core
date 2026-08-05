using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Sunder.App.Views;

internal static class SunderWindowSizing
{
    internal const double LoadingDesignWidth = 820;
    internal const double LoadingDesignHeight = 460;

    private static readonly SunderWindowSizeProfile LoadingProfile = new(
        LoadingDesignWidth,
        LoadingDesignHeight,
        MinimumWidth: 1,
        MinimumHeight: 1,
        MaximumWorkingAreaWidthRatio: 0.56,
        MaximumWorkingAreaHeightRatio: 0.52,
        PreserveAspectRatio: true);

    private static readonly SunderWindowSizeProfile MainProfile = new(
        DesignWidth: 1600,
        DesignHeight: 920,
        MinimumWidth: 1040,
        MinimumHeight: 680,
        MaximumWorkingAreaWidthRatio: 0.94,
        MaximumWorkingAreaHeightRatio: 0.90,
        PreserveAspectRatio: false);

    public static void ApplyLoadingWindowSize(Window window)
        => ApplyDefaultSize(window, LoadingProfile);

    public static void ApplyMainWindowSize(Window window)
        => ApplyDefaultSize(window, MainProfile);

    public static void ApplySecondaryWindowSize(Window window)
    {
        var profile = new SunderWindowSizeProfile(
            window.Width,
            window.Height,
            window.MinWidth,
            window.MinHeight,
            MaximumWorkingAreaWidthRatio: 0.88,
            MaximumWorkingAreaHeightRatio: 0.86,
            PreserveAspectRatio: false);
        ApplyDefaultSize(window, profile);
    }

    internal static SunderWindowSizeResult CalculateDefaultSize(
        double workingAreaPixelWidth,
        double workingAreaPixelHeight,
        double scaling,
        SunderWindowSizeProfile profile)
    {
        var workingArea = ToDips(workingAreaPixelWidth, workingAreaPixelHeight, scaling);
        if (!IsValidPositive(workingArea.Width) || !IsValidPositive(workingArea.Height))
        {
            return new SunderWindowSizeResult(
                new Size(profile.DesignWidth, profile.DesignHeight),
                new Size(profile.MinimumWidth, profile.MinimumHeight));
        }

        var maxWidth = workingArea.Width * profile.MaximumWorkingAreaWidthRatio;
        var maxHeight = workingArea.Height * profile.MaximumWorkingAreaHeightRatio;
        var size = profile.PreserveAspectRatio
            ? CalculateAspectLockedSize(profile.DesignWidth, profile.DesignHeight, maxWidth, maxHeight)
            : new Size(
                Math.Floor(Math.Min(profile.DesignWidth, maxWidth)),
                Math.Floor(Math.Min(profile.DesignHeight, maxHeight)));

        var minimumSize = new Size(
            Math.Floor(Math.Min(profile.MinimumWidth, size.Width)),
            Math.Floor(Math.Min(profile.MinimumHeight, size.Height)));
        return new SunderWindowSizeResult(size, minimumSize);
    }

    internal static Size ClampSizeToWorkingArea(
        double width,
        double height,
        double minimumWidth,
        double minimumHeight,
        double workingAreaPixelWidth,
        double workingAreaPixelHeight,
        double scaling)
    {
        var workingArea = ToDips(workingAreaPixelWidth, workingAreaPixelHeight, scaling);
        if (!IsValidPositive(workingArea.Width) || !IsValidPositive(workingArea.Height))
        {
            return new Size(Math.Max(minimumWidth, width), Math.Max(minimumHeight, height));
        }

        var effectiveMinimumWidth = Math.Min(minimumWidth, workingArea.Width);
        var effectiveMinimumHeight = Math.Min(minimumHeight, workingArea.Height);
        return new Size(
            Math.Clamp(width, effectiveMinimumWidth, workingArea.Width),
            Math.Clamp(height, effectiveMinimumHeight, workingArea.Height));
    }

    private static void ApplyDefaultSize(Window window, SunderWindowSizeProfile profile)
    {
        var screen = ResolveScreen(window);
        if (screen is null)
        {
            return;
        }

        var result = CalculateDefaultSize(
            screen.WorkingArea.Width,
            screen.WorkingArea.Height,
            screen.Scaling,
            profile);
        window.MinWidth = result.MinimumSize.Width;
        window.MinHeight = result.MinimumSize.Height;
        window.Width = result.Size.Width;
        window.Height = result.Size.Height;
    }

    private static Size CalculateAspectLockedSize(double designWidth, double designHeight, double maxWidth, double maxHeight)
    {
        var widthScale = maxWidth / designWidth;
        var heightScale = maxHeight / designHeight;
        var scale = Math.Min(1, Math.Min(widthScale, heightScale));
        return new Size(Math.Floor(designWidth * scale), Math.Floor(designHeight * scale));
    }

    private static Size ToDips(double workingAreaPixelWidth, double workingAreaPixelHeight, double scaling)
    {
        var effectiveScaling = IsValidPositive(scaling) ? scaling : 1;
        return new Size(workingAreaPixelWidth / effectiveScaling, workingAreaPixelHeight / effectiveScaling);
    }

    private static Screen? ResolveScreen(Window window)
        => window.Screens.ScreenFromWindow(window) ?? window.Screens.All.FirstOrDefault();

    private static bool IsValidPositive(double value)
        => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}

internal sealed record SunderWindowSizeProfile(
    double DesignWidth,
    double DesignHeight,
    double MinimumWidth,
    double MinimumHeight,
    double MaximumWorkingAreaWidthRatio,
    double MaximumWorkingAreaHeightRatio,
    bool PreserveAspectRatio);

internal sealed record SunderWindowSizeResult(Size Size, Size MinimumSize);
