/*
 * 
 * Atari STE BLiTTER (BLock Image Transfer) emulation.
 * 
 * Some parts inspired in Hatari emulator created by Thomas Huth and others.
 * 
 * The BLiTTER provides hardware-accelerated block memory transfers with
 * halftone patterns, 16 logical operations, barrel-shifting (skew), and
 * configurable endmasks. Registers are mapped at $FF8A00-$FF8A3D.
 * 
 * References:
 *   - Atari ST/STE Hardware Reference Manual
 *   - https://info-coach.fr/atari/hardware/STE-HW.php
 * 
 * Make it real, how to install a BLiTTER on yout ST F/FM
 * 👉 https://www.youtube.com/watch?v=mbLMX0BnyQQ
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using System.Runtime.CompilerServices;

using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Emulates the Atari STE BLiTTER chip (BLock Image Transfer).
    /// Supports halftone patterns, 16 logical operations, barrel-shifting (skew),
    /// FXSR/NFSR source read modes, smudge mode, and HOG/shared bus modes.
    /// </summary>
    public static class Blitter
    {
        const uint BASE = 0xFF8A00;

        // Halftone RAM (16 words at $FF8A00-$FF8A1E)
        static readonly ushort[] halftone = new ushort[16];

        // Source registers
        static short srcXInc;      // $FF8A20.w (signed)
        static short srcYInc;      // $FF8A22.w (signed)
        static uint srcAddr;       // $FF8A24-$FF8A27 (24-bit, word-aligned)

        // Endmasks
        static ushort endmask1;    // $FF8A28.w - first word
        static ushort endmask2;    // $FF8A2A.w - middle words
        static ushort endmask3;    // $FF8A2C.w - last word

        // Destination registers
        static short dstXInc;      // $FF8A2E.w (signed)
        static short dstYInc;      // $FF8A30.w (signed)
        static uint dstAddr;       // $FF8A32-$FF8A35 (24-bit, word-aligned)

        // Count registers
        static ushort xCount;      // $FF8A36.w - words per line
        static ushort yCount;      // $FF8A38.w - number of lines

        // Operation registers
        static byte hop;           // $FF8A3A.b - Halftone OPeration (bits 1-0)
        static byte op;            // $FF8A3B.b - Logical OPeration (bits 3-0)

        // Control register ($FF8A3C.b)
        static byte lineNumber;    // bits 3-0 : halftone line number
        static bool smudge;        // bit 5    : smudge mode
        static bool hog;           // bit 6    : HOG mode (blitter takes all bus cycles)
        static bool busy;          // bit 7    : busy flag (set to 1 to start)

        // Skew register ($FF8A3D.b)
        static byte skew;          // bits 3-0 : barrel-shift amount (0-15)
        static bool nfsr;          // bit 6    : No Final Source Read
        static bool fxsr;          // bit 7    : Force eXtra Source Read

        // Internal state (persistent across blits, like Hatari's BlitterVars)
        static uint srcBuffer;     // 32-bit barrel-shifter buffer
        static uint xCountReset;   // saved x_count value (set when xCount register is written)

        // --- Bus timing and arbitration ------------------------------------------------------
        //
        // The blitter is a second bus master, not a function call: it takes the bus away from the
        // 68000, moves a word every 4 cycles and — unless it is in HOG mode — hands the bus back
        // every so often so the CPU can run. Running a whole blit inside the register write that
        // starts it (which is what this did) gets the *result* right and the *timing* completely
        // wrong, and there is a whole class of code that only cares about the timing: raster
        // effects that blit into the palette or the video registers, loaders that overlap a blit
        // with CPU work, and anything that measures the chip. The symptom is not a wrong picture
        // but a picture with no blitter in it at all — every write the blit makes lands on one
        // single CPU cycle, so the mid-line palette replay collapses them into one position.
        // Numbers from Hatari's blitter.c, measured on real hardware.

        /// <summary>Cycles one blitter bus access costs (a word read or a word write).</summary>
        const int BusAccessCycles = 4;

        /// <summary>
        /// Cycles the bus arbitration takes, in either direction — the blitter waits this long
        /// after asking for the bus, and again when it gives it back.
        /// </summary>
        const int BusArbitrationCycles = 4;

        /// <summary>Bus accesses the blitter makes before yielding, in shared (non-HOG) mode.</summary>
        const int SharedBusBlitterAccesses = 64;

        /// <summary>Bus accesses the CPU gets before the blitter takes the bus back.</summary>
        const int SharedBusCpuAccesses = 64;

        static int blitterBusCredit;   // accesses left in the current blitter burst
        static int cpuBusCredit;       // CPU accesses still owed before the blitter resumes
        static bool busGranted;        // the arbitration cost of this burst has been paid
        static bool busRequestPending; // the blitter has asked for the bus, latency not yet over
        static long busRequestClock;   // when it asked

        /// <summary>
        /// The blitter's own clock. It is a second bus master with a clock of its own, and the
        /// two run <b>in parallel</b>: the blitter takes the bus as soon as the arbitration is
        /// over, while the 68000 goes on with whatever internal cycles its current instruction
        /// still has to do and only stalls at its next bus access.
        /// <para>
        /// This has to be tracked separately because Moira can only be stopped between
        /// instructions. When the CPU's quota runs out in the middle of a <c>divs</c> (142
        /// cycles, one single bus access at the front of it), ASE has already run the whole
        /// instruction by the time it looks at the blitter again — and taking the Moira clock as
        /// the start of the burst charges those cycles to the blitter, which on real hardware
        /// they never were. The burst then lands up to an instruction late, every time.
        /// </para>
        /// </summary>
        static long blitClock;

        /// <summary>
        /// The cycle the bus comes back to the 68000, or 0 while the CPU has it. The blitter does
        /// not stop the CPU, it stops its <b>memory</b>: a 68000 that loses the bus goes on with
        /// the internal cycles of the instruction it is in and only stalls at its next access.
        /// That is what this models -- <see cref="Run"/> leaves the CPU's clock exactly where it
        /// was and the next CPU bus access waits here instead.
        /// <para>
        /// Charging the CPU the whole burst (which is what moving its clock to the end of the
        /// burst did) costs it the internal cycles it should have overlapped -- 138 of the 142 of
        /// a <c>divs</c> -- so every burst whose quota ran out inside a long instruction lands
        /// that much late.
        /// </para>
        /// </summary>
        static long busFreeAt;

        /// <summary>
        /// CPU bus accesses made after the blitter already had the bus but before the emulation
        /// loop got round to running the burst. Moira can only be stopped between instructions,
        /// so an access that on the machine would have been held off until the burst ended has
        /// often already happened by then; <see cref="ReleaseBus"/> charges the CPU the wait it
        /// should have taken and counts them against the new quota, which is where they belong.
        /// </summary>
        static int earlyCpuAccesses;
        static long earlyAccessClock;

        /// <summary>
        /// A CPU access slipped into the arbitration window, so the blitter lost one access of
        /// this burst -- but <b>not</b> the time. MiSTer's RTL (stBlitter.sv) has a single
        /// counter that counts every bus cycle from the moment busy is set, so a slot the CPU
        /// took is a slot gone: the blitter still holds the bus for its 64 of them and simply
        /// makes 63 accesses in that time. Shortening the burst by the missing access instead
        /// leaves every burst period 4 cycles short -- which no access count shows, and which
        /// walks the whole pattern down the screen at the wrong angle.
        /// </summary>
        static bool slotStolen;

        // Per-line state of the blit in progress. It used to live in Execute()'s locals, which
        // was fine while a blit ran to completion in one call and is exactly what has to survive
        // now that it is stepped.
        static bool nfsrState;
        static bool haveFxsr;

        // The last word the blitter moved over the bus, read (source or destination) or written:
        // what the extra shift-and-fetch of an NFSR line's last word takes in (Hatari's bus_word).
        // Not per blit -- only a reset clears it.
        static ushort lastBusWord;

        /// <summary>
        /// True while the blitter owns the bus, which is when the 68000 cannot reach memory: the
        /// emulation loop runs the blitter instead of the CPU (see ASEMain.RunCpuUntil). Asking
        /// for the bus does not take it — the 68000 keeps it for the arbitration latency first,
        /// which is what lets the access below slip through.
        /// <para>
        /// "Cannot reach memory" is not "is stopped": a 68000 goes on with the internal cycles of
        /// the instruction it is in and only stalls at its next bus access. That is what
        /// <see cref="blitClock"/> is for — the emulation loop cannot interrupt Moira mid
        /// instruction, so the parallelism is recovered by running the blitter on a clock of its
        /// own and taking the later of the two at the end.
        /// </para>
        /// </summary>
        public static bool HoldsBus =>
            busy && cpuBusCredit == 0 && CPU._moira.Clock >= busRequestClock + 2 * BusArbitrationCycles;

        /// <summary>
        /// Counts one CPU bus access, from the CPU-facing accessors in <see cref="Memory"/>.
        /// In shared mode the blitter gives the CPU a fixed number of <b>accesses</b>, not a fixed
        /// number of cycles, so this is what times the hand-back — and it is what makes a CPU
        /// busy with long non-memory instructions (a string of <c>divs</c>, say) hand the bus
        /// back far less often than one running a stream of <c>move.w</c>.
        /// <para>
        /// The second half is Hatari's <c>Blitter_HOG_CPU_BusCountError</c>, and it is not a
        /// rounding detail — it is measurable. Asking for the bus does
        /// not take it: the 68000 keeps it for the 4-cycle arbitration latency, and an access it
        /// slips into that window is counted by the blitter as one of <i>its</i> own, so the burst
        /// comes out at <b>63</b> instead of 64. MiSTer's RTL (stBlitter.sv, Jorge Cwik) shows
        /// where that comes from: there is a single 7-bit counter that counts <i>every</i> bus
        /// cycle while busy is set — "BLITTER Buglet: Starts counting as soon as BUSY is set" —
        /// with bit 6 deciding who owns the bus, so a CPU access made before the blitter has
        /// taken it simply eats one of the blitter's 64 slots.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void NoteCpuBusAccess(uint addr, bool write)
        {
            // The blitter held the bus until busFreeAt: an access before that waits for it, like
            // any other wait state. This is the only thing a burst costs the 68000 -- the cycles
            // it spends inside an instruction it keeps.
            if (busFreeAt != 0)
            {
                if (CPU._moira.Clock < busFreeAt) CPU._moira.Clock = busFreeAt;
                else busFreeAt = 0;
            }

            if (cpuBusCredit > 0)
            {
                if (--cpuBusCredit == 0)
                {
                    busRequestClock = CPU._moira.Clock;
                    busRequestPending = true;
                }
                return;
            }

            if (!busy)
                return;

            if (busRequestPending)
            {
                busRequestPending = false;

                // Slipped into the arbitration window: the blitter counts it as one of its own
                // (Hatari's Blitter_HOG_CPU_BusCountError, and see the summary above).
                if (CPU._moira.Clock - busRequestClock <= 2 * BusArbitrationCycles)
                {
                    blitterBusCredit--;
                    slotStolen = true;
                    return;
                }
            }

            // The write half of a TAS whose read got the bus is still the CPU's. TAS is the 68000's
            // one indivisible read-modify-write: AS stays asserted from the read to the write, and
            // no master can take the bus in between (stBlitter.sv grants only with AS negated,
            // Hatari holds the start back while cpu_bus_rmw). With no early access booked yet, the
            // read just before was not one either, so the write is not an early access, and the
            // burst starts once it is over. Running the burst here, as for any other register
            // access, is what broke the VDI of TOS and EmuTOS: its restart loop `tas.b (a5) / nop / bmi.b *-4`
            // read the control byte, the burst moved the halftone line on, and the write put the
            // line read before it back -- every desktop fill drawn after such a turn had its
            // pattern a few lines out of phase.
            if (write && earlyCpuAccesses == 0 && IsTas(CPU._moira.IRD))
            {
                // Moira calls back halfway through the 4-cycle access.
                long lockEnd = CPU._moira.Clock + BusAccessCycles / 2;
                long grant = busRequestClock + 2 * BusArbitrationCycles + (slotStolen ? BusAccessCycles : 0);
                if (!busGranted && lockEnd > grant) busRequestClock += lockEnd - grant;
                return;
            }

            // The window has closed and the bus is the blitter's. On the machine this access
            // simply would not happen yet; here it already has, so it is booked and paid for
            // when the burst ends (see ReleaseBus).
            if (earlyCpuAccesses++ == 0) earlyAccessClock = CPU._moira.Clock;

            // Booking the time is enough for an access to RAM the blit does not touch, but not
            // for one to the blitter itself: a register written now would change the blit before
            // it has run. The restart idiom does exactly that — `move.w d1,(a2)` (y count) then
            // `move.b d6,(a6)` (busy + HOG), over and over, common in sprite routines — and the y
            // count meant for the next chunk would be consumed by the one still waiting, so every
            // plane would lose its last chunk. The access waits for the bus
            // here, as on the machine: the burst runs first (the whole blit, in HOG mode).
            if ((addr & 0xFFFFC0) == BASE)
                Run(long.MaxValue);
        }

        /// <summary>
        /// The opcode is a TAS on memory: <c>0100 1010 11 mmm rrr</c> with a data-alterable
        /// memory mode. Moira keeps the instruction in IRD until the prefetch at its end, so
        /// this holds for both of its bus accesses. Dn makes no access, and the other encodings
        /// (An, PC-relative, immediate -- $4AFC is ILLEGAL) raise an exception whose stack writes
        /// must not pass for the write half.
        /// </summary>
        static bool IsTas(ushort ird)
        {
            if ((ird & 0xFFC0) != 0x4AC0) return false;
            int mode = (ird >> 3) & 7;
            return mode >= 2 && (mode < 7 || (ird & 7) <= 1);
        }

        public static void Reset()
        {
            Array.Clear(halftone);
            srcXInc = 2;
            srcYInc = 2;
            srcAddr = 0;
            endmask1 = 0xFFFF;
            endmask2 = 0xFFFF;
            endmask3 = 0xFFFF;
            dstXInc = 2;
            dstYInc = 2;
            dstAddr = 0;
            xCount = 0;
            yCount = 0;
            hop = 3;
            op = 3;
            lineNumber = 0;
            smudge = false;
            hog = false;
            busy = false;
            skew = 0;
            nfsr = false;
            fxsr = false;
            srcBuffer = 0;
            xCountReset = 0;

            blitterBusCredit = 0;
            cpuBusCredit = 0;
            busGranted = false;
            busRequestPending = false;
            busRequestClock = 0;
            blitClock = 0;
            busFreeAt = 0;
            earlyCpuAccesses = 0;
            earlyAccessClock = 0;
            slotStolen = false;
            nfsrState = false;
            haveFxsr = false;
            lastBusWord = 0;
        }

        // Snapshot

        public static void SaveState(Snapshot.Writer w)
        {
            for (int i = 0; i < 16; i++)
                w.U16(halftone[i]);

            w.I16(srcXInc);
            w.I16(srcYInc);
            w.U32(srcAddr);
            w.U16(endmask1);
            w.U16(endmask2);
            w.U16(endmask3);
            w.I16(dstXInc);
            w.I16(dstYInc);
            w.U32(dstAddr);
            w.U16(xCount);
            w.U16(yCount);
            w.U8(hop);
            w.U8(op);
            w.U8(lineNumber);
            w.Bool(smudge);
            w.Bool(hog);
            w.Bool(busy);
            w.U8(skew);
            w.Bool(nfsr);
            w.Bool(fxsr);
            w.U32(srcBuffer);
            w.U32(xCountReset);

            // Appended: the state of a blit caught in flight. A blit is no longer instantaneous,
            // so a snapshot can land in the middle of one and has to be able to carry it on.
            // Older snapshots stop above and restore with an idle blitter.
            w.I32(blitterBusCredit);
            w.I32(cpuBusCredit);
            w.Bool(busGranted);
            w.Bool(busRequestPending);
            w.I64(busRequestClock);
            w.Bool(nfsrState);
            w.Bool(haveFxsr);
            w.U16(lastBusWord);

            // Appended again: the blitter's own clock (see the field). A snapshot taken mid-blit
            // without it would resume the burst from the CPU's clock, which is the very thing
            // this separates.
            w.I64(blitClock);

            // Appended again: the bus hand-back (see busFreeAt). A snapshot without it restores a
            // machine whose next CPU access does not wait for a burst that was in flight.
            w.I64(busFreeAt);
            w.I32(earlyCpuAccesses);
            w.I64(earlyAccessClock);
            w.Bool(slotStolen);
        }

        public static void LoadState(Snapshot.Reader r)
        {
            for (int i = 0; i < 16; i++)
                halftone[i] = r.U16();

            srcXInc = r.I16();
            srcYInc = r.I16();
            srcAddr = r.U32();
            endmask1 = r.U16();
            endmask2 = r.U16();
            endmask3 = r.U16();
            dstXInc = r.I16();
            dstYInc = r.I16();
            dstAddr = r.U32();
            xCount = r.U16();
            yCount = r.U16();
            hop = r.U8();
            op = r.U8();
            lineNumber = r.U8();
            smudge = r.Bool();
            hog = r.Bool();
            busy = r.Bool();
            skew = r.U8();
            nfsr = r.Bool();
            fxsr = r.Bool();
            srcBuffer = r.U32();
            xCountReset = r.U32();

            // In-flight blit state (see SaveState). Absent from older snapshots, which restore
            // with the blitter idle -- and if one of those was taken mid-blit, busy would be set
            // with no bus credit behind it, so the blit is dropped rather than resumed wrong.
            if (r.Remaining >= 21)
            {
                blitterBusCredit = r.I32();
                cpuBusCredit = r.I32();
                busGranted = r.Bool();
                busRequestPending = r.Bool();
                busRequestClock = r.I64();
                nfsrState = r.Bool();
                haveFxsr = r.Bool();
                lastBusWord = r.U16();

                // The blitter clock was appended later still; a snapshot without it restarts the
                // burst from the CPU clock, which is only ever a few cycles off.
                blitClock = r.Remaining >= 8 ? r.I64() : CPU._moira.Clock;

                if (r.Remaining >= 21)
                {
                    busFreeAt = r.I64();
                    earlyCpuAccesses = r.I32();
                    earlyAccessClock = r.I64();
                    slotStolen = r.Bool();
                }
                else
                {
                    busFreeAt = 0;
                    earlyCpuAccesses = 0;
                    earlyAccessClock = 0;
                    slotStolen = false;
                }
            }
            else
            {
                busy = false;
                blitterBusCredit = 0;
                cpuBusCredit = 0;
                busGranted = false;
                busRequestPending = false;
                busRequestClock = 0;
                blitClock = 0;
                busFreeAt = 0;
                earlyCpuAccesses = 0;
                earlyAccessClock = 0;
                slotStolen = false;
                nfsrState = false;
                haveFxsr = false;
                lastBusWord = 0;
            }

            // The INT line is the busy bit on GPIP3. The MFP's section, restored first, has it
            // already -- except in snapshots older than the wiring, which saved the pull-up.
            ASEMain._mfp.SetBlitterLineLevel(busy);
        }

        /// <summary>
        /// Reads a byte from a blitter register ($FF8A00-$FF8A3D).
        /// </summary>
        public static byte ReadByte(uint addr)
        {
            uint offset = addr - BASE;

            // Halftone RAM ($FF8A00-$FF8A1F)
            if (offset <= 0x1F)
            {
                int idx = (int)(offset >> 1);
                return (offset & 1) == 0
                    ? (byte)(halftone[idx] >> 8)
                    : (byte)(halftone[idx] & 0xFF);
            }

            return offset switch
            {
                0x20 => (byte)((ushort)srcXInc >> 8),
                0x21 => (byte)(srcXInc & 0xFF),
                0x22 => (byte)((ushort)srcYInc >> 8),
                0x23 => (byte)(srcYInc & 0xFF),
                0x24 => 0,                                      // bits 31-24 unused
                0x25 => (byte)((srcAddr >> 16) & 0xFF),
                0x26 => (byte)((srcAddr >> 8) & 0xFF),
                0x27 => (byte)(srcAddr & 0xFE),                 // bit 0 always 0
                0x28 => (byte)(endmask1 >> 8),
                0x29 => (byte)(endmask1 & 0xFF),
                0x2A => (byte)(endmask2 >> 8),
                0x2B => (byte)(endmask2 & 0xFF),
                0x2C => (byte)(endmask3 >> 8),
                0x2D => (byte)(endmask3 & 0xFF),
                0x2E => (byte)((ushort)dstXInc >> 8),
                0x2F => (byte)(dstXInc & 0xFF),
                0x30 => (byte)((ushort)dstYInc >> 8),
                0x31 => (byte)(dstYInc & 0xFF),
                0x32 => 0,                                      // bits 31-24 unused
                0x33 => (byte)((dstAddr >> 16) & 0xFF),
                0x34 => (byte)((dstAddr >> 8) & 0xFF),
                0x35 => (byte)(dstAddr & 0xFE),                 // bit 0 always 0
                0x36 => (byte)(xCount >> 8),
                0x37 => (byte)(xCount & 0xFF),
                0x38 => (byte)(yCount >> 8),
                0x39 => (byte)(yCount & 0xFF),
                0x3A => hop,
                0x3B => op,
                0x3C => PackControl(),
                0x3D => PackSkew(),
                _ => 0xFF
            };
        }

        /// <summary>
        /// Writes a byte to a blitter register. Writing bit 7 of $FF8A3C starts the blit.
        /// </summary>
        public static void WriteByte(uint addr, byte v)
        {
            uint offset = addr - BASE;

            // Halftone RAM
            if (offset <= 0x1F)
            {
                int idx = (int)(offset >> 1);
                if ((offset & 1) == 0)
                    halftone[idx] = (ushort)((v << 8) | (halftone[idx] & 0x00FF));
                else
                    halftone[idx] = (ushort)((halftone[idx] & 0xFF00) | v);
                return;
            }

            switch (offset)
            {
                case 0x20: srcXInc = (short)((v << 8) | ((ushort)srcXInc & 0xFF)); break;
                case 0x21: srcXInc = (short)(((ushort)srcXInc & 0xFF00) | v); break;
                case 0x22: srcYInc = (short)((v << 8) | ((ushort)srcYInc & 0xFF)); break;
                case 0x23: srcYInc = (short)(((ushort)srcYInc & 0xFF00) | v); break;
                case 0x24: break; // bits 31-24 ignored
                case 0x25: srcAddr = (srcAddr & 0x0000FFFF) | ((uint)v << 16); break;
                case 0x26: srcAddr = (srcAddr & 0x00FF00FF) | ((uint)v << 8); break;
                case 0x27: srcAddr = (srcAddr & 0x00FFFF00) | (uint)(v & 0xFE); break;
                case 0x28: endmask1 = (ushort)((v << 8) | (endmask1 & 0x00FF)); break;
                case 0x29: endmask1 = (ushort)((endmask1 & 0xFF00) | v); break;
                case 0x2A: endmask2 = (ushort)((v << 8) | (endmask2 & 0x00FF)); break;
                case 0x2B: endmask2 = (ushort)((endmask2 & 0xFF00) | v); break;
                case 0x2C: endmask3 = (ushort)((v << 8) | (endmask3 & 0x00FF)); break;
                case 0x2D: endmask3 = (ushort)((endmask3 & 0xFF00) | v); break;
                case 0x2E: dstXInc = (short)((v << 8) | ((ushort)dstXInc & 0xFF)); break;
                case 0x2F: dstXInc = (short)(((ushort)dstXInc & 0xFF00) | v); break;
                case 0x30: dstYInc = (short)((v << 8) | ((ushort)dstYInc & 0xFF)); break;
                case 0x31: dstYInc = (short)(((ushort)dstYInc & 0xFF00) | v); break;
                case 0x32: break; // bits 31-24 ignored
                case 0x33: dstAddr = (dstAddr & 0x0000FFFF) | ((uint)v << 16); break;
                case 0x34: dstAddr = (dstAddr & 0x00FF00FF) | ((uint)v << 8); break;
                case 0x35: dstAddr = (dstAddr & 0x00FFFF00) | (uint)(v & 0xFE); break;
                case 0x36: xCount = (ushort)((v << 8) | (xCount & 0x00FF)); xCountReset = xCount == 0 ? 65536u : xCount; break;
                case 0x37: xCount = (ushort)((xCount & 0xFF00) | v); xCountReset = xCount == 0 ? 65536u : xCount; break;
                case 0x38: yCount = (ushort)((v << 8) | (yCount & 0x00FF)); break;
                case 0x39: yCount = (ushort)((yCount & 0xFF00) | v); break;
                case 0x3A: hop = (byte)(v & 0x03); break;
                case 0x3B: op = (byte)(v & 0x0F); break;
                case 0x3C:
                    WriteControl(v);
                    break;
                case 0x3D:
                    UnpackSkew(v);
                    break;
            }
        }

        /// <summary>
        /// Reads a word from blitter registers.
        /// </summary>
        public static ushort ReadWord(uint addr)
        {
            return (ushort)((ReadByte(addr) << 8) | ReadByte(addr + 1));
        }

        /// <summary>
        /// Writes a word to blitter registers. The control + skew word at $FF8A3C
        /// is handled atomically so that both bytes are set before starting the blit.
        /// </summary>
        public static void WriteWord(uint addr, ushort v)
        {
            uint offset = addr - BASE;

            // Control + Skew word at $FF8A3C: set both bytes before starting
            if (offset == 0x3C)
            {
                UnpackSkew((byte)(v & 0xFF));
                WriteControl((byte)(v >> 8));
                return;
            }

            WriteByte(addr, (byte)(v >> 8));
            WriteByte(addr + 1, (byte)(v & 0xFF));
        }

        /// <summary>
        /// A write to the control register ($FF8A3C), from a byte or a word access. The blitter's
        /// INT line follows busy, but busy reads as deasserted for as long as the register is
        /// being written (stBlitter.sv: <c>busy = rBusy &amp; !ctrlWr</c>, "this will even toggle
        /// INTn if Blitter still busy"): a write to a running blitter drops the line and raises
        /// it again, so the MFP sees both edges and a "blit done" interrupt is requested on
        /// whichever its AER selects -- the restart loop of a program that also enabled the
        /// interrupt takes one per turn on the machine.
        /// </summary>
        static void WriteControl(byte v)
        {
            bool wasBusy = busy;
            if (wasBusy) ASEMain._mfp.SetGPIOBit(3, false);

            UnpackControl(v);
            ControlWritten(wasBusy);
            UpdateIrqLine();
        }

        /// <summary>
        /// Drives the blitter's INT output onto MFP GPIP3: high while busy, low when idle
        /// (MiSTer's <c>INTn = busy</c>, Hatari's Blitter_Set_Line_IRQ_MFP). The 1-&gt;0 edge at the
        /// end of a blit is the MFP's "blit done" interrupt with the default AER. Hatari raises
        /// the line when the first burst starts, MiSTer when busy is written; this follows the
        /// RTL. The MFP only acts on a change, so calling it with the level unchanged is free.
        /// </summary>
        static void UpdateIrqLine() => ASEMain._mfp.SetGPIOBit(3, busy);

        // --- Control / Skew register packing ---

        static byte PackControl()
        {
            byte val = (byte)(lineNumber & 0x0F);
            if (smudge) val |= 0x20;
            if (hog)    val |= 0x40;
            if (busy)   val |= 0x80;
            return val;
        }

        static void UnpackControl(byte v)
        {
            lineNumber = (byte)(v & 0x0F);
            smudge = (v & 0x20) != 0;
            hog    = (v & 0x40) != 0;
            busy   = (v & 0x80) != 0;
        }

        static byte PackSkew()
        {
            byte val = (byte)(skew & 0x0F);
            if (nfsr) val |= 0x40;
            if (fxsr) val |= 0x80;
            return val;
        }

        static void UnpackSkew(byte v)
        {
            skew = (byte)(v & 0x0F);
            nfsr = (v & 0x40) != 0;
            fxsr = (v & 0x80) != 0;
        }

        // --- LOP need_src / need_dst tables ---
        // Indexed by op (0-15): whether the LOP formula uses source or destination
        static readonly bool[] LopNeedSrc = [
            false, true, true, true, true, false, true, true,
            true, true, false, true, true, true, true, false
        ];
        static readonly bool[] LopNeedDst = [
            false, true, true, false, true, true, true, true,
            true, true, true, true, false, true, true, false
        ];

        /// <summary>
        /// Acts on a write to the control register ($FF8A3C), given the busy bit as it stood
        /// <b>before</b> the write. Busy is a flip-flop: only the 0-&gt;1 edge arms a blit and only
        /// the 1-&gt;0 edge aborts one. Writing a 1 into one that is already set is a
        /// <b>restart</b>, not a new blit: the blit carries on where it is, but the blitter asks
        /// for the bus again at once instead of waiting for the CPU to spend its 64 accesses
        /// (see <see cref="RequestBus"/>). That is the normal path, not a corner case — the
        /// standard way to wait for a shared-mode blit is to keep writing the bit back, and TOS'
        /// own VDI does exactly that:
        /// <code>tas.b (a5) / nop / bmi.b *-4</code>
        /// with a5 = $FF8A3C (ten copies of that loop in TOS 1.62, at $E0B4A2, $E0AB64, $E104C8
        /// and seven more; four in EmuTOS), where TAS is a read-modify-write that puts bit 7
        /// back on <i>every turn</i> of the loop. The loop is how the blitter gets nearly the
        /// whole bus without HOG mode: the CPU makes a handful of accesses per turn, not 64.
        /// <para>
        /// While the whole blit ran inside the register write this was invisible: busy was
        /// already clear by the time the loop first looked. Now that the blit is stepped,
        /// calling <see cref="StartBlit"/> there reset <see cref="nfsrState"/> and
        /// <see cref="haveFxsr"/> in the middle of a line — and an
        /// NFSR blit whose state was cleared between xCount 2 and 1 then made the source read it
        /// was meant to skip, so srcAddr advanced one word too far and every line after it was
        /// fetched from the wrong place. That is what shredded the GEM menus and the Atari logo.
        /// The restart does reset the arbitration on every turn, and that is right now that a
        /// register access arriving after the blitter has the bus runs the burst first (see
        /// <see cref="NoteCpuBusAccess"/>): by the time the write lands, the burst it would have
        /// cut short is over and no CPU access is left booked against it.
        /// </para>
        /// </summary>
        static void ControlWritten(bool wasBusy)
        {
            // Busy written with no lines left: the transfer is already complete, and the chip
            // drops busy and HOG on the spot (see BlitComplete). This is what the VDI's restart
            // loop meets on its last turn -- TAS writes bit 7 back over a finished blit -- and
            // it must read as idle at once, not as a blit armed with nothing to do.
            if (busy && yCount == 0)
                BlitComplete();

            if (busy)
            {
                if (!wasBusy) StartBlit();
                else if (!busGranted) RequestBus();
            }
            else if (wasBusy)
            {
                FinishBlit();
            }
        }

        /// <summary>
        /// Arms a blit: the write to $FF8A3C that sets the busy bit does not do the work, it asks
        /// for the bus. The blit itself is carried out by <see cref="Run"/>, a word at a time.
        /// </summary>
        static void StartBlit()
        {
            RequestBus();

            nfsrState = false;
            haveFxsr = false;
        }

        /// <summary>
        /// Asks for the bus from the write to $FF8A3C that is going on now, with a fresh burst
        /// quota: both the start of a blit and the restart of a running one (busy written while
        /// set). The restart leaves the blit's own state alone -- it continues from the word it
        /// stopped at -- and only cuts the CPU's quota short. stBlitter.sv shows why: BUSY reads
        /// as deasserted while the control register is being written, and its 64-cycle bus
        /// counter clears whenever BUSY is low, so every control write starts the count from zero
        /// with the blitter wanting the bus; Hatari puts the blitter back into PRE_START on any
        /// such write ("the cpu can restart the blitter immediately, without waiting for 64 cpu
        /// bus accesses") and only flushes the word state when it was stopped.
        /// </summary>
        static void RequestBus()
        {
            blitterBusCredit = hog ? int.MaxValue : SharedBusBlitterAccesses;
            cpuBusCredit = 0;
            busGranted = false;

            // A bus *request* like any other: the write to $FF8A3C completes, the CPU keeps the
            // bus for the arbitration latency, and what it does in that window decides whether
            // this burst gets 64 accesses or 63 (see NoteCpuBusAccess).
            busRequestClock = CPU._moira.Clock;
            busRequestPending = true;
            earlyCpuAccesses = 0;
            slotStolen = false;
        }

        /// <summary>A blit is in progress (the busy bit of $FF8A3C is set).</summary>
        public static bool Busy => busy;

        /// <summary>How many bus accesses the CPU still owes before the blitter takes over.</summary>
        public static int CpuBusCredit => cpuBusCredit;

        /// <summary>
        /// Hands the bus over although the CPU has not used its quota. A 68000 sitting in
        /// <c>STOP</c> — or halted by a double fault — makes no bus accesses at all, so the quota
        /// would never run out and a blit started before it would never finish, hanging whatever
        /// waits on the busy bit. On real hardware an idle CPU simply releases the bus and the
        /// blitter carries on, which is what the emulation loop asks for here when a CPU slice
        /// goes by without a single bus cycle.
        /// </summary>
        public static void GrantBusToIdleCpu()
        {
            if (!busy || cpuBusCredit == 0)
                return;

            cpuBusCredit = 0;
            busRequestClock = CPU._moira.Clock;
            busRequestPending = false;
            earlyCpuAccesses = 0;
        }

        /// <summary>
        /// Ends the blit and gives the bus back to the CPU.
        /// </summary>
        /// <returns>The cycles the CPU has to be pushed back by (see <see cref="ReleaseBus"/>).</returns>
        static long FinishBlit()
        {
            busy = false;
            UpdateIrqLine();
            long stall = 0;

            if (busGranted)
            {
                blitClock += BusArbitrationCycles;
                stall = ReleaseBus();
                busGranted = false;
            }

            cpuBusCredit = 0;
            busRequestPending = false;
            earlyCpuAccesses = 0;
            return stall;
        }

        /// <summary>
        /// Hands the bus back at <see cref="blitClock"/>. From here the 68000's next access waits
        /// for it (<see cref="busFreeAt"/>) -- and an access it has <i>already</i> made while the
        /// blitter had the bus (see <see cref="earlyCpuAccesses"/>) is charged the wait the
        /// machine would have made it take, which is the whole of what a burst costs the CPU.
        /// Everything it did between that access and here, it keeps: those are internal cycles,
        /// and on real hardware they run alongside the blit.
        /// </summary>
        /// <returns>The cycles the CPU has to be pushed back by.</returns>
        static long ReleaseBus()
        {
            busFreeAt = blitClock;

            if (earlyCpuAccesses == 0)
                return 0;

            long stall = blitClock - earlyAccessClock;
            return stall > 0 ? stall : 0;
        }

        /// <summary>
        /// Carries the blit forward while the blitter holds the bus, stopping at
        /// <paramref name="targetClock"/>. The emulation loop calls this instead of running the
        /// CPU (see ASEMain.RunCpuUntil), which is what makes the blitter a bus master rather
        /// than a subroutine: every access advances <see cref="blitClock"/>, so each write lands
        /// on the cycle it really lands on and the rest of the machine -- the video's per-line
        /// model above all -- sees the time the blit takes.
        /// <para>
        /// The clock limit is not a detail: a long blit can run for hundreds of scanlines, and
        /// stopping at the caller's target is what spreads it over the lines it really covers
        /// instead of charging the whole of it to the line that started it.
        /// </para>
        /// <para>
        /// The two masters run <b>in parallel</b>, and that is what the blitter's own clock is
        /// for. The burst starts where the arbitration ends, whatever the 68000 was in the middle
        /// of, and the machine's clock at the end is the later of the two -- the CPU's internal
        /// cycles cost the blitter nothing, which is the same thing Hatari gets by ignoring the
        /// CPU cycles that already ran alongside a blit.
        /// </para>
        /// </summary>
        /// <returns>true when the blitter made at least one bus access.</returns>
        public static bool Run(long targetClock)
        {
            if (!busy || cpuBusCredit > 0)
                return false;

            // Where the CPU has got to. It keeps this: the blitter is about to run over the same
            // stretch of time, not after it.
            long cpuClock = CPU._moira.Clock;

            // Armed with nothing to do: the busy bit still has to drop.
            if (yCount == 0 || xCountReset == 0)
            {
                // y count rewritten to 0 under a paused blit: complete, as at its last word.
                if (yCount == 0) BlitComplete();
                CPU._moira.Clock = cpuClock + FinishBlit();
                return false;
            }

            if (!busGranted)
            {
                // The blitter has the bus at request + latency + arbitration, and it takes it
                // there even if the 68000 is halfway through a long instruction -- that is what a
                // second bus master is. Do NOT start from the Moira clock: Moira stops only
                // between instructions, so by the time the emulation loop looks at the blitter
                // the CPU has already run the rest of the instruction, and starting the burst
                // there charges the blitter for cycles the CPU spent on its own.
                //
                blitClock = busRequestClock + 2 * BusArbitrationCycles;

                // The slot the CPU took is the blitter's first one: it holds the bus for the same
                // 64 slots either way and just starts one access later (see slotStolen).
                if (slotStolen) { blitClock += BusAccessCycles; slotStolen = false; }

                busGranted = true;
                busRequestPending = false;   // the window has closed
            }

            // Never behind the start of the scanline being rendered. The palette and video writes
            // below are stamped with their cycle inside the line, and a write stamped before the
            // line began would get a negative one, which the renderer's forward-only replay cannot
            // place. It takes the blitter running a whole instruction behind a CPU that has just
            // crossed the line boundary, so the few cycles this skips are an exceptional case.
            if (blitClock < VideoTiming.LineStartClock) blitClock = VideoTiming.LineStartClock;

            Memory mem = ASEMain._mem;
            bool accessed = false;

            while (busy && blitterBusCredit > 0 && blitClock < targetClock)
            {
                StepWord(mem);
                accessed = true;
            }

            if (!busy)
            {
                CPU._moira.Clock = cpuClock + FinishBlit();
                return accessed;
            }

            long stall = 0;

            // Shared mode: the burst is spent, so the bus goes back to the CPU for its own quota
            // of accesses. In HOG mode the credit never runs out and the CPU does not get a look
            // in until the blit is over.
            if (blitterBusCredit <= 0)
            {
                blitClock += BusArbitrationCycles;
                busGranted = false;
                blitterBusCredit = hog ? int.MaxValue : SharedBusBlitterAccesses;
                cpuBusCredit = hog ? 0 : SharedBusCpuAccesses;

                stall = ReleaseBus();

                // Accesses the CPU has already made against this new quota (see
                // earlyCpuAccesses): on the machine they happen here, as its first ones.
                if (earlyCpuAccesses > 0)
                {
                    cpuBusCredit -= earlyCpuAccesses;
                    earlyCpuAccesses = 0;

                    if (cpuBusCredit <= 0)
                    {
                        cpuBusCredit = 0;
                        busRequestClock = cpuClock + stall;
                        busRequestPending = true;
                    }
                }
            }

            // HOG mode is the exception: the blitter keeps the bus until the blit ends, so there
            // is nothing for the 68000 to overlap beyond the instruction it is in -- and a HOG
            // blit can run for thousands of cycles, which the timers driven off this clock have
            // to see go by. Its clock follows the blitter's there, as it always did.
            if (hog && busGranted && blitClock > cpuClock)
            {
                CPU._moira.Clock = blitClock;

                // Paid, and paid once. The CPU has just been put ON the blitter's clock, so the
                // accesses it made before the burst started are long behind it and there is
                // nothing left to charge. Leaving them booked charged the blit a SECOND time
                // when it ended: nothing releases the bus during a HOG blit, so ReleaseBus'
                // stall (blitClock - earlyAccessClock) spans the *whole* blit, and the CPU was
                // pushed that far past a clock that had already followed the blitter to the end.
                // Every HOG blit came out exactly twice as long as it is -- which is what a test
                // measuring the blitter against a move.l/dbra copy loop reported as "the same
                // speed as the CPU" where the machine gives about twice it.
                earlyCpuAccesses = 0;
                return accessed;
            }

            // The CPU goes back exactly where it was: BusAccess moved the machine's clock onto
            // the blitter's to stamp each write, and the 68000 neither lost nor gained those
            // cycles -- it only waits at its next bus access (busFreeAt), plus whatever the
            // accesses it had already made owe (stall).
            CPU._moira.Clock = cpuClock + stall;
            return accessed;
        }

        /// <summary>
        /// One blitter bus access: 4 cycles of bus, and one off the burst's quota. The Moira clock
        /// is moved onto the blitter's for the duration of the access -- it can go <i>backwards</i>
        /// while the CPU is ahead -- because the memory write that follows is stamped with it, and
        /// that stamp is what puts a palette write at its horizontal position on the line.
        /// <see cref="Run"/> puts the machine's clock back at the end.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void BusAccess()
        {
            blitClock += BusAccessCycles;
            CPU._moira.Clock = blitClock;
            blitterBusCredit--;
        }

        /// <summary>
        /// Processes one word of the blit.
        /// Faithfully follows Hatari's Blitter_Step + Blitter_ProcessWord model:
        ///  - need_src gates ALL source reads (FXSR + normal) AND source address updates
        ///  - need_dst determines whether destination is read (forced true when mask != 0xFFFF)
        ///  - NFSR state is reset at start of each line, set when xCount reaches 2
        ///  - FXSR flag is latched per-line at the first word
        ///  - Special weird case: xCount=1 + NFSR does SourceShift+Fetch(busWord) before and after write,
        ///    busWord being the last word on the bus (source read, destination read or write)
        ///  - Barrel shifter direction depends on sign of srcXInc
        ///  - For single-word lines, only endmask1 is used
        /// </summary>
        static void StepWord(Memory mem)
        {
            bool haveSrc = false;
            bool fetchSrc = false;

            {
                // --- Blitter_Step: per-word processing ---
                bool isFirst = (xCount == xCountReset);
                bool isLast = (xCount == 1);

                // Endmask selection
                ushort mask;
                if (isFirst || xCountReset == 1)
                    mask = endmask1;
                else if (isLast)
                    mask = endmask3;
                else
                    mask = endmask2;

                // Reset NFSR state at start of each line
                if (isFirst)
                    nfsrState = false;

                // Latch FXSR flag at start of each line
                bool lineFxsr = false;
                if (isFirst)
                    lineFxsr = fxsr;

                // Determine if source is needed
                bool needSrc = LopNeedSrc[op];
                // HOP must use source: bit1==1, OR halftone(hop==1) with smudge
                needSrc = needSrc && ((hop & 2) != 0 || (hop == 1 && smudge));

                // Determine if destination read is needed
                bool needDst = LopNeedDst[op] || mask != 0xFFFF;

                // FXSR: extra source read at start of line (only if src is needed)
                if (lineFxsr && !haveFxsr && needSrc)
                {
                    SourceShift();
                    BusAccess();
                    lastBusWord = mem.Read16(srcAddr);
                    SourceFetch(lastBusWord);
                    srcAddr = AdvanceAddr(srcAddr, srcXInc);
                    haveFxsr = true;
                }

                // Normal source read (skip when NFSR state is active or source not needed)
                fetchSrc = false;
                if (needSrc && !haveSrc)
                {
                    if (!nfsrState)
                    {
                        SourceShift();
                        BusAccess();
                        lastBusWord = mem.Read16(srcAddr);
                        SourceFetch(lastBusWord);
                        haveSrc = true;
                        fetchSrc = true;
                    }
                }

                // Read destination if needed
                ushort dstWord = 0;
                if (needDst)
                {
                    BusAccess();
                    dstWord = mem.Read16(dstAddr);
                    lastBusWord = dstWord;
                }

                // Special 'weird' case for xCount=1 and NFSR=1 (per Hatari)
                if (nfsr && xCount == 1)
                {
                    SourceShift();
                    SourceFetch(lastBusWord);
                }

                // Barrel-shift by skew
                ushort skewedSrc = (ushort)((srcBuffer >> skew) & 0xFFFF);

                // Halftone
                ushort htWord = smudge
                    ? halftone[skewedSrc & 0x0F]
                    : halftone[lineNumber & 0x0F];

                // HOP
                ushort hopResult = hop switch
                {
                    0 => 0xFFFF,
                    1 => htWord,
                    2 => skewedSrc,
                    3 => (ushort)(skewedSrc & htWord),
                    _ => 0xFFFF
                };

                // LOP
                ushort lopResult = ApplyOp(hopResult, dstWord);

                // Apply endmask (read-modify-write when mask is not all 1s)
                ushort finalResult;
                if (mask != 0xFFFF)
                    finalResult = (ushort)((lopResult & mask) | (dstWord & ~mask));
                else
                    finalResult = lopResult;

                BusAccess();
                mem.Write16(dstAddr, finalResult);
                lastBusWord = finalResult;

                // Special 'weird' case for xCount=1 and NFSR=1 — after write
                if (nfsr && xCount == 1)
                {
                    SourceShift();
                    SourceFetch(lastBusWord);
                }

                // Post-write updates (Blitter_Step continuation)

                // NFSR: activate when xCount reaches 2
                if (xCount == 2 && nfsr)
                    nfsrState = true;

                // Update source address only if a source word was actually fetched
                if (fetchSrc)
                {
                    if (isLast || nfsrState)
                        srcAddr = AdvanceAddr(srcAddr, srcYInc);
                    else
                        srcAddr = AdvanceAddr(srcAddr, srcXInc);
                }

                // Update X/Y count and destination address
                if (isLast)
                {
                    haveFxsr = false;
                    yCount--;
                    xCount = (ushort)xCountReset;

                    dstAddr = AdvanceAddr(dstAddr, dstYInc);

                    if (dstYInc >= 0)
                        lineNumber = (byte)((lineNumber + 1) & 0x0F);
                    else
                        lineNumber = (byte)((lineNumber - 1) & 0x0F);
                }
                else
                {
                    xCount--;
                    dstAddr = AdvanceAddr(dstAddr, dstXInc);
                }

                // Reset per-word state (Blitter_FlushWordState(false))
                haveSrc = false;
                fetchSrc = false;
            }

            if (yCount == 0)
                BlitComplete();
        }

        /// <summary>
        /// The last word of the last line is done: busy <b>and HOG</b> drop, so $FF8A3C reads
        /// back $00 plus the line number after a HOG blit, not $40. In stBlitter.sv a completed
        /// blit raises XYZ, which holds both bits at zero until the y count is written again --
        /// so a 1 written into busy meanwhile does not stick either (see ControlWritten);
        /// Hatari clears both bits at the end of a blit and on a start with y count 0. A program
        /// restarting with <c>bset #7</c> after a HOG blit therefore gets a shared-mode one.
        /// </summary>
        static void BlitComplete()
        {
            busy = false;
            hog = false;
            UpdateIrqLine();
        }

        /// <summary>
        /// Shifts the barrel-shifter buffer. Direction depends on srcXInc sign.
        /// Separate from fetch.
        /// </summary>
        static void SourceShift()
        {
            if (srcXInc >= 0)
                srcBuffer <<= 16;
            else
                srcBuffer >>= 16;
        }

        /// <summary>
        /// Inserts a source word into the barrel-shifter buffer.
        /// Direction depends on srcXInc sign.
        /// </summary>
        static void SourceFetch(ushort word)
        {
            if (srcXInc >= 0)
                srcBuffer |= word;
            else
                srcBuffer |= (uint)word << 16;
        }

        /// <summary>
        /// Advances a 24-bit address by a signed increment.
        /// </summary>
        static uint AdvanceAddr(uint addr, short incr)
        {
            return (uint)((int)(addr & 0xFFFFFF) + incr) & 0xFFFFFF;
        }

        /// <summary>
        /// Applies one of the 16 standard logical operations between source and destination.
        /// </summary>
        static ushort ApplyOp(ushort src, ushort dst)
        {
            return op switch
            {
                0  => 0x0000,                           // 0
                1  => (ushort)(src & dst),               // S AND D
                2  => (ushort)(src & ~dst),              // S AND NOT D
                3  => src,                               // S (copy)
                4  => (ushort)(~src & dst),              // NOT S AND D
                5  => dst,                               // D (no-op)
                6  => (ushort)(src ^ dst),               // S XOR D
                7  => (ushort)(src | dst),               // S OR D
                8  => (ushort)(~(src | dst)),            // NOR
                9  => (ushort)(~(src ^ dst)),            // XNOR
                10 => (ushort)(~dst),                    // NOT D
                11 => (ushort)(src | ~dst),              // S OR NOT D
                12 => (ushort)(~src),                    // NOT S
                13 => (ushort)(~src | dst),              // NOT S OR D
                14 => (ushort)(~(src & dst)),            // NAND
                15 => 0xFFFF,                            // 1
                _  => 0x0000
            };
        }
    }
}
