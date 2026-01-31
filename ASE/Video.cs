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

            /// <summary>
            /// Converts a 16-bit ST color value to a 32-bit ARGB8888 color value.
            /// </summary>
            /// <remarks>This method expands the 3-bit red, green, and blue components from the ST
            /// color format to 8 bits each to produce a standard ARGB8888 color value. The alpha channel is always set
            /// to fully opaque.</remarks>
            /// <param name="stColor">The 16-bit color value in ST format, where the red, green, and blue components are each represented by 3
            /// bits.</param>
            /// <returns>A 32-bit unsigned integer representing the color in ARGB8888 format, with the alpha channel set to 255
            /// (opaque).</returns>
            public static uint St12ToArgb8888(ushort stColor)
            {
                int red3 = (stColor >> 8) & 0x07;  // Bits 8-6
                int green3 = (stColor >> 4) & 0x07;  // Bits 5-3
                int blue3 = (stColor >> 0) & 0x07;  // Bits 2-0

                int red8 = (red3 << 5) | (red3 << 2) | (red3 >> 1);
                int green8 = (green3 << 5) | (green3 << 2) | (green3 >> 1);
                int blue8 = (blue3 << 5) | (blue3 << 2) | (blue3 >> 1);

                // Construir ARGB8888: Alpha | Rojo | Verde | Azul
                uint argb = 0xFF000000 |                  // Alpha = 255 (opaco)
                            ((uint)red8 << 16) |          // Rojo en bits 16-23
                            ((uint)green8 << 8) |         // Verde en bits 8-15
                            ((uint)blue8);                // Azul en bits 0-7

                return argb;
            }

            /// <summary>
            /// Copies the current frame from the ST video memory to the specified texture, converting the pixel data
            /// according to the selected video mode.
            /// </summary>
            /// <remarks>This method directly manipulates the texture's pixel data based on the
            /// specified video mode, which affects how the frame is rendered. Ensure that the texture is created with
            /// appropriate format and dimensions to match the expected output.</remarks>
            /// <param name="texture">A handle to the texture that will receive the frame data. The texture must be created with a format and
            /// dimensions compatible with the expected output.</param>
            /// <param name="mode">The video mode to use for the blitting operation. If set to StVideoMode.Auto, the mode is determined
            /// automatically based on the current settings.</param>
            /// <exception cref="Exception">Thrown if the texture cannot be locked for writing, indicating an error occurred during the locking
            /// process.</exception>
            public static unsafe void BlitStFrameToTexture(IntPtr texture, StVideoMode mode = StVideoMode.Auto)
            {
                mode = GetModeInfo(mode, out int w, out int h, out int planes, out int wordsPerLine);

                uint[] pal = StPalTo8888();

                // Lock texture
                IntPtr pixels;
                int pitch;
                if (SDL.SDL_LockTexture(texture, IntPtr.Zero, out pixels, out pitch) != 0)
                    throw new Exception(SDL.SDL_GetError());

                try
                {
                    uint vramBase;
                    byte* dstBase = (byte*)pixels;
                    uint vramBaseHigh = Program._mem.Read8(Memory.STPortAdress.ST_SCRHIGHADDR);
                    uint vramBaseMid = Program._mem.Read8(Memory.STPortAdress.ST_SCRMIDADDR);

                    vramBase = (vramBaseHigh * 0x10000) + (vramBaseMid * 0x100);

                    // Low 320: wordsPerLine=20 (20*16=320), planes=4
                    // Med 640: wordsPerLine=40 (40*16=640), planes=2
                    // High 640: wordsPerLine=40 (40*16=640), planes=1 <- Not emulated yet

                    int bytesPerLine = wordsPerLine * planes * 2;

                    for (int y = 0; y < h; y++)
                    {
                        uint* dstRow = (uint*)(dstBase + y * pitch);
                        uint srcLine = vramBase + (uint)(y * bytesPerLine);

                        int x = 0;
                        for (int group = 0; group < wordsPerLine; group++)
                        {
                            // Reads 16 pixels from the ST video memory
                            // Order color mode low res: plane0word, plane1word, plane2word, plane3word -> 4 planes, 16 colors -> 4 words per pixel group
                            // Order medium res: plane0, plane1 -> 2 planes, 4 colors -> 2 words per pixel group
                            ushort w0 = ReadBEWord(srcLine, group, planes, 0);
                            ushort w1 = planes > 1 ? ReadBEWord(srcLine, group, planes, 1) : (ushort)0;
                            ushort w2 = planes > 2 ? ReadBEWord(srcLine, group, planes, 2) : (ushort)0;
                            ushort w3 = planes > 3 ? ReadBEWord(srcLine, group, planes, 3) : (ushort)0;

                            // Para cada pixel creamos el índice de color
                            for (int bit = 15; bit >= 0; bit--)
                            {
                                int idx = 0;
                                idx |= ((w0 >> bit) & 1) << 0;
                                idx |= ((w1 >> bit) & 1) << 1;
                                idx |= ((w2 >> bit) & 1) << 2;
                                idx |= ((w3 >> bit) & 1) << 3;

                                dstRow[x++] = pal[idx];

                                if(mode == StVideoMode.Low320x200x16)
                                    dstRow[x++] = pal[idx];
                            }
                        }
                    }
                }
                finally
                {
                    SDL.SDL_UnlockTexture(texture);
                }
            }

            /// <summary>
            /// Copies a scanline from Atari ST video memory to the specified buffer, converting pixel data according to
            /// the selected video mode and palette.
            /// </summary>
            /// <remarks>This method supports multiple Atari ST video modes and automatically handles
            /// palette conversion to 32-bit color. The buffer must be pre-allocated and large enough to accommodate the
            /// written scanline at the specified destination index. If the video memory address is not provided, the
            /// method reads the current screen base address from the hardware ports.</remarks>
            /// <param name="buffer">The destination array that receives the converted pixel data for the scanline. Must be large enough to
            /// hold the output pixels at the specified destination index.</param>
            /// <param name="StAddr">The ST starting address in video memory from which to read the scanline data. This also may be
            /// usefull to make a window to inspect memory. If set to 0, the method
            /// retrieves the base address from the Atari ST hardware ports.</param>
            /// <param name="scanlineSrc">The zero-based index of the source scanline to read from video memory.</param>
            /// <param name="scanlineDst">The zero-based index in the buffer where the scanline data will begin to be written.</param>
            /// <param name="mode">The Atari ST video mode to use for interpreting the scanline data. If set to Auto, the mode is
            /// determined automatically.</param>
            public static void BlitStLineToBuffer(uint[] buffer, uint StAddr = 0, int scanlineSrc = 0, int scanlineDst = 0, StVideoMode mode = StVideoMode.Auto)
            {
                mode = GetModeInfo(mode, out int w, out int h, out int planes, out int wordsPerLine);
                
                uint[] pal = StPalTo8888();

                int bytesPerLine = wordsPerLine * planes * 2;

                uint vramBase;

                // Si StSddr=0, leemos de los puertos ST la dirección base de la memoria para la pantalla.

                if (StAddr == 0)
                {
                    uint vramBaseHigh = Program._mem.Read8(Memory.STPortAdress.ST_SCRHIGHADDR);
                    uint vramBaseMid = Program._mem.Read8(Memory.STPortAdress.ST_SCRMIDADDR);

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

            /// <summary>
            /// Copies pixel data from the specified buffer to the given texture, locking the texture for direct access
            /// during the operation.
            /// </summary>
            /// <remarks>The method assumes that the texture is compatible with the pixel format and
            /// dimensions of the buffer. The buffer should contain pixel data in a format that matches the texture's
            /// expected format and size. The texture is automatically unlocked after the operation completes, even if
            /// an exception occurs.</remarks>
            /// <param name="texture">A handle to the texture that will receive the pixel data.</param>
            /// <param name="buffer">An array of 32-bit unsigned integers containing the pixel data to copy to the texture. The buffer must
            /// contain enough data to fill the texture area being updated.</param>
            /// <exception cref="Exception">Thrown if the texture cannot be locked for writing, indicating an error occurred during the operation.</exception>
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

            // Lee una word big-endian en la línea para un grupo y un plano.
            private static unsafe ushort ReadBEWord(uint srcLine, int group, int planes, int plane)
            {
                uint offsetBytes = (uint)((group * planes + plane) * 2);
                return BigEndian.Read16(srcLine + offsetBytes);
            }

            private static uint[] StPalTo8888()
            {
                // Preconvertimos paleta a ARGB
                uint[] pal = new uint[16];
                int palCount = 16;

                for (int i = 0; i < palCount; i++)
                    pal[i] = St12ToArgb8888(Program._mem.Read16((uint)(Memory.STPortAdress.ST_PALLETE + (i * 2))));

                return pal;
            }

            private static StVideoMode GetModeInfo(StVideoMode mode, out int w, out int h, out int planes, out int wordsPerLine)
            {
                if(mode == StVideoMode.Auto)
                    mode = Program._mem.Read8(Memory.STPortAdress.ST_RES) == 0 ? AtariStRenderer.StVideoMode.Low320x200x16 : AtariStRenderer.StVideoMode.Med640x200x4;

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
