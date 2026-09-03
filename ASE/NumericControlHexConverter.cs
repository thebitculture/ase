/*
 *
 * Hexadecimal text for a NumericUpDown: the debugger's spinners hold ST addresses, and an
 * address is only readable in hex.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace ASE
{
    /// <summary>
    /// Bidirectional text converter for <c>NumericUpDown.TextConverter</c>.
    ///
    /// NOTE THE DIRECTIONS, they are the opposite of what the names suggest: Avalonia formats
    /// the value into the box through <see cref="ConvertBack"/> (decimal -> string, from
    /// NumericUpDown.ConvertValueToText) and parses what the user types through
    /// <see cref="Convert"/> (string -> decimal?, from NumericUpDown.ConvertTextToValueCore).
    /// Writing them the intuitive way round leaves the control silently dead — it shows an
    /// empty box and refuses every value typed into it, which is exactly how it presents.
    /// </summary>
    public class NumericControlHexConverter : IValueConverter
    {
        /// <summary>
        /// Digits the value is padded to. Six is a 68000 address ($000000-$FFFFFF), which is
        /// what the debugger uses this for.
        /// </summary>
        public int Digits { get; set; } = 6;

        /// <summary>
        /// Text -> value. Anything the box cannot read throws, which is what tells NumericUpDown
        /// the input is not valid yet: it then keeps the value it had and greys the spinner out
        /// instead of resetting the field while a number is half typed.
        /// </summary>
        public object Convert(
                                object value,
                                Type targetType,
                                object parameter,
                                CultureInfo culture)
        {
            if (value is not string text)
                throw new FormatException("Not a hexadecimal string");

            // Both notations people actually type: the 68000 world's $ and C's 0x
            text = text.Trim().TrimStart('$');

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];

            if (text.Length == 0)
                return (decimal?)null;

            if (!long.TryParse(
                    text,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var result))
            {
                throw new FormatException($"'{text}' is not a hexadecimal number");
            }

            return (decimal?)result;
        }

        /// <summary>Value -> text.</summary>
        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            if (value is decimal d)
                return ((long)d).ToString("X" + Digits, CultureInfo.InvariantCulture);

            return "";
        }
    }
}
