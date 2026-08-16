/*
 *
 * VideoTiming.cs
 *
 * Models the Atari ST GLUE/Shifter video timing with cycle precision *within a line*
 * (not per pixel). The emulation loop keeps running the CPU in 448 + 64 cycle chunks,
 * but this module:
 *
 *   - Tracks the CPU clock at the start of every scanline (LineStartClock).
 *   - Captures writes to the sync register ($FF820A) and the resolution register
 *     ($FF8260) together with their exact cycle inside the line (Memory.Write8).
 *   - Resolves, per line, the Display Enable (DE) start/stop cycles -> left/right
 *     borders, and runs a vertical state machine -> top/bottom borders.
 *   - Computes the Video Address Pointer (VAP) live, so a game reading $FF8205/07/09
 *     at any point gets the correct value (this is what some games poll in tight loops).
 *
 * The cycle values follow the canonical ST timing (a WS3-compatible profile). They are
 * exposed as constants so they can be tuned against real demos/games.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// GLUE/Shifter timing model: live Video Address Pointer and screen border (overscan)
    /// emulation, including the left/right/top/bottom border-removal tricks.
    /// </summary>
    public static class VideoTiming
    {
        // ===================== Colour (PAL) horizontal timing (CPU cycles, 8 MHz) =====================
        // A PAL scanline is 512 cycles. During DE the shifter reads 0.5 bytes/cycle, so the
        // normal 320-cycle display window reads 160 bytes (low and medium resolution alike).
        public const int COLOR_CYCLES_PER_LINE = 512;
        const int COLOR_SCANLINES  = 313;
        const int COLOR_DE_STOP    = 376;   // where the emulation loop fires the HBL / Timer B

        const int DE_START_50 = 56;
        const int DE_STOP_50  = 376;   // (376-56)/2 = 160 bytes
        const int DE_START_60 = 52;
        const int DE_STOP_60  = 372;
        const int DE_START_LEFT_OPEN = 4;     // left border removed  (~ +26 bytes on the left)
        const int DE_STOP_RIGHT_OPEN = 464;   // right border removed (~ +44 bytes on the right)

        // ===================== Colour vertical timing (scanline numbers) =====================
        const int V_START_50 = 63;
        const int V_STOP_50  = 263;    // 200 visible lines (63..262)
        const int V_START_60 = 34;
        const int V_STOP_60  = 234;
        const int V_STOP_BOTTOM_OPEN = 308;   // safety limit for an opened bottom border

        // ===================== Monochrome (SM124) timing =====================
        // High resolution: 640x400, one bitplane, ~71.25 Hz. 501 scanlines of 224 CPU cycles.
        // Each line fetches 40 words (80 bytes) over a ~160-cycle DE window; 80*400 = 32000 bytes,
        // the ST monochrome screen. There are no colour/border tricks here — the monitor shows the
        // full 640x400 with only a thin blanking margin — so the model is deliberately simple.
        const int MONO_CYCLES_PER_LINE = 224;
        const int MONO_SCANLINES       = 501;
        const int MONO_WIDTH           = 640;
        const int MONO_HEIGHT          = 400;
        const int MONO_BYTES_PER_LINE  = 80;
        const int MONO_V_START         = 36;                    // first displayed scanline
        const int MONO_V_STOP          = MONO_V_START + MONO_HEIGHT;   // 436
        const int MONO_DE_START        = 24;                    // left edge of the fetched data
        const int MONO_DE_STOP         = MONO_DE_START + 160;   // where the loop fires HBL / Timer B

        // ===================== Visible window (what a real colour TV shows, centred) =====================
        // Horizontal units are "low-res pixels" == DE cycles; each maps to 2 texture pixels
        // (low-res is pixel-doubled). Vertical units are scanlines, 1:1 with texture rows.
        //
        // The window matches what a 4:3 PAL tube actually displays: ~52 µs of active line
        // (52 µs at 8 MHz = 416 px) over ~288 visible lines, positioned so the normal 320x200
        // display sits in the centre (the display centre is cycle 216 = (56+376)/2 and line
        // 163 = (63+263)/2):
        //   left/right border = 48 px each      top/bottom border = 44 lines each
        // With the 12/13 ST pixel aspect this window is *exactly* the 4:3 tube. An opened
        // bottom border fits in full; border-removal tricks can drive DE beyond the window
        // horizontally (from cycle 4 on the left, up to cycle 464 on an opened right border) —
        // that content is clipped here just as a TV loses it to blanking/overscan. Widen the
        // window if you ever need to inspect it.
        public const int VISIBLE_LEFT_CYCLE  = 8;     // 56 - 48   (display centred horizontally)
        public const int VISIBLE_RIGHT_CYCLE = 424;   // 376 + 48
        public const int VISIBLE_TOP_LINE    = 19;    // 63 - 44   (display centred vertically)
        public const int VISIBLE_BOTTOM_LINE = 307;   // 263 + 44

        const int COLOR_WIDTH  = (VISIBLE_RIGHT_CYCLE - VISIBLE_LEFT_CYCLE) * 2; // 832
        const int COLOR_HEIGHT = VISIBLE_BOTTOM_LINE - VISIBLE_TOP_LINE;          // 288

        // Framebuffers and the GL texture are allocated at the largest of both modes so switching
        // monitor type (which happens only on a reset) never has to reallocate the shared buffers.
        public const int MAX_WIDTH     = COLOR_WIDTH;    // 832 (colour overscan is the widest)
        public const int MAX_HEIGHT    = MONO_HEIGHT;    // 400 (mono is the tallest)
        public const int MAX_VIEW_SIZE = MAX_WIDTH * MAX_HEIGHT;

        // Texture rectangle of the normal (non-overscan) 320x200 display inside the colour buffer.
        // Used to crop back to the borderless view when ShowBorders is off. With the centred
        // window the display is symmetric inside the buffer (96 px / 44 lines margin each side).
        public const int DISPLAY_ORIGIN_X  = (DE_START_50 - VISIBLE_LEFT_CYCLE) * 2;       // 96
        public const int DISPLAY_ORIGIN_Y  = V_START_50 - VISIBLE_TOP_LINE;                // 44
        public const int DISPLAY_TEX_WIDTH = (DE_STOP_50 - DE_START_50) * 2;               // 640
        public const int DISPLAY_TEX_HEIGHT = V_STOP_50 - V_START_50;                      // 200

        // ===================== Active geometry (chosen at Reset from the monitor type) =====================
        // These replace the old compile-time constants: colour and monochrome monitors have
        // completely different resolutions, line counts and refresh rates. The colour values
        // below are the defaults, so behaviour is unchanged until a monochrome monitor is selected.
        public static bool Mono { get; private set; }
        public static int  BUFFER_WIDTH        { get; private set; } = COLOR_WIDTH;   // active stride & width
        public static int  BUFFER_HEIGHT       { get; private set; } = COLOR_HEIGHT;
        public static int  CYCLES_PER_LINE     { get; private set; } = COLOR_CYCLES_PER_LINE;
        public static int  SCANLINES_PER_FRAME { get; private set; } = COLOR_SCANLINES;
        public static int  DE_STOP_CYCLE       { get; private set; } = COLOR_DE_STOP;       // loop HBL/Timer B split
        public static int  RENDER_TOP_LINE     { get; private set; } = VISIBLE_TOP_LINE;    // scanline shown at buffer row 0
        public static double FRAME_SECONDS     { get; private set; } = (double)COLOR_SCANLINES * COLOR_CYCLES_PER_LINE / 8_000_000.0;

        // Bumped whenever the active geometry changes, so the GL thread knows to resize its texture.
        public static int GeometryGeneration { get; private set; }

        /// <summary>Selects colour or monochrome geometry from the running config. Called by Reset().</summary>
        static void ConfigureGeometry()
        {
            bool mono = ConfigOptions.RunninConfig.MonochromeMonitor;

            if (mono)
            {
                BUFFER_WIDTH = MONO_WIDTH; BUFFER_HEIGHT = MONO_HEIGHT;
                CYCLES_PER_LINE = MONO_CYCLES_PER_LINE; SCANLINES_PER_FRAME = MONO_SCANLINES;
                DE_STOP_CYCLE = MONO_DE_STOP; RENDER_TOP_LINE = MONO_V_START;
            }
            else
            {
                BUFFER_WIDTH = COLOR_WIDTH; BUFFER_HEIGHT = COLOR_HEIGHT;
                CYCLES_PER_LINE = COLOR_CYCLES_PER_LINE; SCANLINES_PER_FRAME = COLOR_SCANLINES;
                DE_STOP_CYCLE = COLOR_DE_STOP; RENDER_TOP_LINE = VISIBLE_TOP_LINE;
            }

            FRAME_SECONDS = (double)SCANLINES_PER_FRAME * CYCLES_PER_LINE / 8_000_000.0;

            if (mono != Mono)
            {
                Mono = mono;
                GeometryGeneration++;
            }
        }

        // ===================== State =====================
        struct VidEvent { public int Cycle; public bool IsRes; public byte Val; }

        static readonly VidEvent[] _events = new VidEvent[64];
        static int _eventCount;

        // ===================== Mid-line palette (Spectrum 512 / palette splits) =====================
        // Every palette-register byte write is captured together with the cycle inside the line
        // at which it happened, so the renderer can apply it at the matching horizontal position
        // instead of using one palette for the whole scanline. This is what makes the Spectrum
        // 512 trick (up to 512 colours by reloading the palette several times per line) and
        // ordinary raster palette splits visible.
        public struct PalEvent { public int Cycle; public int ByteOffset; public byte Val; }
        static readonly PalEvent[] _palEvents = new PalEvent[512];
        static int _palEventCount;
        static readonly byte[] _lineStartPalette = new byte[32];

        public static int PaletteEventCount => _palEventCount;
        public static PalEvent[] PaletteEvents => _palEvents;
        public static byte[] LineStartPalette => _lineStartPalette;

        static readonly bool _vapLegacy = Environment.GetEnvironmentVariable("ASE_VAP_LEGACY") != null;

        static byte _currentSync = 0x02;        // bit 1 = 1 -> 50 Hz (PAL) by default
        static byte _currentRes  = 0x00;        // low resolution
        static byte _syncAtLineStart = 0x02;
        static byte _resAtLineStart  = 0x00;

        static uint _videoCounter;              // absolute shifter address (advances each frame)
        static uint _lineStartCounter;          // _videoCounter at the start of the current line
        static long _lineStartClock;
        static int  _currentLine;
        static bool _vDisplayOn;                // vertical display state machine output
        static bool _vDisplayDone;              // display already finished this frame (no restart)
        static bool _currentLineHasDisplay;
        static bool _topBorderOpen;             // display starts ~29 lines earlier (line 34)
        static bool _bottomBorderOpen;          // display runs past line 263 into the bottom border

        /// <summary>CPU clock value captured at the start of the current scanline.</summary>
        public static long LineStartClock => _lineStartClock;

        /// <summary>Per-line result handed to the renderer.</summary>
        public struct LineInfo
        {
            public bool Visible;       // line falls inside the visible vertical window
            public bool HasDisplay;    // the shifter actually fetched data on this line
            public int  Line;
            public uint VideoAddr;     // shifter address at the start of the line
            public int  DeStart;       // DE rise cycle (left edge of the fetched data)
            public int  DeStop;        // DE fall cycle (right edge of the fetched data)
            public byte Res;           // resolution in effect for this line
        }

        /// <summary>Resets persistent state (called on cold/hard reset).</summary>
        public static void Reset()
        {
            // Pick colour vs monochrome geometry for this power-on (the monitor type can only
            // change through a reset, so the whole video pipeline is fixed for the session).
            ConfigureGeometry();

            _currentSync = 0x02;
            _currentRes = Mono ? (byte)0x02 : (byte)0x00;
            _syncAtLineStart = 0x02;
            _resAtLineStart = _currentRes;
            _videoCounter = 0;
            _lineStartCounter = 0;
            _lineStartClock = 0;
            _currentLine = 0;
            _vDisplayOn = false;
            _currentLineHasDisplay = false;
            _eventCount = 0;
        }

        /// <summary>
        /// Re-latches the sync ($FF820A) and resolution ($FF8260) state from the restored port
        /// array after loading a snapshot. The rest of the per-frame/per-line state rebuilds
        /// itself on the next StartFrame/StartLine.
        /// </summary>
        public static void RestoreFromPorts()
        {
            _currentSync = ASEMain._mem.Ports[(int)(Memory.STPortAdress.ST_TVHz - Memory.PortsBase)];
            _currentRes = ASEMain._mem.Ports[(int)(Memory.STPortAdress.ST_RES - Memory.PortsBase)];
            _syncAtLineStart = _currentSync;
            _resAtLineStart = _currentRes;
        }

        /// <summary>Start of a frame: reload the shifter address from the video base register.</summary>
        public static void StartFrame(uint videoBase)
        {
            _videoCounter = videoBase & 0xFFFFFFu;
            _lineStartCounter = _videoCounter;
            _vDisplayOn = false;
            _vDisplayDone = false;
            _topBorderOpen = false;
            _bottomBorderOpen = false;
        }

        /// <summary>
        /// Start of a scanline. Captures the CPU clock, clears the per-line event list and
        /// advances the vertical state machine using the inherited frequency (the top/bottom
        /// border decision is taken near the line start, i.e. with the frequency left over
        /// from the previous line).
        /// </summary>
        public static void StartLine(int line, long clock)
        {
            _currentLine = line;
            _lineStartClock = clock;
            _syncAtLineStart = _currentSync;
            _resAtLineStart = _currentRes;
            _eventCount = 0;
            _lineStartCounter = _videoCounter;

            // Snapshot the palette as it stands at the start of the line and clear the per-line
            // palette-write list; mid-line writes are captured during the line and replayed by
            // the renderer on top of this snapshot.
            _palEventCount = 0;
            Array.Copy(ASEMain._mem.Ports,
                       (int)(Memory.STPortAdress.ST_PALLETE - Memory.PortsBase),
                       _lineStartPalette, 0, 32);

            // Prediction for the live VAP. The persistent state is advanced authoritatively in
            // ResolveLine, once this line's sync writes (which may open a border) are known; here
            // we only predict from the state left by the previous line.
            bool pred = _vDisplayOn, done = _vDisplayDone;
            AdvanceVerticalDisplay(line, ref pred, ref done);
            _currentLineHasDisplay = pred;
        }

        // Vertical display state machine. The display starts at line 63 (50 Hz) — or line 34 when
        // the top border is opened — and stops at line 263 — or runs to the safety limit when the
        // bottom border is opened. It starts/stops once per frame (no restart).
        static void AdvanceVerticalDisplay(int line, ref bool displayOn, ref bool displayDone)
        {
            if (Mono)
            {
                // No border tricks on a monochrome monitor: display is simply on for the 400
                // active lines.
                displayOn = line >= MONO_V_START && line < MONO_V_STOP;
                displayDone = line >= MONO_V_STOP;
                return;
            }

            if (!displayOn && !displayDone)
            {
                int startLine = _topBorderOpen ? V_START_60 : V_START_50;
                if (line >= startLine)
                    displayOn = true;
            }
            if (displayOn)
            {
                int stopLine = _bottomBorderOpen ? V_STOP_BOTTOM_OPEN : V_STOP_50;
                if (line >= stopLine)
                {
                    displayOn = false;
                    displayDone = true;
                }
            }
        }

        public static void OnSyncWrite(byte v, int cycleInLine)
        {
            _currentSync = v;
            AddEvent(cycleInLine, false, v);

            // Detect the 50/60 Hz pulses that open the vertical borders. The trick flips to 60 Hz
            // around the GLUE's vertical compare line; we flag it for the whole critical window so
            // the exact cycle of the pulse (or which of the two lines it lands on) does not matter.
            if ((v & 0x02) == 0)   // switched to 60 Hz
            {
                // Bottom border: a 60 Hz pulse around the 50 Hz stop line (263) makes the display
                // miss its stop and keep going into the bottom border. (Pang fires this at line 262.)
                if (_currentLine >= V_STOP_50 - 2 && _currentLine <= V_STOP_50)
                    _bottomBorderOpen = true;

                // Top border: a 60 Hz pulse around the 60 Hz start line (34), while the display is
                // still off, makes it start ~29 lines earlier than the 50 Hz start (63).
                if (!_vDisplayOn && _currentLine >= V_START_60 - 2 && _currentLine <= V_START_60 + 1)
                    _topBorderOpen = true;
            }
        }

        public static void OnResWrite(byte v, int cycleInLine)
        {
            _currentRes = v;
            AddEvent(cycleInLine, true, v);
        }

        /// <summary>
        /// Records a palette-register byte write at its cycle inside the line so the renderer can
        /// apply it at the right horizontal position (Spectrum 512 and other palette splits).
        /// <paramref name="byteOffset"/> is 0..31 relative to $FF8240.
        /// </summary>
        public static void OnPaletteWrite(int byteOffset, byte val, int cycleInLine)
        {
            if (_palEventCount >= _palEvents.Length) return;   // pathological line, ignore extras
            _palEvents[_palEventCount].Cycle = cycleInLine;
            _palEvents[_palEventCount].ByteOffset = byteOffset;
            _palEvents[_palEventCount].Val = val;
            _palEventCount++;
        }

        static void AddEvent(int cycle, bool isRes, byte val)
        {
            if (_eventCount >= _events.Length) return;   // pathological line, ignore extra events
            _events[_eventCount].Cycle = cycle;
            _events[_eventCount].IsRes = isRes;
            _events[_eventCount].Val = val;
            _eventCount++;
        }

        // True when the sync frequency in effect at the given cycle is 60 Hz (bit 1 clear).
        // Events are stored in temporal (cycle) order because writes happen as the CPU runs.
        static bool Is60AtCycle(int cycle)
        {
            byte sync = _syncAtLineStart;
            for (int i = 0; i < _eventCount; i++)
            {
                if (_events[i].IsRes) continue;
                if (_events[i].Cycle <= cycle) sync = _events[i].Val;
                else break;
            }
            return (sync & 0x02) == 0;
        }

        // Left border removal: a brief 60 Hz pulse (and back to 50 Hz) before the 50 Hz DE
        // start makes the GLUE open DE much earlier. A *sustained* switch to 60 Hz is instead
        // a normal 60 Hz line and is not treated as a left-open.
        static bool DetectLeftOpen()
        {
            bool saw60 = false, backTo50 = false;
            byte sync = _syncAtLineStart;
            if ((sync & 0x02) == 0) saw60 = true;
            for (int i = 0; i < _eventCount; i++)
            {
                if (_events[i].IsRes) continue;
                if (_events[i].Cycle >= DE_START_50) break;
                sync = _events[i].Val;
                if ((sync & 0x02) == 0) saw60 = true;
                else if (saw60) backTo50 = true;
            }
            return saw60 && backTo50;
        }

        /// <summary>
        /// Resolve the line that just finished executing: compute the horizontal DE window
        /// (left/right borders), advance the shifter address and return the render info.
        /// </summary>
        public static LineInfo ResolveLine()
        {
            if (Mono)
            {
                AdvanceVerticalDisplay(_currentLine, ref _vDisplayOn, ref _vDisplayDone);

                LineInfo mono = default;
                mono.Line = _currentLine;
                mono.Res = 0x02;                       // high resolution
                mono.Visible = _currentLine >= MONO_V_START && _currentLine < MONO_V_STOP;
                mono.HasDisplay = _vDisplayOn;
                if (!_vDisplayOn)
                    return mono;                        // outside the 400 active lines: address frozen

                mono.VideoAddr = _lineStartCounter;
                mono.DeStart = MONO_DE_START;
                mono.DeStop = MONO_DE_STOP;
                _videoCounter = (_videoCounter + MONO_BYTES_PER_LINE) & 0xFFFFFFu;
                return mono;
            }

            // Authoritative vertical decision: now that this line's sync writes are known, the
            // border flags (set in OnSyncWrite) are taken into account.
            AdvanceVerticalDisplay(_currentLine, ref _vDisplayOn, ref _vDisplayDone);

            LineInfo info = default;
            info.Line = _currentLine;
            info.Res = _resAtLineStart;
            info.Visible = _currentLine >= VISIBLE_TOP_LINE && _currentLine < VISIBLE_BOTTOM_LINE;
            info.HasDisplay = _vDisplayOn;

            if (!_vDisplayOn)
                return info;   // no fetch on this line; the shifter address is frozen

            // ---- DE start (left border) ----
            int deStart;
            if (DetectLeftOpen())
                deStart = DE_START_LEFT_OPEN;
            else if (Is60AtCycle(DE_START_60))
                deStart = DE_START_60;
            else
                deStart = DE_START_50;

            // ---- DE stop (right border) ----
            // A 60 Hz frequency seen at cycle 372 stops DE early (normal 60 Hz line). If it is
            // still 50 Hz at 372 but 60 Hz at 376, neither stop marker fired -> DE runs on to
            // the end of the line (right border removed).
            int deStop;
            if (Is60AtCycle(DE_STOP_60))
                deStop = DE_STOP_60;
            else if (Is60AtCycle(DE_STOP_50))
                deStop = DE_STOP_RIGHT_OPEN;
            else
                deStop = DE_STOP_50;

            info.VideoAddr = _lineStartCounter;
            info.DeStart = deStart;
            info.DeStop = deStop;

            int bytes = (deStop - deStart) / 2;

            // STE: extra words appended per line (line-width register $FF820F).
            uint steExtra = 0;
            if (ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.STE)
                steExtra = (uint)ASEMain._mem.Read8(Memory.STPortAdress.ST_LINEWIDTH) * 2u;

            _videoCounter = (_videoCounter + (uint)bytes + steExtra) & 0xFFFFFFu;
            return info;
        }

        /// <summary>
        /// Live Video Address Pointer ($FF8205/07/09). Advances smoothly through the active
        /// display and stays frozen in the borders, matching what tight raster loops expect.
        /// </summary>
        public static uint GetCurrentVideoAddress()
        {
            if (!_currentLineHasDisplay)
                return _lineStartCounter & 0xFFFFFFu;

            if (Mono)
            {
                // One word every 4 CPU cycles across the 160-cycle DE window (40 words = 80 bytes).
                int cyc = (int)(CPU._moira.Clock - _lineStartClock);
                int mwords = (cyc - MONO_DE_START) / 4;
                if (mwords < 0) mwords = 0;
                else if (mwords > MONO_BYTES_PER_LINE / 2) mwords = MONO_BYTES_PER_LINE / 2;
                return (_lineStartCounter + (uint)(mwords * 2)) & 0xFFFFFFu;
            }

            int cycleInLine = (int)(CPU._moira.Clock - _lineStartClock);

            // DE window estimated from the frequency currently programmed; this is enough for the
            // usual case where the VAP is polled during the active display or right at the border.
            bool is60 = Is60AtCycle(DE_START_60);
            int deStart = is60 ? DE_START_60 : DE_START_50;
            int deStop  = is60 ? DE_STOP_60  : DE_STOP_50;

            // TEMP bisect toggle: ASE_VAP_LEGACY restores the old byte-granular (odd-capable) formula.
            if (_vapLegacy)
            {
                int b = (cycleInLine - deStart) / 2;
                int lb = (deStop - deStart) / 2;
                if (b < 0) b = 0; else if (b > lb) b = lb;
                return (_lineStartCounter + (uint)b) & 0xFFFFFFu;
            }

            int lineWords = (deStop - deStart) / 4;   // words fetched across the DE window

            // The shifter fetches one WORD (2 bytes) per memory cycle, i.e. 2 bytes every 4 CPU
            // cycles in low/medium resolution (160 bytes across the 320-cycle DE window, 0.5
            // bytes/cycle). The video-address counter therefore advances in word steps and is
            // ALWAYS EVEN. This is critical: self-stabilising raster code (Spectrum 512 and many
            // intros) reads $FF8209 as a word-granular beam position and uses it directly as a
            // jump offset into a 2-byte-per-slot NOP sled — an odd value lands mid-instruction and
            // crashes with an address error. Before DE the pointer sits at the line-start value;
            // after DE (right border / H-Blank) it freezes at the end-of-line value.
            int words = (cycleInLine - deStart) / 4;
            if (words < 0) words = 0;
            else if (words > lineWords) words = lineWords;

            return (_lineStartCounter + (uint)(words * 2)) & 0xFFFFFFu;
        }
    }
}
