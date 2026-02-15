/*
 * 
 * Render routines for Atari ST video modes.
 * By now, only low and medium resolutions are supported.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using SDL2;

namespace ASE
{
    public class Video
    {
        unsafe public static void SetPixel(IntPtr pixels, int pitch, int x, int y, uint argb)
        {
            uint* row = (uint*)((byte*)pixels + y * pitch);
            row[x] = argb;
        }

        public static class AtariStRenderer
        {
            public enum StVideoMode
            {
                Low320x200x16,
                Med640x200x4,
                High640x400x2,
                Auto = 99
            }

            public static uint StColorToArgb8888(ushort stColor)
            {
                // STFM/F palette, for the STE, this should be 16 tones of every primary color
                // but I have not implemented :-/
                int red3 = (stColor >> 8) & 0x07;  // Bits 8-6
                int green3 = (stColor >> 4) & 0x07;  // Bits 5-3
                int blue3 = (stColor >> 0) & 0x07;  // Bits 2-0

                int red8 = (red3 << 5) | (red3 << 2) | (red3 >> 1);
                int green8 = (green3 << 5) | (green3 << 2) | (green3 >> 1);
                int blue8 = (blue3 << 5) | (blue3 << 2) | (blue3 >> 1);

                // ARGB8888: Alpha | red | green | blue
                uint argb = 0xFF000000 |
                            ((uint)red8 << 0) |
                            ((uint)green8 << 8) | 
                            ((uint)blue8 << 16);
                
                return argb;
            }

            private static uint[] StPalTo8888()
            {
                uint[] pal = new uint[16];
                int palCount = 16;

                for (int i = 0; i < palCount; i++)
                    pal[i] = StColorToArgb8888(ASEMain._mem.Read16((uint)(Memory.STPortAdress.ST_PALLETE + (i * 2))));

                return pal;
            }


            public static void BlitStLineToBuffer(uint[] buffer, uint StAddr = 0, int scanlineSrc = 0, int scanlineDst = 0, StVideoMode mode = StVideoMode.Auto)
            {
                mode = GetModeInfo(mode, out int w, out int h, out int planes, out int wordsPerLine);
                
                uint[] pal = StPalTo8888();

                int bytesPerLine = wordsPerLine * planes * 2;

                uint vramBase;

                if (StAddr == 0)
                {
                    uint vramBaseHigh = ASEMain._mem.Read8(Memory.STPortAdress.ST_SCRHIGHADDR);
                    uint vramBaseMid = ASEMain._mem.Read8(Memory.STPortAdress.ST_SCRMIDADDR);

                    vramBase = (vramBaseHigh * 0x10000) + (vramBaseMid * 0x100);
                }
                else
                {
                    vramBase = StAddr;
                }

                uint srcLine = vramBase + (uint)(scanlineSrc * bytesPerLine);
                int dstPixel = scanlineDst * 640;

                for (int group = 0; group < wordsPerLine; group++)
                {
                    ushort w0 = ReadBEWord(srcLine, group, planes, 0);
                    ushort w1 = planes > 1 ? ReadBEWord(srcLine, group, planes, 1) : (ushort)0;
                    ushort w2 = planes > 2 ? ReadBEWord(srcLine, group, planes, 2) : (ushort)0;
                    ushort w3 = planes > 3 ? ReadBEWord(srcLine, group, planes, 3) : (ushort)0;

                    for (int bit = 15; bit >= 0; bit--)
                    {
                        int idx = 0;
                        idx |= ((w0 >> bit) & 1) << 0;
                        idx |= ((w1 >> bit) & 1) << 1;
                        idx |= ((w2 >> bit) & 1) << 2;
                        idx |= ((w3 >> bit) & 1) << 3;

                        buffer[dstPixel++] = pal[idx];

                        if (mode == StVideoMode.Low320x200x16)
                            buffer[dstPixel++] = pal[idx];
                    }
                }
            }

            public static unsafe void BufferToTexture(IntPtr texture, uint[] buffer)
            {
                // Lock texture
                IntPtr pixels;
                int pitch;
                if (SDL.SDL_LockTexture(texture, IntPtr.Zero, out pixels, out pitch) != 0)
                    throw new Exception(SDL.SDL_GetError());
                try
                {
                    byte* dstBase = (byte*)pixels;
                    for (int y = 0; y < 200; y++)
                    {
                        uint* dstRow = (uint*)(dstBase + y * pitch);
                        int srcPixel = y * 640;
                        for (int x = 0; x < 640; x++)
                        {
                            dstRow[x] = buffer[srcPixel++];
                        }
                    }
                }
                finally
                {
                    SDL.SDL_UnlockTexture(texture);
                }
            }

            private static unsafe ushort ReadBEWord(uint srcLine, int group, int planes, int plane)
            {
                uint offsetBytes = (uint)((group * planes + plane) * 2);
                return BigEndian.Read16(srcLine + offsetBytes);
            }

            private static StVideoMode GetModeInfo(StVideoMode mode, out int w, out int h, out int planes, out int wordsPerLine)
            {
                if(mode == StVideoMode.Auto)
                    mode = ASEMain._mem.Read8(Memory.STPortAdress.ST_RES) == 0 ? AtariStRenderer.StVideoMode.Low320x200x16 : AtariStRenderer.StVideoMode.Med640x200x4;

                switch (mode)
                {
                    case StVideoMode.Low320x200x16:
                        w = 320; h = 200; planes = 4; wordsPerLine = 20; return mode;
                    case StVideoMode.Med640x200x4:
                        w = 640; h = 200; planes = 2; wordsPerLine = 40; return mode;
                    case StVideoMode.High640x400x2:
                        w = 640; h = 400; planes = 1; wordsPerLine = 40; return mode;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode));
                }
            }
        }
    }
}
