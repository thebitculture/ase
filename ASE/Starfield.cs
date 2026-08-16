using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace ASE;

/// <summary>
/// The starfield every Amiga/ST demo used to open with, brought forward a few
/// decades: stars are still points rushed past a perspective camera, but they now
/// carry motion trails, a tinted palette, a slow roll and a drifting vanishing
/// point over a faint two-tone nebula.
///
/// Everything is painted in <see cref="Render"/> rather than by moving hundreds of
/// shapes around a Canvas: at this star count a visual per star would mean a layout
/// pass every frame, while the whole field is one draw list here. The control owns
/// its clock, so it starts and stops with the window that hosts it.
/// </summary>
public class Starfield : Control
{
    // Camera. Z is depth in arbitrary units: stars are born at ZFar and rush towards
    // ZNear. ZNear is deliberately small so every star has left the window before it
    // is recycled -- otherwise the ones passing near the axis would blink out on screen.
    private const double ZNear = 0.06;
    private const double ZFar = 1.0;

    // Spawn disc, same units. The hole in the middle keeps stars off the vanishing
    // point, where they would crawl for seconds instead of flying.
    private const double SpawnInnerRadius = 0.13;
    private const double SpawnOuterRadius = 0.55;

    private const double BaseSpeed = 0.17;   // Z units per second
    private const double RollSpeed = 0.075;  // radians per second

    // Trail length as travel time rather than as a number of frames, so the streaks
    // look the same whether the timer is keeping up or not. Long enough that near
    // stars read as streaks rather than as capsules.
    private const double TrailSeconds = 0.08;

    private const int StarCount = 320;

