using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RevitJournal.Core.Live;

namespace RevitJournal.App;

/// <summary>What <see cref="Gauge"/> draws: this computer's figure and Autodesk's figures on one scale.</summary>
/// <param name="UsedMb">The part of <paramref name="HaveMb"/> in use now (RAM only), drawn as a thin line under the scale.</param>
public sealed record GaugeData(long HaveMb, IReadOnlyList<RequirementMark> Marks, long? UsedMb = null);

/// <summary>
/// A straight scale for one resource, as on a drawing. The coloured zones and the numbers under the scale are
/// Autodesk's figures, every figure drawn the same way. One figure (a minimum): red below it, green above. Several
/// (RAM 16 / 32 / 64): red below the first, then one step per figure from amber to green, full green above the last.
/// An upper bound (the page file): green up to it, red above. The pointer above the scale is this computer.
/// </summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(GaugeData), typeof(Gauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GaugeData? Data { get => (GaugeData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private const double BarTop = 12, Thickness = 6;
    private static readonly Brush Red = Frozen(Color.FromRgb(0xDC, 0x53, 0x47));
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE6, 0xA5, 0x2E));
    private static readonly Brush LightGreen = Frozen(Color.FromRgb(0xA9, 0xC6, 0x59));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x4A, 0x9F, 0x66));
    /// <summary>The steps between several figures, from the first to the last: amber, light green, and so on.</summary>
    private static readonly Brush[] Steps = [Amber, LightGreen];
    private static readonly Brush Used = Frozen(Color.FromRgb(0x1F, 0x55, 0xC9));
    private static readonly Brush Track = Frozen(Color.FromRgb(0xCD, 0xD9, 0xE9));
    private static readonly Brush Pointer = Frozen(Color.FromRgb(0x16, 0x26, 0x3F));
    private static readonly Brush TickInk = Frozen(Color.FromRgb(0x5D, 0x71, 0x90));

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 200 : available.Width, double.IsInfinity(available.Height) ? 48 : Math.Min(available.Height, 48));

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is not { } data || ActualWidth < 60 || ActualHeight < 40)
            return;

        var marks = data.Marks.OrderBy(m => m.Mb).ToList();
        double scale = Math.Max(data.HaveMb, marks.Count > 0 ? marks[^1].Mb : 0) * 1.15;
        if (scale <= 0)
            return;
        // Half a pointer of room at each end, so the pointer and the end numbers stay inside.
        const double inset = 5;
        double width = ActualWidth - 2 * inset;
        double X(double mb) => Math.Round(inset + width * Math.Clamp(mb / scale, 0, 1));
        void Bar(double from, double to, Brush brush, double top, double height)
        {
            if (X(to) > X(from))
                dc.DrawRectangle(brush, null, new Rect(X(from), top, X(to) - X(from), height));
        }

        // Zones from Autodesk's figures; a gap of one pixel at each figure.
        if (marks.FirstOrDefault(m => m.Maximum) is { } maximum)
        {
            Bar(0, maximum.Mb, Green, BarTop, Thickness);
            Bar(maximum.Mb, scale, Red, BarTop, Thickness);
        }
        else if (marks.Count > 0)
        {
            Bar(0, marks[0].Mb, Red, BarTop, Thickness);
            for (int i = 0; i + 1 < marks.Count; i++)
                Bar(marks[i].Mb, marks[i + 1].Mb, Steps[Math.Min(i, Steps.Length - 1)], BarTop, Thickness);
            Bar(marks[^1].Mb, scale, Green, BarTop, Thickness);
        }
        else
            Bar(0, scale, Track, BarTop, Thickness);

        // What is in use now (RAM): a thin line under the scale, on a track as long as the installed memory.
        double below = BarTop + Thickness + 2;
        if (data.UsedMb is { } used)
        {
            Bar(0, data.HaveMb, Track, below, 2);
            Bar(0, Math.Min(used, data.HaveMb), Used, below, 2);
            below += 3;
        }

        // The rule: a short tick every tenth, a longer one with its number at each of Autodesk's figures.
        var tick = new Pen(Track, 1);
        for (int i = 0; i <= 10; i++)
            dc.DrawRectangle(Track, null, new Rect(Math.Min(X(scale * i / 10), ActualWidth - inset - 1), below + 1, 1, 3));
        var cut = TryFindResource("Panel") as Brush ?? Brushes.White;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double taken = double.NegativeInfinity;
        foreach (var m in marks)
        {
            double x = X(m.Mb);
            dc.DrawRectangle(cut, null, new Rect(x - 1, BarTop, 2, Thickness));
            dc.DrawRectangle(TickInk, null, new Rect(x, below + 1, 1, 6));
            var text = new FormattedText(Number(m.Mb), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, TickInk, dip);
            double left = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            if (left < taken + 4)
                continue; // two figures too close to print both
            dc.DrawText(text, new Point(left, below + 8));
            taken = left + text.Width;
        }

        // This computer: the pointer above the scale.
        double at = X(data.HaveMb);
        var pointer = new StreamGeometry();
        using (var g = pointer.Open())
        {
            g.BeginFigure(new Point(at - 5, 1), true, true);
            g.LineTo(new Point(at + 5, 1), false, false);
            g.LineTo(new Point(at, BarTop - 2), false, false);
        }
        pointer.Freeze();
        dc.DrawGeometry(Pointer, null, pointer);
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
