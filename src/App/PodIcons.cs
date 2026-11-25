using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BTBatteryIndicator.Core;

namespace BTBatteryIndicator;

/// <summary>
/// Original earbud artwork drawn from simple shapes (a rounded head on a short stem, and a charging case).
/// Nothing here is copied from Apple's icons; these are generic stem-style earbud silhouettes.
/// </summary>
public static class PodIcons
{
    public static readonly Geometry Left = Freeze(Bud(headRight: false, 0));
    public static readonly Geometry Right = Freeze(Bud(headRight: true, 0));
    public static readonly Geometry Pair = Freeze(MakePair());
    public static readonly Geometry Case = Freeze(MakeCase());

    public static Geometry For(PodView view) => view switch
    {
        PodView.Left => Left,
        PodView.Right => Right,
        _ => Pair,
    };

    /// <summary>A ready-to-place icon, 15 units tall, in the given colour.</summary>
    public static Path Element(Geometry g, Brush fill, double height = 17) => new()
    {
        Data = g, Fill = fill, Height = height, Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = false,
    };

    // One bud in a 20 x 20 box: the head bulges to one side at the top, the stem hangs straight down.
    static Geometry Bud(bool headRight, double shiftX)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(10.6, 6.4), 5.4, 4.9) { Transform = new RotateTransform(20, 10.6, 6.4) }); // head
        g.Children.Add(new EllipseGeometry(new Point(15.2, 9.6), 2.7, 2.4) { Transform = new RotateTransform(35, 15.2, 9.6) });  // ear tip
        g.Children.Add(new RectangleGeometry(new Rect(5.4, 4.5, 3.9, 15), 1.95, 1.95));                                          // stem
        var t = new TransformGroup();
        if (!headRight) t.Children.Add(new ScaleTransform(-1, 1, 10, 0));
        t.Children.Add(new TranslateTransform(shiftX, 0));
        g.Transform = t;
        return g;
    }

    // Two buds, stems towards the middle and heads pointing outwards.
    static Geometry MakePair()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(Bud(headRight: false, 0));
        g.Children.Add(Bud(headRight: true, 11.5));
        return g;
    }

    // Charging case: a rounded box with a lid seam and a status light (cut out as holes).
    static Geometry MakeCase()
    {
        Geometry box = new RectangleGeometry(new Rect(1, 3, 20, 15), 5, 5);
        Geometry seam = new RectangleGeometry(new Rect(1, 7.6, 20, 1.3));
        Geometry light = new EllipseGeometry(new Point(11, 12.6), 1.3, 1.3);
        return new CombinedGeometry(GeometryCombineMode.Exclude,
            new CombinedGeometry(GeometryCombineMode.Exclude, box, seam), light);
    }

    static Geometry Freeze(Geometry g)
    {
        var flat = g.GetFlattenedPathGeometry(0.02, ToleranceType.Absolute); // bake transforms into plain outlines
        flat.Freeze();
        return flat;
    }
}
