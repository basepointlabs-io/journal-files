using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RevitJournal.Core.Live;

namespace RevitJournal.App;

/// <summary>What <see cref="Gauge"/> draws: this computer's figure and Autodesk's figures on one dial.</summary>
/// <param name="UsedMb">The part of <paramref name="HaveMb"/> in use now (RAM only), drawn as a thin inner arc.</param>
public sealed record GaugeData(long HaveMb, IReadOnlyList<RequirementMark> Marks, long? UsedMb = null);

/// <summary>
/// A half-circle dial for one resource. The coloured zones and the numbers on the dial are Autodesk's figures, every
/// figure drawn the same way. One figure (a minimum): red below it, green above. Several (RAM 16 / 32 / 64): red below
/// the first, then one step per figure from amber to green, full green above the last. An upper bound (the page file):
/// green up to it, red above. The needle is this computer.
/// </summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(GaugeData), typeof(Gauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GaugeData? Data { get => (GaugeData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private const double Thickness = 12;
    private static readonly Brush Red = Frozen(Color.FromRgb(0xF0, 0x95, 0x95));
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xFA, 0xC7, 0x75));
    private static readonly Brush LightGreen = Frozen(Color.FromRgb(0xC0, 0xDD, 0x97));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x97, 0xC4, 0x59));
    /// <summary>The steps between several figures, from the first to the last: amber, light green, and so on.</summary>
    private static readonly Brush[] Steps = [Amber, LightGreen];
    private static readonly Brush Used = Frozen(Color.FromRgb(0x37, 0x8A, 0xDD));
    private static readonly Brush Track = Frozen(Color.FromRgb(0xE6, 0xE9, 0xEE));
    private static readonly Brush Needle = Frozen(Color.FromRgb(0x2A, 0x2E, 0x35));
    private static readonly Brush TickInk = Frozen(Color.FromRgb(0x5C, 0x63, 0x70));

    // No wider than the card: asking for more makes WPF clip the dial, numbers at its ends included.
    protected override Size MeasureOverride(Size available) => new(Math.Min(180, available.Width), 104);

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is not { } data || ActualWidth < 60 || ActualHeight < 40)
            return;

        // The numbers of Autodesk's figures go just outside the dial; the dial is as large as leaves room for the widest
        // of them at the sides ("max 95.8") and for one line of them above.
        var marks = data.Marks.OrderBy(m => m.Mb).ToList();
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labels = marks.Select(m => new FormattedText((m.Maximum ? "max " : "") + Number(m.Mb), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, TickInk, dip)).ToList();
        // The same room on every dial, for the widest number any of them shows, so all dials are the same size.
        var widest = new FormattedText("max 888.8", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, TickInk, dip);
        double side = widest.Width, top = widest.Height;
        double r = Math.Min(ActualWidth / 2 - Thickness / 2 - side - 6, ActualHeight - Thickness / 2 - top - 6);
        if (r < 20)
            return;
        var centre = new Point(ActualWidth / 2, ActualHeight - 6);
        double scale = Math.Max(data.HaveMb, marks.Count > 0 ? marks[^1].Mb : 0) * 1.15;
        if (scale <= 0)
            return;
        double Angle(double mb) => Math.PI * (1 - Math.Clamp(mb / scale, 0, 1));
        Point At(double mb, double radius) => new(centre.X + radius * Math.Cos(Angle(mb)), centre.Y - radius * Math.Sin(Angle(mb)));

        void Arc(double from, double to, Brush brush, double radius, double thickness)
        {
            if (to <= from)
                return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(At(from, radius), false, false);
                g.ArcTo(At(to, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, geometry);
        }

        // Zones from Autodesk's figures.
        if (marks.FirstOrDefault(m => m.Maximum) is { } maximum)
        {
            Arc(0, maximum.Mb, Green, r, Thickness);
            Arc(maximum.Mb, scale, Red, r, Thickness);
        }
        else if (marks.Count > 0)
        {
            Arc(0, marks[0].Mb, Red, r, Thickness);
            for (int i = 0; i + 1 < marks.Count; i++)
                Arc(marks[i].Mb, marks[i + 1].Mb, Steps[Math.Min(i, Steps.Length - 1)], r, Thickness);
            Arc(marks[^1].Mb, scale, Green, r, Thickness);
        }
        else
            Arc(0, scale, Track, r, Thickness);

        // What is in use now (RAM): a thin arc inside the dial.
        if (data.UsedMb is { } used)
        {
            Arc(0, data.HaveMb, Track, r - Thickness - 2, 4);
            Arc(0, Math.Min(used, data.HaveMb), Used, r - Thickness - 2, 4);
        }

        // Autodesk's figures: a cut through the band, exactly as wide as the band and in the card's colour, and the
        // number outside it.
        var cut = new Pen(TryFindResource("Panel") as Brush ?? Brushes.White, 3);
        for (int i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            dc.DrawLine(cut, At(m.Mb, r - Thickness / 2), At(m.Mb, r + Thickness / 2));
            // An upper bound says so ("max 16"); a minimum is just its number.
            var text = labels[i];
            // The number sits just outside the band: its box is moved out along the dial's radius by half its width
            // and half its height, so a wide number near the ends ("max 95.8") stays clear of the band.
            double a = Angle(m.Mb);
            var p = At(m.Mb, r + Thickness / 2 + 4);
            double x = p.X - text.Width / 2 + Math.Cos(a) * text.Width / 2;
            double y = p.Y - text.Height / 2 - Math.Sin(a) * text.Height / 2;
            x = Math.Clamp(x, 0, Math.Max(0, ActualWidth - text.Width));
            dc.DrawText(text, new Point(x, Math.Max(0, y)));
        }

        // This computer: the needle.
        var tip = At(data.HaveMb, r - 2);
        dc.DrawLine(new Pen(Needle, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, centre, tip);
        dc.DrawEllipse(Needle, null, centre, 5, 5);
    }

    /// <summary>Autodesk's figure in GB, as short as it can be: "16", "95.8".</summary>
    private static string Number(long mb) => (mb / 1024.0).ToString(mb % 1024 == 0 || mb >= 100 * 1024 ? "0" : "0.#", CultureInfo.InvariantCulture);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
