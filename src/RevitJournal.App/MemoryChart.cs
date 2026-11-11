using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RevitJournal.Core.Live;

namespace RevitJournal.App;

/// <summary>What <see cref="MemoryChart"/> draws: free memory over time against the machine's RAM and the line below which memory is low.</summary>
public sealed record MemoryChartData(IReadOnlyList<RamPoint> History, long TotalMb, long LowMb);

/// <summary>
/// Free memory over the last half hour as a filled curve: the scale on the right, the "low" zone shaded at the bottom,
/// a dot on the latest reading. Drawn at its real size, so the dot stays round and the text sharp at any width.
/// </summary>
public sealed class MemoryChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(MemoryChartData), typeof(MemoryChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MemoryChart), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A small curve for a tile: no scale, no labels, only the curve, the low line and the latest reading.</summary>
    public static readonly DependencyProperty SparklineProperty = DependencyProperty.Register(
        nameof(Sparkline), typeof(bool), typeof(MemoryChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Sparkline { get => (bool)GetValue(SparklineProperty); set => SetValue(SparklineProperty, value); }
    public MemoryChartData? Data { get => (MemoryChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private const double Gutter = 46, Footer = 16, Top = 6;
    private static readonly Brush Grid = Frozen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEC)));
    private static readonly Brush Axis = Frozen(new SolidColorBrush(Color.FromRgb(0xC5, 0xCA, 0xD3)));
    private static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9E)));
    private static readonly Brush Low = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B)));
    private static readonly Brush LowFill = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xD9, 0x3B, 0x3B)));

    protected override void OnRender(DrawingContext dc)
    {
        bool full = !Sparkline;
        // The sparkline keeps room only for the dot (top and right) and reaches the bottom of its box, so the tile's
        // padding is the same on every side.
        double width = ActualWidth - (full ? Gutter : 5), height = ActualHeight - (full ? Footer + Top : 5);
        if (Data is not { TotalMb: > 0 } data || width <= 10 || height <= 10)
            return;

        double top = full ? Top : 5;
        double Y(double mb) => top + height * (1 - Math.Clamp(mb / data.TotalMb, 0, 1));
        double bottom = top + height;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        void Text(string text, double x, double y, Brush brush, bool right = false)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(right ? x - ft.Width : x, y));
        }

        // Scale: the machine's RAM at the top, half of it, and the zone where memory counts as low.
        if (full)
        {
            dc.DrawLine(new Pen(Grid, 1), new Point(0, Y(data.TotalMb)), new Point(width, Y(data.TotalMb)));
            dc.DrawLine(new Pen(Grid, 1), new Point(0, Y(data.TotalMb / 2.0)), new Point(width, Y(data.TotalMb / 2.0)));
        }
        double lowY = Y(data.LowMb);
        dc.DrawRectangle(LowFill, null, new Rect(0, lowY, width, bottom - lowY));
        dc.DrawLine(new Pen(Low, 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(0, lowY), new Point(width, lowY));
        if (full)
        {
            Text($"{data.TotalMb / 1024.0:0} GB", width + 6, Y(data.TotalMb) - 5, Label);
            Text($"{data.TotalMb / 2048.0:0}", width + 6, Y(data.TotalMb / 2.0) - 7, Label);
            Text("low", width + 6, lowY - 8, Low);
        }

        var history = data.History;
        if (history.Count >= 2)
        {
            double span = Math.Max(1, (history[^1].Time - history[0].Time).TotalSeconds);
            var points = history.Select(p => new Point(width * (p.Time - history[0].Time).TotalSeconds / span, Y(p.FreeMb))).ToList();

            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, bottom), isFilled: true, isClosed: true);
                g.PolyLineTo(points, isStroked: false, isSmoothJoin: true);
                g.LineTo(new Point(points[^1].X, bottom), false, false);
            }
            area.Freeze();
            var fill = Stroke.Clone();
            fill.Opacity = 0.12;
            dc.DrawGeometry(fill, null, area);

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(points[0], isFilled: false, isClosed: false);
                g.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
            dc.DrawEllipse(Stroke, new Pen(Brushes.White, full ? 2 : 1.5), points[^1], full ? 4.5 : 3.5, full ? 4.5 : 3.5);
            if (!full)
                return;

            int minutes = (int)Math.Round((history[^1].Time - history[0].Time).TotalMinutes);
            Text(minutes >= 1 ? $"{minutes} min ago" : "just started", 0, bottom + 3, Label);
            Text("now", width, bottom + 3, Label, right: true);
        }
        dc.DrawLine(new Pen(Axis, 1), new Point(0, bottom), new Point(width, bottom));
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
