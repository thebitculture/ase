/*
 * 
 * Outputs colored text to the console using custom markup.
 * Surround text with [[ColorName]] and [[/ColorName]] to change its color.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using System.Text.RegularExpressions;

namespace ASE
{
    /// <summary>
    /// Provides methods for writing colored text to the console.
    /// </summary>
    /// <remarks>The text can include color tags in the format [[ColorName]]Content[[/ColorName]], where
    /// ColorName corresponds to a valid ConsoleColor. If an invalid color is specified, the text will be printed in the
    /// default console color. The WriteLine method appends a new line after the text is written.</remarks>
    public class ColoredConsole
    {
        /// <summary>
        /// Writes the specified text to the console, applying color formatting based on embedded color tags.
        /// </summary>
        /// <remarks>The method supports color names defined in the ConsoleColor enumeration. If an
        /// unrecognized color name is encountered, the text is written in the default console color. The console output
        /// encoding is set to UTF-8 to support a wider range of characters.</remarks>
        /// <param name="text">The text to be written to the console, which may contain color tags in the format
        /// [[ColorName]]Content[[/ColorName]].</param>
        public static void Write(string text)
        {
            var defaultColor = Console.ForegroundColor;
            var pattern = @"\[\[(\w+)\]\](.*?)\[\[/\1\]\]";
            var lastIndex = 0;

            Console.OutputEncoding = System.Text.Encoding.UTF8;

            foreach (Match match in Regex.Matches(text, pattern))
            {
                if (match.Index > lastIndex)
                {
                    Console.ForegroundColor = defaultColor;
                    Console.Write(text.Substring(lastIndex, match.Index - lastIndex));
                }

                var colorName = match.Groups[1].Value;
                var content = match.Groups[2].Value;

                if (Enum.TryParse(colorName, true, out ConsoleColor color))
                {
                    Console.ForegroundColor = color;
                    Console.Write(content);
                }
                else
                {
                    Console.ForegroundColor = defaultColor;
                    Console.Write(match.Value);
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                Console.ForegroundColor = defaultColor;
                Console.Write(text.Substring(lastIndex));
            }

            Console.ForegroundColor = defaultColor;
        }

        /// <summary>
        /// Writes the specified text followed by the current line terminator to the output.
        /// </summary>
        /// <remarks>This method is useful for outputting text with a newline, making it suitable for
        /// logging or console output.</remarks>
        /// <param name="text">The text to write to the output. This parameter cannot be null.</param>
        public static void WriteLine(string text)
        { 
            Write(text);
            Write(Environment.NewLine);
        }
    }
}