    // Mostly cold whites with a few brand-warm ones, which is what keeps the field
    // from reading as a screensaver and ties it to the rest of the window.
    private static readonly Color[] Tints =
    {
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0xDC, 0xE6, 0xFF),
        Color.FromRgb(0x9F, 0xC6, 0xFF),
        Color.FromRgb(0xFF, 0xB0, 0x67),
        Color.FromRgb(0xFF, 0x8A, 0x2B),
    };

    // Cumulative weights for the palette above.
    private static readonly double[] TintWeights = { 0.46, 0.70, 0.84, 0.94, 1.00 };

    private struct Star
    {
        public double X;
        public double Y;
        public double Z;
        public int Tint;
    }

    // Brushes and pens are quantized and cached: a star's colour changes continuously
    // with depth, and without this every frame would allocate one brush and one pen
    // per star. 32 alpha steps are past the point where banding is visible.
    private const int AlphaSteps = 32;
    private const int WidthSteps = 12;
    private const double MinStarWidth = 0.55;
    private const double MaxStarWidth = 2.4;

    private readonly ImmutableSolidColorBrush[,] _brushes =
        new ImmutableSolidColorBrush[Tints.Length, AlphaSteps];

    private readonly ImmutablePen[,,] _pens =
        new ImmutablePen[Tints.Length, AlphaSteps, WidthSteps];

    // Halos need a gradient, not a flat colour: a solid low-alpha circle reads as a
    // grey disc stuck to the star. Cached like the rest -- the brush is relative to
    // the ellipse it fills, so one per tint and alpha covers every size.
    private readonly IBrush[,] _halos = new IBrush[Tints.Length, AlphaSteps];

    private readonly Star[] _stars = new Star[StarCount];

    // Fixed seed: the field is decorative, and a reproducible one makes visual
    // tweaks comparable between runs. 1985 is the ST's year.
    private readonly Random _random = new(1985);

    private readonly Stopwatch _clock = new();
    private DispatcherTimer _timer;
    private double _time;
    private double _lastTick;

    public Starfield()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        for (int i = 0; i < _stars.Length; i++)
        {
            // Spread over the whole depth range on the first frame, so the window
            // opens onto a full field instead of one that fills up over five seconds.
            Respawn(ref _stars[i], ZNear + _random.NextDouble() * (ZFar - ZNear));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _clock.Restart();
        _time = 0;
        _lastTick = 0;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        _clock.Stop();

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTick(object sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;

        // Clamped: a stall (window drag, GC pause) must not teleport the field.
        var dt = Math.Clamp(now - _lastTick, 0.0, 0.05);
        _lastTick = now;
        _time = now;

        Advance(dt);
        InvalidateVisual();
    }

    private void Advance(double dt)
    {
        var step = SpeedAt(_time) * dt;

        for (int i = 0; i < _stars.Length; i++)
        {
            ref var star = ref _stars[i];

            star.Z -= step;

            if (star.Z <= ZNear)
                Respawn(ref star, ZFar);
        }
    }

    private void Respawn(ref Star star, double z)
    {
        // Uniform over the disc: sqrt on the radius, or the stars would bunch up in
        // the middle where they are least interesting.
        var angle = _random.NextDouble() * Math.PI * 2.0;
        var radius = SpawnInnerRadius +
                     (SpawnOuterRadius - SpawnInnerRadius) * Math.Sqrt(_random.NextDouble());

        star.X = Math.Cos(angle) * radius;
        star.Y = Math.Sin(angle) * radius;
        star.Z = z;
        star.Tint = PickTint();
    }

    private int PickTint()
    {
        var roll = _random.NextDouble();

        for (int i = 0; i < TintWeights.Length; i++)
        {
            if (roll < TintWeights[i])
                return i;
        }

        return 0;
    }

    // Breathing speed. The field never quite settles, which is what makes it feel
    // hand-tuned rather than looped.
    private static double SpeedAt(double t)
        => BaseSpeed * (0.78 + 0.22 * Math.Sin(t * 0.31));

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;

        if (w <= 1 || h <= 1)
            return;

        DrawNebula(context, w, h);

        var t = _time;
        var speed = SpeedAt(t);

        // Field of view in pixels. Tied to the width so the field keeps its shape if
        // the window is ever resized.
        var fov = w * 0.30;

        // The vanishing point drifts and the whole field rolls. Both are sampled a
        // trail-length back in time as well, so the streaks curve with the roll
        // instead of pointing at a fixed centre.
        var head = Projection(t, w, h);
        var tail = Projection(t - TrailSeconds, w, h);

        for (int i = 0; i < _stars.Length; i++)
        {
            ref var star = ref _stars[i];

            var z = star.Z;
            var depth = 1.0 - (z - ZNear) / (ZFar - ZNear);   // 0 far … 1 near

            var alpha = Math.Pow(depth, 1.2) * 0.95;
            if (alpha <= 0.012)
                continue;

            var size = MinStarWidth + depth * depth * (MaxStarWidth - MinStarWidth);

            var x = head.Project(star.X, star.Y, z, fov);

            // Where the star was one trail-length ago. Z only ever decreases, so the
            // tail is always the point closer to the vanishing point.
            var pz = z + speed * TrailSeconds;
            var y = tail.Project(star.X, star.Y, pz, fov);

            var dx = x.X - y.X;
            var dy = x.Y - y.Y;
            var trail = Math.Sqrt(dx * dx + dy * dy);

            var brush = GetBrush(star.Tint, alpha);

            // Close stars get a soft halo. Only a handful qualify at any time, and it
            // is what reads as "bloom" without a shader.
            if (depth > 0.8)
            {
                var halo = GetHalo(star.Tint, alpha * 0.34);
                context.DrawEllipse(halo, null, x, size * 3.6, size * 3.6);
            }

            if (trail < 1.5)
            {
                // Far stars: a single pixel, the way the originals drew them. An
                // antialiased ellipse this small just looks like grey mush.
                context.FillRectangle(brush, new Rect(x.X - size * 0.5, x.Y - size * 0.5, size, size));
            }
            else
            {
                context.DrawLine(GetPen(star.Tint, alpha, size), y, x);
            }
        }
    }

    /// <summary>Vanishing point and roll at a given time.</summary>
    private readonly struct Camera
    {
        private readonly double _cx;
        private readonly double _cy;
        private readonly double _cos;
        private readonly double _sin;

        public Camera(double cx, double cy, double angle)
        {
            _cx = cx;
            _cy = cy;
            _cos = Math.Cos(angle);
            _sin = Math.Sin(angle);
        }

        public Point Project(double x, double y, double z, double fov)
        {
            var rx = x * _cos - y * _sin;
            var ry = x * _sin + y * _cos;

            return new Point(_cx + rx / z * fov, _cy + ry / z * fov);
        }
    }

    private static Camera Projection(double t, double w, double h)
        => new(w * (0.5 + 0.055 * Math.Sin(t * 0.21)),
               h * (0.5 + 0.050 * Math.Cos(t * 0.17)),
               t * RollSpeed);

    /// <summary>
    /// Two very low-opacity radial washes drifting against each other. Pure black
    /// behind the stars looks flat on a modern panel; this gives the field somewhere
    /// to be, and picks up the window's warm accent.
    /// </summary>
    private void DrawNebula(DrawingContext context, double w, double h)
    {
        var t = _time;
        var area = new Rect(0, 0, w, h);

        context.FillRectangle(
            Wash(Color.FromRgb(0xEE, 0x73, 0x26), 0x16,
                 0.78 + 0.05 * Math.Sin(t * 0.13),
                 0.20 + 0.04 * Math.Cos(t * 0.11),
                 0.78),
            area);

        context.FillRectangle(
            Wash(Color.FromRgb(0x2F, 0x5B, 0xFF), 0x12,
                 0.18 + 0.05 * Math.Cos(t * 0.09),
                 0.82 + 0.04 * Math.Sin(t * 0.12),
                 0.85),
            area);
    }

    private static RadialGradientBrush Wash(Color color, byte peakAlpha, double cx, double cy, double radius)
    {
        var centre = new RelativePoint(cx, cy, RelativeUnit.Relative);

        var brush = new RadialGradientBrush
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(radius, RelativeUnit.Relative)
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(peakAlpha, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peakAlpha / 3), color.R, color.G, color.B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));

        return brush;
    }

    private ImmutableSolidColorBrush GetBrush(int tint, double alpha)
    {
        var slot = AlphaSlot(alpha);
        var cached = _brushes[tint, slot];

        if (cached == null)
        {
            var color = Tints[tint];
            cached = new ImmutableSolidColorBrush(
                Color.FromArgb((byte)(255 * (slot + 1) / AlphaSteps), color.R, color.G, color.B));

            _brushes[tint, slot] = cached;
        }

        return cached;
    }

    private IBrush GetHalo(int tint, double alpha)
    {
        var slot = AlphaSlot(alpha);
        var cached = _halos[tint, slot];

        if (cached == null)
        {
            var color = Tints[tint];
            var peak = (byte)(255 * (slot + 1) / AlphaSteps);

            // Default centre and radii already map onto the ellipse being filled.
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak * 0.34), color.R, color.G, color.B), 0.42));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));

            cached = brush.ToImmutable();
            _halos[tint, slot] = cached;
        }

        return cached;
    }

    private ImmutablePen GetPen(int tint, double alpha, double width)
    {
        var alphaSlot = AlphaSlot(alpha);
        var widthSlot = (int)Math.Clamp(
            (width - MinStarWidth) / (MaxStarWidth - MinStarWidth) * (WidthSteps - 1), 0, WidthSteps - 1);

        var cached = _pens[tint, alphaSlot, widthSlot];

        if (cached == null)
        {
            var thickness = MinStarWidth + (MaxStarWidth - MinStarWidth) * widthSlot / (WidthSteps - 1);

            cached = new ImmutablePen(GetBrush(tint, alpha), thickness, null, PenLineCap.Round);
            _pens[tint, alphaSlot, widthSlot] = cached;
        }

        return cached;
    }

    private static int AlphaSlot(double alpha)
        => (int)Math.Clamp(alpha * AlphaSteps, 0, AlphaSteps - 1);
}
