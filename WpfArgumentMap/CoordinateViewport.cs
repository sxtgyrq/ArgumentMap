using System.Windows;

namespace WpfArgumentMap;

/// <summary>World coordinates are independent of the size of the WPF drawing surface.</summary>
internal sealed class CoordinateViewport
{
    internal const double WorldSize = 500_000;
    internal const double MaximumScale = 32;
    internal Point Center { get; private set; } = new(WorldSize / 2, WorldSize / 2);
    internal double Scale { get; private set; } = 1;
    internal Size Size { get; private set; }
    internal double FitScale => Math.Max(0.000001, Math.Min(Size.Width, Size.Height) * 0.9 / WorldSize);

    internal void Resize(Size size)
    {
        Size = size;
        Scale = Math.Clamp(Scale, FitScale, MaximumScale);
    }

    internal Point WorldToScreen(Point world) => new(
        Size.Width / 2 + (world.X - Center.X) * Scale,
        Size.Height / 2 - (world.Y - Center.Y) * Scale);

    internal Point ScreenToWorld(Point screen) => new(
        Center.X + (screen.X - Size.Width / 2) / Scale,
        Center.Y - (screen.Y - Size.Height / 2) / Scale);

    internal void Pan(Vector delta) => SetCenter(new Point(
        Center.X - delta.X / Scale, Center.Y + delta.Y / Scale));

    internal void Zoom(double factor, Point anchor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        var world = ScreenToWorld(anchor);
        Scale = Math.Clamp(Scale * factor, FitScale, MaximumScale);
        SetCenter(new Point(
            world.X - (anchor.X - Size.Width / 2) / Scale,
            world.Y + (anchor.Y - Size.Height / 2) / Scale));
    }

    internal void Reset(bool fit = false)
    {
        Center = new Point(WorldSize / 2, WorldSize / 2);
        Scale = fit ? FitScale : Math.Clamp(1, FitScale, MaximumScale);
    }

    internal void FitBounds(Rect bounds)
    {
        if(bounds.IsEmpty || bounds.Width<=0 || bounds.Height<=0)return;
        Scale=Math.Clamp(Math.Min(Size.Width/bounds.Width,Size.Height/bounds.Height)*0.9,FitScale,MaximumScale);
        SetCenter(new(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2));
    }

    private void SetCenter(Point center) => Center = new Point(
        Math.Clamp(center.X, 0, WorldSize), Math.Clamp(center.Y, 0, WorldSize));

    // Keep labels readable while changing intervals in a predictable 1, 2, 5 sequence.
    internal static double GetGridStep(double scale)
    {
        var desired = 100 / scale;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        var normalized = desired / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }
}
