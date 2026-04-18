/*
 * 
 * Atari STE BLiTTER (BLock Image Transfer) emulation.
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
                    UnpackControl(v);
                    if (busy) Execute();
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
                UnpackControl((byte)(v >> 8));
                if (busy) Execute();
                return;
            }

            WriteByte(addr, (byte)(v >> 8));
            WriteByte(addr + 1, (byte)(v & 0xFF));
        }

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

        // --- LOP need_src / need_dst tables (per Hatari) ---
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
        /// Executes the blit operation. Runs synchronously.
        /// Faithfully follows Hatari's Blitter_Step + Blitter_ProcessWord model:
        ///  - need_src gates ALL source reads (FXSR + normal) AND source address updates
        ///  - need_dst determines whether destination is read (forced true when mask != 0xFFFF)
        ///  - NFSR state is reset at start of each line, set when xCount reaches 2
        ///  - FXSR flag is latched per-line at the first word
        ///  - Special weird case: xCount=1 + NFSR does SourceShift+Fetch(busWord) before and after write
        ///  - Barrel shifter direction depends on sign of srcXInc
        ///  - For single-word lines, only endmask1 is used
        /// </summary>
        static void Execute()
        {
            if (yCount == 0)
            {
                busy = false;
                return;
            }

            Memory mem = ASEMain._mem;

            // Use the static xCountReset field. If it's somehow 0, treat as no-op.
            if (xCountReset == 0)
            {
                busy = false;
                return;
            }

            bool nfsrState = false;
            bool haveFxsr = false;
            bool haveSrc = false;
            bool fetchSrc = false;
            ushort lastBusWord = 0; // last word read from bus (for NFSR special case)

            while (yCount > 0)
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

                // Determine if source is needed (per Hatari's need_src logic)
                bool needSrc = LopNeedSrc[op];
                // HOP must use source: bit1==1, OR halftone(hop==1) with smudge
                needSrc = needSrc && ((hop & 2) != 0 || (hop == 1 && smudge));

                // Determine if destination read is needed
                bool needDst = LopNeedDst[op] || mask != 0xFFFF;

                // FXSR: extra source read at start of line (only if src is needed)
                if (lineFxsr && !haveFxsr && needSrc)
                {
                    SourceShift();
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
                        lastBusWord = mem.Read16(srcAddr);
                        SourceFetch(lastBusWord);
                        haveSrc = true;
                        fetchSrc = true;
                    }
                }

                // Read destination if needed
                ushort dstWord = needDst ? mem.Read16(dstAddr) : (ushort)0;

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

                mem.Write16(dstAddr, finalResult);

                // Special 'weird' case for xCount=1 and NFSR=1 — after write (per Hatari)
                if (nfsr && xCount == 1)
                {
                    SourceShift();
                    SourceFetch(lastBusWord);
                }

                // --- Post-write updates (Blitter_Step continuation) ---

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

            busy = false;
        }

        /// <summary>
        /// Shifts the barrel-shifter buffer. Direction depends on srcXInc sign.
        /// Separate from fetch (per Hatari's Blitter_SourceShift).
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
        /// Direction depends on srcXInc sign (per Hatari's Blitter_SourceFetch).
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
