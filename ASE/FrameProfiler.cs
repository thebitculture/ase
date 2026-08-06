/*
 *
 * Per-frame timing breakdown, printed to the console every N frames (--profile[=N]).
 * Answers the first question when the emulator stutters on a slow host: is the machine
 * short of CPU, or is the picture just not being presented evenly?
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ASE
{
    /// <summary>
    /// Accumulates where each emulated frame's time goes and prints the averages over the last
    /// N frames as a single console line.
    /// <para>
    /// The emulation thread owns the phase accumulators (no locking: it is the only writer), while
    /// the GL thread reports its own render timings through <see cref="ReportGlFrame"/>, which is
    /// the only cross-thread path and uses interlocked adds.
    /// </para>
    /// <para>
    /// How to read a line: <b>work</b> is what emulating one frame costs and <b>idle</b> is what
    /// was left over of the 20 ms PAL budget. Work close to 20 ms (idle near zero, <b>late</b>
    /// climbing) means the host cannot keep up — that is a CPU problem. Plenty of idle time while
    /// the picture still stutters means the emulation is fine and the problem is presentation:
    /// look at the GL figures, and remember that 50 Hz emulation on a 60 Hz screen judders by
    /// construction, no matter how much headroom there is.
    /// </para>
    /// <para>
    /// Cost when disabled: one static bool test per phase. Cost when enabled: about 2,500 clock
    /// reads per second (roughly 0.3% on a Raspberry Pi 4), which is included in the numbers it
    /// reports.
    /// </para>
    /// </summary>
    public static class FrameProfiler
    {
        /// <summary>Frames between report lines; 0 disables the profiler entirely.</summary>
        public static int Interval { get; private set; }

        /// <summary>Fast path check, read on every phase boundary of every scanline.</summary>
        public static bool Enabled;

        // PAL frame budget. Work above this line cannot run at 100% speed.
        const double FrameBudgetMs = 1000.0 / 50.0;

        // ---- Emulation-thread accumulators (ticks) ----
        static long _cpu, _video, _input, _publish, _idle, _frame, _maxWork;
        static long _idleThisFrame;     // needed to separate work from waiting within one frame
        static int _frames, _late;

        // ---- GL-thread accumulators (interlocked) ----
        static long _glRender, _glUpload;
        static int _glFrames, _glUploads;

        static double _msPerTick;

        /// <summary>
        /// Turns the profiler on with a report every <paramref name="frames"/> frames (0 = off).
        /// </summary>
        public static void Configure(int frames)
        {
            Interval = frames > 0 ? frames : 0;
            Enabled = Interval > 0;
            _msPerTick = 1000.0 / Stopwatch.Frequency;
            Reset();

            if (Enabled)
                ColoredConsole.WriteLine($"[[cyan]]Frame profiler on[[/cyan]]: one line every [[yellow]]{Interval}[[/yellow]] frames. Times are averages in ms.");
        }

        /// <summary>Timestamp for the start of a phase; 0 when the profiler is off.</summary>
        public static long Stamp() => Enabled ? Stopwatch.GetTimestamp() : 0;

        // Each Add* closes the phase that started at t0 and opens the next one by returning the
        // current timestamp, so a scanline costs one clock read per phase boundary instead of two.

        public static long AddInput(long t0)   { if (!Enabled) return 0; long t = Stopwatch.GetTimestamp(); _input   += t - t0; return t; }
        public static long AddCpu(long t0)     { if (!Enabled) return 0; long t = Stopwatch.GetTimestamp(); _cpu     += t - t0; return t; }
        public static long AddVideo(long t0)   { if (!Enabled) return 0; long t = Stopwatch.GetTimestamp(); _video   += t - t0; return t; }
        public static long AddPublish(long t0) { if (!Enabled) return 0; long t = Stopwatch.GetTimestamp(); _publish += t - t0; return t; }

        /// <summary>Closes the wait that ends a frame; also charged to that frame, so the
        /// work/idle split below is per frame and not just an average.</summary>
        public static long AddIdle(long t0)
        {
            if (!Enabled) return 0;
            long t = Stopwatch.GetTimestamp();
            _idle += t - t0;
            _idleThisFrame += t - t0;
            return t;
        }

        /// <summary>
        /// Closes the frame that started at <paramref name="frameStart"/> and prints the averages
        /// when <see cref="Interval"/> frames have been collected.
        /// </summary>
        public static void EndFrame(long frameStart)
        {
            if (!Enabled) return;

            long frame = Stopwatch.GetTimestamp() - frameStart;
            _frame += frame;

            // Work is the frame minus the time deliberately spent waiting for the next one, so it
            // is comparable against the budget whether or not the host had headroom to spare.
            long work = frame - _idleThisFrame;
            if (work > _maxWork) _maxWork = work;
            if (work * _msPerTick > FrameBudgetMs) _late++;
            _idleThisFrame = 0;

            if (++_frames < Interval) return;

            Report();
            Reset();
        }

        /// <summary>
        /// Called from the GL thread once per presented frame with the ticks spent inside
        /// OnOpenGlRender and, of those, the ticks spent uploading the framebuffer texture.
        /// <paramref name="uploaded"/> is false on frames drawn without a new emulated frame to
        /// push, which are counted separately so the reported 'tex' stays a per-upload cost.
        /// <para>
        /// This measures the CPU side of drawing. Issuing the draw call does not wait for the GPU,
        /// so an expensive CRT shader shows up as a drop in the reported GL frame rate (the driver
        /// throttles) rather than as a large render time.
        /// </para>
        /// </summary>
        public static void ReportGlFrame(long renderTicks, long uploadTicks, bool uploaded)
        {
            if (!Enabled) return;
            Interlocked.Add(ref _glRender, renderTicks);
            Interlocked.Increment(ref _glFrames);

            if (uploaded)
            {
                Interlocked.Add(ref _glUpload, uploadTicks);
                Interlocked.Increment(ref _glUploads);
            }
        }

        static void Report()
        {
            double n = _frames;
            double frameMs   = _frame   * _msPerTick / n;
            double idleMs    = _idle    * _msPerTick / n;
            double cpuMs     = _cpu     * _msPerTick / n;
            double videoMs   = _video   * _msPerTick / n;
            double inputMs   = _input   * _msPerTick / n;
            double publishMs = _publish * _msPerTick / n;
            double workMs    = frameMs - idleMs;
            double maxWorkMs = _maxWork * _msPerTick;

            // Everything the phases did not account for: MFP/ACIA housekeeping, the VBL, the LED
            // refresh and the loop itself.
            double otherMs = Math.Max(0.0, workMs - cpuMs - videoMs - inputMs - publishMs);
            double load = workMs / FrameBudgetMs * 100.0;

            int glFrames = Interlocked.Exchange(ref _glFrames, 0);
            int glUploads = Interlocked.Exchange(ref _glUploads, 0);
            double glRenderMs = Interlocked.Exchange(ref _glRender, 0) * _msPerTick;
            double glUploadMs = Interlocked.Exchange(ref _glUpload, 0) * _msPerTick;
            double elapsedS = _frame * _msPerTick / 1000.0;

            // 'tex' is the average of the uploads that actually happened; the x/y counts show how
            // many of the drawn frames carried a new emulated frame (the rest reuse the texture).
            var c = CultureInfo.InvariantCulture;
            string gl = glFrames > 0
                ? string.Format(c, "{0:F2} ms (tex {1:F2} on {2}/{3}), {4:F1} fps",
                                glRenderMs / glFrames,
                                glUploads > 0 ? glUploadMs / glUploads : 0.0,
                                glUploads, glFrames,
                                elapsedS > 0 ? glFrames / elapsedS : 0.0)
                : "no frames";

            ColoredConsole.WriteLine(string.Format(c,
                "[Profile] {0} frames | work [[yellow]]{1:F2}[[/yellow]]/{2:F1} ms ([[yellow]]{3:F0}%[[/yellow]], max {4:F2}) | " +
                "cpu {5:F2} video {6:F2} input {7:F2} publish {8:F2} other {9:F2} | idle {10:F2} | late [[yellow]]{11}[[/yellow]] | GL {12}",
                _frames, workMs, FrameBudgetMs, load, maxWorkMs,
                cpuMs, videoMs, inputMs, publishMs, otherMs, idleMs, _late, gl));
        }

        static void Reset()
        {
            _cpu = _video = _input = _publish = _idle = _frame = _maxWork = 0;
            _idleThisFrame = 0;
            _frames = _late = 0;
            Interlocked.Exchange(ref _glRender, 0);
            Interlocked.Exchange(ref _glUpload, 0);
            Interlocked.Exchange(ref _glFrames, 0);
            Interlocked.Exchange(ref _glUploads, 0);
        }
    }
}
