/*
 *
 * The time warp: back a few seconds in the game (Emulation ▸ Time warp…, Ctrl+Shift+F10).
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Keeps the last <see cref="ConfigOptions.TimeWarpSeconds"/> seconds of play as moments, one
    /// per second, and puts the machine back to any of them. A moment is the machine itself —
    /// CPU, memory and chips, as a snapshot has them (<see cref="Snapshot.SaveMachine"/>) — plus
    /// the disks in the drives and a thumbnail of the picture; the input that led there is not
    /// needed, so nothing about it depends on the emulation being deterministic.
    ///
    /// Taking one costs the emulation thread a copy of the memory and a scaled-down picture once
    /// a second; the compression runs on the thread pool. The disks travel only when they
    /// changed (a disk inserted or written, <see cref="FloppyImage.Writes"/>), and moments with
    /// the same disks share one copy, so a game that never saves keeps one. Going back rebuilds
    /// the CPU and chips around the memory already there (<see cref="CPU.ResetChips"/>) and
    /// applies the moment, with the emulation parked at a frame boundary: no power-on, no TOS
    /// reload, no MIDI module power-cycle. The moments after it are dropped — the game carries
    /// on from there.
    ///
    /// Three rules keep a moment true to the machine. It is never taken while a timed STX
    /// command is delivering its data, since a snapshot would complete it and change the machine
    /// (a protection timing its sectors); the next frame is tried instead. It is not available
    /// with a hard disk attached — what the program wrote to it cannot be taken back, and the
    /// ACSI controller's command in progress and the GEMDOS drive's open files are not machine
    /// state — nor in an online session or a recording, whose machine belongs to the session.
    /// And going back plugs the host's controls into the restored machine again (a joystick
    /// held then is not the one held now) and silences the MIDI output, whose notes belong to
    /// the moment it left.
    /// </summary>
    public static class TimeWarp
    {
        /// <summary>How far one press of Ctrl+Shift+F10 goes back.</summary>
        public const int StepSeconds = 5;

        /// <summary>One moment of the machine.</summary>
        public sealed class Moment
        {
            /// <summary>The machine's frame number when it was taken, counted from power-on.</summary>
            public long Frame { get; init; }

            /// <summary>The picture, scaled down: RGBA pixels (0xAABBGGRR in memory, like the
            /// framebuffer), <see cref="ThumbWidth"/> × <see cref="ThumbHeight"/>.</summary>
            public uint[] Thumbnail { get; init; }
            public int ThumbWidth { get; init; }
            public int ThumbHeight { get; init; }

            internal byte[] Raw;            // the machine, until packed
            internal byte[] Packed;         // the machine, deflated
            internal Disks Disks;
            internal Task Packing;
        }

        /// <summary>The disks in the drives as one or more moments saw them.</summary>
        internal sealed class Disks
        {
            public object A, B;             // FloppyImage.MediaToken of each drive
            public int WritesA, WritesB;
            public byte[] Raw;              // both drives, until packed (Snapshot.MediaRaw)
            public byte[] Section;          // an "FLP " payload
        }

        static readonly object _lock = new();
        static readonly List<Moment> _moments = new();

        // Frames since power-on, and the frame the next moment is due at. The emulation thread
        // counts; going back sets them with the emulation parked.
        static long _frame;
        static long _due;

        /// <summary>Frames since power-on: the time the moments are measured against.</summary>
        public static long Frame => Volatile.Read(ref _frame);

        /// <summary>Emulated frames in a second (50 in colour, ~71 on a monochrome monitor).</summary>
        public static double FramesPerSecond => 1.0 / VideoTiming.FRAME_SECONDS;

        /// <summary>Whether the time warp is switched on: as the user chose in its window, or, until
        /// they have, everywhere but on a Raspberry Pi (<see cref="ConfigOptions.TimeWarpEnabled"/>).</summary>
        public static bool Enabled => ConfigOptions.RunninConfig.TimeWarpEnabled ?? !Config.HostIsRaspberryPi;

        /// <summary>
        /// The switch in the window (UI thread): the choice is kept in the configuration, and
        /// switching off lets go of every moment and the memory they hold. Switched on, the first
        /// moment is taken at the next frame.
        /// </summary>
        public static void SetEnabled(bool on)
        {
            ConfigOptions.RunninConfig.TimeWarpEnabled = on;
            Program.Config.DumpJsonConfig();

            if (on)
            {
                _due = Frame;
            }
            else
            {
                lock (_lock)
                    _moments.Clear();
            }
        }

        /// <summary>Why the time warp can neither keep nor use moments right now, or null.</summary>
        public static string Unavailable
        {
            get
            {
                if (!Enabled)
                    return "The time warp is off: switch it on in Emulation ▸ Time warp… (Ctrl+Shift+F10).";

                if (ConfigOptions.RunninConfig.TimeWarpSeconds <= 0)
                    return "The time warp is switched off: TimeWarpSeconds is 0 in the configuration file.";

                if (Netplay.MachineInput.Mode != Netplay.MachineInput.Modes.Live)
                    return "Not available while recording, playing back or playing together online: the machine belongs to the session.";

                if (Netplay.SerialCable.Plugged || Netplay.MidiCable.Plugged)
                    return "Not available in a cable game: the other machines would not go back with this one.";

                if (HardDiskGames.Active || GemdosHD.Enabled || Acsi.AttachedImagePath != null)
                    return "Not available with a hard disk attached — a hard-disk game from the library, an ACSI image or a GEMDOS folder: " +
                           "what the program writes to it cannot be taken back.";

                return null;
            }
        }

        /// <summary>The moments kept, oldest first.</summary>
        public static IReadOnlyList<Moment> Moments
        {
            get
            {
                lock (_lock)
                    return _moments.ToArray();
            }
        }

        /// <summary>A power-on: a new machine has no past (ASEMain.TurnOn).</summary>
        public static void Clear()
        {
            lock (_lock)
                _moments.Clear();

            Volatile.Write(ref _frame, 0);
            _due = 0;
        }

        // ==================== Keeping moments (emulation thread) ====================

        /// <summary>
        /// End of a frame, once it has been published (<paramref name="picture"/>): every second,
        /// a moment of the machine. Emulation thread; nothing at all on the other frames.
        /// </summary>
        public static void EndFrame(uint[] picture)
        {
            long frame = _frame + 1;
            Volatile.Write(ref _frame, frame);

            if (frame < _due || picture == null || Unavailable != null)
                return;

            // An STX command delivering its data: the next frame
            byte[] raw = Snapshot.SaveMachine();
            if (raw == null)
                return;

            _due = frame + (long)Math.Round(FramesPerSecond);

            var thumb = Thumbnail(picture, out int width, out int height);
            var moment = new Moment { Frame = frame, Thumbnail = thumb, ThumbWidth = width, ThumbHeight = height, Raw = raw };
            bool newDisks;

            lock (_lock)
            {
                var previous = _moments.Count > 0 ? _moments[^1].Disks : null;
                newDisks = previous == null || !DrivesHold(previous);
                moment.Disks = newDisks ? CurrentDisks() : previous;

                _moments.Add(moment);

                // One a second: the oldest goes once the window is full
                int keep = ConfigOptions.RunninConfig.TimeWarpSeconds + 1;
                if (_moments.Count > keep)
                    _moments.RemoveRange(0, _moments.Count - keep);
            }

            var disks = newDisks ? moment.Disks : null;
            moment.Packing = Task.Run(() =>
            {
                moment.Packed = Snapshot.Pack(w => w.Bytes(moment.Raw));
                moment.Raw = null;

                if (disks != null)
                {
                    disks.Section = Snapshot.MediaSection(disks.Raw);
                    disks.Raw = null;
                }
            });
        }

        // The disks in the drives now, as they are (uncompressed: the pool packs them)
        static Disks CurrentDisks() => new Disks
        {
            A = ASEMain.driveA.MediaToken,
            B = ASEMain.driveB.MediaToken,
            WritesA = ASEMain.driveA.Writes,
            WritesB = ASEMain.driveB.Writes,
            Raw = Snapshot.MediaRaw(),
        };

        // Whether the drives still hold exactly these disks: the same ones, written no more
        static bool DrivesHold(Disks disks) =>
            disks.A == ASEMain.driveA.MediaToken && disks.WritesA == ASEMain.driveA.Writes &&
            disks.B == ASEMain.driveB.MediaToken && disks.WritesB == ASEMain.driveB.Writes;

        /// <summary>
        /// The picture as the screen shows it (with or without borders, like a screenshot),
        /// scaled down by 4 in each direction of the displayed picture — 4 across, and 2 down
        /// in colour, whose lines are doubled on screen. Each pixel is the mean of the block, so
        /// dithering comes out as the tone it stands for.
        /// </summary>
        static uint[] Thumbnail(uint[] picture, out int width, out int height)
        {
            bool mono = VideoTiming.Mono;
            bool borders = mono || ConfigOptions.RunninConfig.ShowBorders;
            int srcX = borders ? 0 : VideoTiming.DISPLAY_ORIGIN_X;
            int srcY = borders ? 0 : VideoTiming.DISPLAY_ORIGIN_Y;
            int srcW = borders ? VideoTiming.BUFFER_WIDTH : VideoTiming.DISPLAY_TEX_WIDTH;
            int srcH = borders ? VideoTiming.BUFFER_HEIGHT : VideoTiming.DISPLAY_TEX_HEIGHT;

            const int stepX = 4;
            int stepY = mono ? 4 : 2;
            int area = stepX * stepY;

            width = srcW / stepX;
            height = srcH / stepY;
            var thumb = new uint[width * height];

            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int y = 0; y < stepY; y++)
                    {
                        int row = (srcY + ty * stepY + y) * VideoTiming.BUFFER_WIDTH + srcX + tx * stepX;
                        for (int x = 0; x < stepX; x++)
                        {
                            uint p = picture[row + x];
                            r += (int)(p & 0xFF);
                            g += (int)((p >> 8) & 0xFF);
                            b += (int)((p >> 16) & 0xFF);
                        }
                    }

                    thumb[ty * width + tx] = 0xFF000000u | (uint)(b / area) << 16 | (uint)(g / area) << 8 | (uint)(r / area);
                }
            }

            return thumb;
        }

        // ==================== Going back (UI thread) ====================

        /// <summary>Back <paramref name="seconds"/> seconds — <see cref="StepSeconds"/> for
        /// Ctrl+Shift+F10, 5 or 10 for a gamepad button — to the newest moment at least that old,
        /// or the oldest kept when none is. How far it went, in seconds, or -1 with the reason.</summary>
        public static int StepBack(int seconds, out string error)
        {
            var moments = Moments;
            error = Unavailable;
            if (error != null)
                return -1;

            if (moments.Count == 0)
            {
                error = "There is nothing to go back to yet.";
                return -1;
            }

            long now = Frame;
            long target = now - (long)Math.Round(seconds * FramesPerSecond);
            var moment = moments.LastOrDefault(m => m.Frame <= target) ?? moments[0];

            if (!GoTo(moment, out error))
                return -1;

            return (int)Math.Round((now - moment.Frame) / FramesPerSecond);
        }

        /// <summary>
        /// Puts the machine back to <paramref name="moment"/> and carries on from there; the
        /// moments after it are dropped. UI thread. False with the reason when it cannot.
        /// </summary>
        public static bool GoTo(Moment moment, out string error)
        {
            error = Unavailable;
            if (error != null)
                return false;

            Moment[] moments;
            lock (_lock)
                moments = _moments.ToArray();

            if (Array.IndexOf(moments, moment) < 0)
            {
                error = "That moment is no longer kept.";
                return false;
            }

            // The pool packs a moment within milliseconds of taking it; going back needs it, and
            // its disks, packed
            foreach (var m in moments)
            {
                try { m.Packing?.Wait(); }
                catch (AggregateException) { /* checked below, for the one that matters */ }
            }

            if (moment.Packed == null || moment.Disks.Section == null)
            {
                error = "That moment could not be kept.";
                return false;
            }

            byte[] machine;
            try
            {
                var r = Snapshot.Unpack(moment.Packed);
                machine = r.Bytes(r.Remaining);
            }
            catch (InvalidDataException ex)
            {
                error = ex.Message;
                return false;
            }

            if (!ASEMain.RunWhilePaused(() => Restore(moment, machine), out error))
            {
                ColoredConsole.WriteLine($"Time warp: [[red]]the machine could not be put back[[/red]] ({error}); a reset brings it back to life", ConfigOptions.DebugModes.None);
                return false;
            }

            return true;
        }

        // With the emulation parked at a frame boundary
        static void Restore(Moment moment, byte[] machine)
        {
            // The audio thread renders from the PSG being replaced: not while it is
            ASEMain.WithAudioLocked(() =>
            {
                CPU.ResetChips();
                Snapshot.ApplyMachine(machine);
            });

            ASEMain.ForgetCpuHalt();

            // Going back in the game, not in the calendar: the clocks keep the host's time
            ASEMain.SetClocksFromHost(powerOn: false);

            // The disks as they were, when they are not still exactly those
            if (!DrivesHold(moment.Disks))
                Snapshot.RestoreMediaSection(moment.Disks.Section);

            // What the host holds goes back into the ports (the next event drain), and the
            // notes of the moment the machine left stop
            HostInput.InvalidatePortConfiguration();
            MidiManager.Silence();

            lock (_lock)
            {
                int index = _moments.IndexOf(moment);
                if (index >= 0)
                    _moments.RemoveRange(index + 1, _moments.Count - index - 1);
            }

            Volatile.Write(ref _frame, moment.Frame);
            _due = moment.Frame + (long)Math.Round(FramesPerSecond);
        }
    }
}
