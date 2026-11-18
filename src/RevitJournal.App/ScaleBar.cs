using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RevitJournal.Core.Live;

namespace RevitJournal.App;

/// <summary>What <see cref="ScaleBar"/> draws: this computer's figure and Autodesk's figures on one scale.</summary>
/// <param name="UsedMb">The part of <paramref name="HaveMb"/> in use now, drawn darker (RAM only).</param>
public sealed record ScaleData(long HaveMb, IReadOnlyList<RequirementMark> Marks, long? UsedMb = null);

/// <summary>
/// A bar for one resource: the filled part is what this computer has, the ticks with labels under them are Autodesk's
/// figures, the one that applies now drawn darker. The scale runs a little past the larger of the two, so both fit.
/// </summary>
public sealed class ScaleBar : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ScaleData), typeof(ScaleBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ScaleBar), new FrameworkPropertyMetadata(Palette.Green, FrameworkPropertyMetadataOptions.AffectsRender));

    public ScaleData? Data { get => (ScaleData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private const double Track = 8, TickOut = 4, LabelGap = 4;
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEE)));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0x9E)));
    private static readonly Brush TargetBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x35)));

    protected override Size MeasureOverride(Size available) => new(0, TickOut * 2 + Track + LabelGap + 15);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        if (Data is not { } data || width <= 20)
            return;

        long max = Math.Max(data.HaveMb, data.Marks.Count > 0 ? data.Marks.Max(m => m.Mb) : 0);
        if (max <= 0)
            return;
        double scale = max * 1.12;
        double X(long mb) => Math.Clamp(width * mb / scale, 0, width);

        double top = TickOut;
        var track = new Rect(0, top, width, Track);
        dc.DrawRoundedRectangle(TrackBrush, null, track, Track / 2, Track / 2);
        double fill = Math.Max(Track, X(data.HaveMb));
        if (data.UsedMb is { } used)
        {
            // What is installed in a light shade, what is in use in the full colour.
            dc.PushOpacity(0.35);
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, top, fill, Track), Track / 2, Track / 2);
            dc.Pop();
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, top, Math.Max(Track, X(Math.Min(used, data.HaveMb))), Track), Track / 2, Track / 2);
        }
        else
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, top, fill, Track), Track / 2, Track / 2);

        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double lastRight = double.NegativeInfinity;
        foreach (var m in data.Marks.OrderBy(m => m.Mb))
        {
            double x = Math.Round(X(m.Mb)) + 0.5;
            var brush = m.Target ? TargetBrush : TickBrush;
            dc.DrawLine(new Pen(brush, m.Target ? 2 : 1), new Point(x, 0), new Point(x, top + Track + TickOut));

            // The label is centred under its tick, kept inside the bar and clear of the previous one.
            var ft = new FormattedText(m.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, m.Target ? bold : typeface, 11, brush, dip);
            double left = Math.Clamp(x - ft.Width / 2, 0, Math.Max(0, width - ft.Width));
            if (left < lastRight + 6)
                continue;
            dc.DrawText(ft, new Point(left, top + Track + TickOut + LabelGap - 2));
            lastRight = left + ft.Width;
        }
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
