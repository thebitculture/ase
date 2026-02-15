/*
 * 
 * BigEndian helper
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 */

namespace ASE
{
    /// <summary>
    /// Provides static methods for reading and writing 16-bit and 32-bit unsigned integers in Motorola 68k big-endian byte order
    /// from and to specified memory addresses.
    /// </summary>
    /// <remarks>Use the methods of this class to ensure correct handling of byte order when interacting with
    /// memory-mapped data or external systems that require big-endian format. These methods handle exceptions that may
    /// occur during memory access, but callers should be aware that exceptions may still be thrown if the underlying
    /// memory operations fail.</remarks>
    public class BigEndian
    {
        /// <summary>
        /// Reads a 16-bit unsigned integer from the specified memory address in Motorola 68k big-endian format.
        /// </summary>
        /// <remarks>32 bit addresses will be trimmed to 24 bits addresses.</remarks>
        /// <param name="addr">The address in memory from which to read the 16-bit value. Must be a valid memory address.</param>
        /// <returns>The 16-bit unsigned integer read from the specified address.</returns>
        public static ushort Read16(uint addr)
        {
            return (ushort)((ASEMain._mem.Read8(addr) << 8) | ASEMain._mem.Read8(addr + 1));
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer from the specified memory address in big-endian format.
        /// </summary>
        /// <remarks>32 bit addresses will be trimmed to 24 bits addresses.</remarks>
        /// <param name="addr">The address from which to read the 32-bit unsigned integer. This address must be valid and accessible.</param>
        /// <returns>A 32-bit unsigned integer representing the value read from the specified address.</returns>
        public static uint Read32(uint addr)
        {
            return ((uint)ASEMain._mem.Read8(addr) << 24) |
                    ((uint)ASEMain._mem.Read8(addr + 1) << 16) |
                    ((uint)ASEMain._mem.Read8(addr + 2) << 8) |
                    ASEMain._mem.Read8(addr + 3);
        }

        /// <summary>
        /// Writes a 16-bit unsigned integer value to the specified memory address in big-endian byte order.
        /// </summary>
        /// <remarks>32 bit addresses will be trimmed to 24 bits addresses.</remarks>
        /// <param name="addr">The memory address at which to write the 16-bit value. The address must be valid and accessible for writing.</param>
        /// <param name="v">The 16-bit unsigned integer value to write to memory. The most significant byte is written first.</param>
        public static void Write16(uint addr, ushort v)
        {
            ASEMain._mem.Write8(addr, (byte)(v >> 8));
            ASEMain._mem.Write8(addr + 1, (byte)v);
        }

        /// <summary>
        /// Writes a 32-bit unsigned integer value to the specified memory address in big-endian byte order.
        /// </summary>
        /// <remarks>32 bit addresses will be trimmed to 24 bits addresses.</remarks>
        /// <param name="addr">The memory address at which to write the 32-bit value. The address must be valid and properly aligned for
        /// writing.</param>
        /// <param name="v">The 32-bit unsigned integer value to write to memory.</param>
        public static void Write32(uint addr, uint v)
        {
            ASEMain._mem.Write8(addr, (byte)(v >> 24));
            ASEMain._mem.Write8(addr + 1, (byte)(v >> 16));
            ASEMain._mem.Write8(addr + 2, (byte)(v >> 8));
            ASEMain._mem.Write8(addr + 3, (byte)v);
        }

    }
}
