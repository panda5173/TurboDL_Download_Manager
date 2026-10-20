using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TurboDL.UI;

/// <summary>
/// IDM-style progress bar: draws each downloaded range of every segment, with a thin marker where
/// a connection started. Falls back to a plain bar when the size is unknown or the download is finished.
/// </summary>
public sealed class SegmentBar : Control
{
    static readonly IBrush DefaultTrack = new ImmutableSolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x42));
    static readonly IBrush DefaultBar = new ImmutableSolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF));
    static readonly IBrush MarkerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0xB6));

    /// <summary>long[] triplets (start, position, end). Typed as object to match the Windows build.</summary>
    public static readonly StyledProperty<object?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentBar, object?>(nameof(Segments));

    public static readonly StyledProperty<long> TotalProperty =
        AvaloniaProperty.Register<SegmentBar, long>(nameof(Total), -1L);

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(TrackBrush));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(SegmentsProperty, TotalProperty, ProgressProperty, BarBrushProperty, TrackBrushProperty);
    }

    public object? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public long Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        double r = h / 2;
        var bounds = new Rect(0, 0, w, h);
        var rounded = new RoundedRect(bounds, r);
        dc.DrawRectangle(TrackBrush ?? DefaultTrack, null, rounded);

        var brush = BarBrush ?? DefaultBar;
        var segs = Segments as long[];
        long total = Total;

        using (dc.PushClip(rounded))
        {
            if (segs != null && segs.Length >= 3 && total > 0)
            {
                double scale = w / total;
                for (int i = 0; i + 2 < segs.Length; i += 3)
                {
                    double x1 = segs[i] * scale;
                    double x2 = segs[i + 1] * scale;
                    if (x2 - x1 > 0.25) dc.FillRectangle(brush, new Rect(x1, 0, x2 - x1, h));
                    if (i > 0) dc.FillRectangle(MarkerBrush, new Rect(Math.Max(0, x1 - 0.5), 0, 1.5, h));
                }
            }
            else
            {
                double p = Math.Clamp(Progress, 0, 100) / 100.0 * w;
                if (p > 0) dc.FillRectangle(brush, new Rect(0, 0, p, h));
            }
        }
    }
}
