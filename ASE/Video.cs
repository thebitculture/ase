/*
 *
 * Render routines for Atari ST video modes.
 * Low and medium resolutions, now including the screen borders (overscan) so the
 * border-removal tricks become visible. The visible window, the Display Enable span and
 * the per-line shifter address all come from VideoTiming.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE
{
    public class Video
    {
        public static class AtariStRenderer
        {
            public static uint StColorToArgb8888(ushort stColor)
            {
                int red8, green8, blue8;

                if (Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE)
                {
                    // STE palette: 4 bits per component, 4096 colors.
                    // The upper 3 bits of each component occupy the same positions as the ST (bits 10-8, 6-4, 2-0).
                    // The new LSB for each component is stored at bits 11 (red), 7 (green) and 3 (blue).
                    int red4   = (((stColor >>  8) & 0x07) << 1) | ((stColor >> 11) & 0x01);
                    int green4 = (((stColor >>  4) & 0x07) << 1) | ((stColor >>  7) & 0x01);
                    int blue4  = (((stColor >>  0) & 0x07) << 1) | ((stColor >>  3) & 0x01);

                    red8   = (red4   << 4) | red4;
                    green8 = (green4 << 4) | green4;
                    blue8  = (blue4  << 4) | blue4;
                }
                else
                {
                    // ST / STFM / Mega palette: 3 bits per component, 512 colors.
                    int red3   = (stColor >> 8) & 0x07;  // Bits 10-8
                    int green3 = (stColor >> 4) & 0x07;  // Bits 6-4
                    int blue3  = (stColor >> 0) & 0x07;  // Bits 2-0

                    red8   = (red3 << 5) | (red3 << 2) | (red3 >> 1);
                    green8 = (green3 << 5) | (green3 << 2) | (green3 >> 1);
                    blue8  = (blue3 << 5) | (blue3 << 2) | (blue3 >> 1);
                }

                // ARGB8888: Alpha | red | green | blue
                uint argb = 0xFF000000 |
                            ((uint)red8   <<  0) |
                            ((uint)green8 <<  8) |
                            ((uint)blue8  << 16);

                return argb;
            }

            // Reusable scratch for one line's palette (avoids a per-line allocation). Rendering
            // runs only on the emulation thread, so static buffers are safe.
            private static readonly uint[] _pal = new uint[16];   // ARGB, updated live by palette writes
            private static readonly byte[] _work = new byte[32];  // raw $FF8240 bytes, mutated as events apply

            // Hatari encodes the same correction as 7 extra 4-cycle spans (28 cycles) in
            // Spec512_StartScanLine ("'7' is required to align pixels and colors"); ASE's value
            // differs because Moira's precise timing stamps the write at a different point inside
            // the instruction than Hatari's convention.
            // 19 is the value at which every image renders clean (20 applied some entries one pixel early,
            // spraying wrong-coloured pixels on a few images). Without the lead every mid-line
            // palette change shows up that many low-res pixels late — invisible on flat palette
            // splits, but a Spectrum 512 photo shows it wherever consecutive values of an entry
            // differ.
            //
            // The BORDER samples colour 0 with ONE MORE cycle of lead (PaintBorderSpan). The
            // stamps sit on the 4-cycle bus grid, so a single integer cannot place both edges:
            // the display columns calibrate to 19, but the colour-0 write that on real hardware
            // (and Hatari) already covers the first right-border pixel needs 20 there — with 19
            // on both, a 1-px stripe of the previous border colour runs down the display→border
            // seam. Sub-grid detail the 4-aligned stamps cannot express, split empirically.
            //
            // I think this needs further investigation, and it would be useful if this variable
            // could be set either as an environment variable or as a parameter so we can run more tests.
            const int PaletteWriteLead = 19;
            const int BorderWriteLead  = PaletteWriteLead + 1;

            /// <summary>
            /// Renders one full texture row (border + active display) for the given line.
            /// The whole row is first filled with the background colour (palette entry 0, the
            /// real border colour); the active display, whose horizontal span comes from the
            /// resolved Display Enable window, is then drawn on top. Opened borders extend the
            /// DE window and therefore spill naturally into the border area.
            ///
            /// The palette starts from the snapshot VideoTiming took at the start of the line and
            /// is updated *while the row is drawn*, replaying the palette-register writes that
            /// happened during the line at their captured horizontal position. That is what makes
            /// the Spectrum 512 trick (and ordinary mid-line palette splits) appear: a single
            /// scanline can show more than 16 colours.
            /// </summary>
            public static void BlitLineWithBorders(uint[] buffer, VideoTiming.LineInfo li)
            {
                if (VideoTiming.Mono)
                {
                    BlitLineMono(buffer, li);
                    return;
                }

                int ty = li.Line - VideoTiming.RENDER_TOP_LINE;
                if (ty < 0 || ty >= VideoTiming.BUFFER_HEIGHT)
                    return;

                int width = VideoTiming.BUFFER_WIDTH;
                int rowBase = ty * width;

                // Start from the palette as it was at the beginning of the line. _work keeps the
                // raw bytes so each mid-line write can be applied and its colour recomputed.
                byte[] raw = VideoTiming.LineStartPalette;
                Array.Copy(raw, _work, 32);
                for (int i = 0; i < 16; i++)
                    _pal[i] = StColorToArgb8888((ushort)((_work[i * 2] << 8) | _work[i * 2 + 1]));

                int palCount = VideoTiming.PaletteEventCount;
                VideoTiming.PalEvent[] palEv = VideoTiming.PaletteEvents;
                int evIdx = 0;

                // Applies to _work/_pal every pending palette event stamped at or before cyc.
                // Events are chronological, so each phase below only ever moves evIdx forward.
                void ApplyPaletteUpTo(int cyc)
                {
                    while (evIdx < palCount && palEv[evIdx].Cycle <= cyc)
                    {
                        int off = palEv[evIdx].ByteOffset;
                        _work[off] = palEv[evIdx].Val;
                        int ci = off >> 1;
                        _pal[ci] = StColorToArgb8888((ushort)((_work[ci * 2] << 8) | _work[ci * 2 + 1]));
                        evIdx++;
                    }
                }

                // Border pixels for tube cycles [cycFrom, cycTo), replaying the colour-0 writes at
                // their horizontal position. The real border shows palette register 0 LIVE, so on a
                // line with mid-line writes (Spectrum 512) the border is striped within the line,
                // not one solid colour; a single line-start value painted black bars into the left
                // border where the write stream had left colour 0 at the previous line's last value.
                void PaintBorderSpan(int cycFrom, int cycTo)
                {
                    if (cycFrom < VideoTiming.VISIBLE_LEFT_CYCLE) cycFrom = VideoTiming.VISIBLE_LEFT_CYCLE;
                    if (cycTo > VideoTiming.VISIBLE_RIGHT_CYCLE) cycTo = VideoTiming.VISIBLE_RIGHT_CYCLE;
                    for (int cyc = cycFrom; cyc < cycTo; cyc++)
                    {
                        ApplyPaletteUpTo(cyc + BorderWriteLead);
                        int bx = (cyc - VideoTiming.VISIBLE_LEFT_CYCLE) * 2;
                        buffer[rowBase + bx] = _pal[0];
                        buffer[rowBase + bx + 1] = _pal[0];
                    }
                }

                uint border = _pal[0];
                for (int x = 0; x < width; x++)
                    buffer[rowBase + x] = border;

                if (!li.HasDisplay)
                {
                    // Pure border line: still replay the colour-0 writes across it (the visible
                    // striping above/below a Spectrum 512 picture). No cost on ordinary lines.
                    if (palCount > 0)
                        PaintBorderSpan(VideoTiming.VISIBLE_LEFT_CYCLE, VideoTiming.VISIBLE_RIGHT_CYCLE);
                    return;
                }

                // Low resolution (4 planes, pixel-doubled into the texture) vs medium (2 planes, 1:1).
                bool low = (li.Res & 0x03) == 0;
                int planes = low ? 4 : 2;
                int bytesPerGroup = planes * 2;     // bytes consumed per 16-pixel group

                int bytes = (li.DeStop - li.DeStart) / 2;
                if (bytes < bytesPerGroup)
                    return;
                int groups = bytes / bytesPerGroup;

                // Texture X of the first displayed pixel. May be negative when the left border
                // is opened; out-of-range writes are simply clipped below.
                int tx = (li.DeStart - VideoTiming.VISIBLE_LEFT_CYCLE) * 2;
                uint srcLine = li.VideoAddr;

                // STE fine scroll: the shifter prefetched one extra word per plane before the
                // display window, and the picture starts li.HScroll pixels into it. So the source
                // pixel index runs ahead of the output one by exactly that many pixels, and the
                // 16-pixel group is re-read whenever it rolls over — with no scroll (every ST, and
                // an STE that is not scrolling) this is the plain group-by-group walk it replaces.
                int scroll = li.HScroll & 0x0F;
                int outPixels = groups * 16;

                // Left border, in draw (and therefore event) order before the display.
                if (palCount > 0)
                    PaintBorderSpan(VideoTiming.VISIBLE_LEFT_CYCLE, li.DeStart);

                int srcGroup = -1;
                ushort w0 = 0, w1 = 0, w2 = 0, w3 = 0;

                for (int p = 0; p < outPixels; p++)
                {
                    int srcPixel = p + scroll;
                    int group = srcPixel >> 4;
                    if (group != srcGroup)
                    {
                        srcGroup = group;
                        w0 = ReadBEWord(srcLine, group, planes, 0);
                        w1 = planes > 1 ? ReadBEWord(srcLine, group, planes, 1) : (ushort)0;
                        w2 = planes > 2 ? ReadBEWord(srcLine, group, planes, 2) : (ushort)0;
                        w3 = planes > 3 ? ReadBEWord(srcLine, group, planes, 3) : (ushort)0;
                    }
                    int bit = 15 - (srcPixel & 0x0F);

                    // Apply any palette writes that land at or before this pixel, shifted by the
                    // fetch->emission pipeline (PaletteWriteLead). Cheap no-op for ordinary
                    // frames, where the line has no mid-line writes.
                    if (evIdx < palCount)
                        ApplyPaletteUpTo(li.DeStart + (low ? p : (p >> 1)) + PaletteWriteLead);

                    int idx = ((w0 >> bit) & 1)
                            | (((w1 >> bit) & 1) << 1)
                            | (((w2 >> bit) & 1) << 2)
                            | (((w3 >> bit) & 1) << 3);
                    uint color = _pal[idx];

                    if (low)
                    {
                        if ((uint)tx < (uint)width) buffer[rowBase + tx] = color;
                        int tx1 = tx + 1;
                        if ((uint)tx1 < (uint)width) buffer[rowBase + tx1] = color;
                        tx += 2;
                    }
                    else
                    {
                        if ((uint)tx < (uint)width) buffer[rowBase + tx] = color;
                        tx += 1;
                    }
                }

                // Right border, after the display so the event cursor keeps moving forward.
                if (palCount > 0)
                    PaintBorderSpan(li.DeStop, VideoTiming.VISIBLE_RIGHT_CYCLE);
            }

            private static unsafe ushort ReadBEWord(uint srcLine, int group, int planes, int plane)
            {
                uint offsetBytes = (uint)((group * planes + plane) * 2);
                // Shifter-style fetch: no bus errors, no side effects. See Memory.ReadVideoWord —
                // reading this through the CPU bus plants spurious faults whenever the video
                // counter points outside RAM, which takes the emulated machine down.
                return ASEMain._mem.ReadVideoWord(srcLine + offsetBytes);
            }

            /// <summary>
            /// Renders one 640-pixel high-resolution (monochrome) row. One bitplane, 40 words
            /// (80 bytes) starting at the line's shifter address. The SM124 is purely black and
            /// white: the shifter does NOT use the palette RGB in high resolution — colour
            /// register 0 only selects normal vs reverse video (pixel 0 shows its tone, pixel 1
            /// the inverse), so registers can hold leftover colour values and must be ignored.
            /// No borders or mid-line palette tricks are modelled here.
            /// </summary>
            static void BlitLineMono(uint[] buffer, VideoTiming.LineInfo li)
            {
                int ty = li.Line - VideoTiming.RENDER_TOP_LINE;
                if (ty < 0 || ty >= VideoTiming.BUFFER_HEIGHT)
                    return;

                int width = VideoTiming.BUFFER_WIDTH;   // 640
                int rowBase = ty * width;

                const uint white = 0xFFFFFFFFu;
                const uint black = 0xFF000000u;

                // Colour register 0 = 0 means reverse video (white on black); anything else is the
                // usual black on white. Pixel value 0 -> register 0's tone, value 1 -> its inverse.
                byte[] raw = VideoTiming.LineStartPalette;
                int reg0 = ((raw[0] << 8) | raw[1]) & 0x0FFF;
                bool reg0White = reg0 != 0;
                uint pal0 = reg0White ? white : black;
                uint pal1 = reg0White ? black : white;

                if (!li.HasDisplay)
                {
                    for (int x = 0; x < width; x++)
                        buffer[rowBase + x] = pal0;
                    return;
                }

                uint srcLine = li.VideoAddr;
                int tx = 0;
                int words = width / 16;   // 40 words per 640-pixel line
                for (int w = 0; w < words; w++)
                {
                    ushort bits = ReadBEWord(srcLine, w, 1, 0);
                    for (int bit = 15; bit >= 0; bit--)
                        buffer[rowBase + tx++] = ((bits >> bit) & 1) != 0 ? pal1 : pal0;
                }
            }
        }
    }
}
