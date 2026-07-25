/*
 *
 * Editable hexadecimal memory monitor control for the debugger.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;
using System.Text;

namespace ASE;

/// <summary>
/// Editable hexadecimal memory monitor. Shows 16 bytes per row (address, hex and ASCII)
/// over the 68000's 24-bit address space. Contents are read and written through the
/// <see cref="Peek"/> and <see cref="Poke"/> delegates, so the control knows nothing
/// about the bus: the caller decides which regions are visible/editable.
///
/// Navigation: arrow keys, PageUp/PageDown, Home/End (Ctrl+Home/End goes to the start/end
/// of memory), mouse wheel and clicking on a byte (in the hex zone or the ASCII one).
/// </summary>
public class HexEditor : Control
{
    public const uint AddressMask = 0xFFFFFF;   // 24-bit address space

    const int BytesPerRow = 16;
    const int AddrChars = 8;                    // "AAAAAA: "
    const int AsciiBarCol = AddrChars + BytesPerRow * 3 + 1;    // column of the leading '|'
    const double PadX = 4;
    const double PadY = 2;
    const double FontSizePx = 13;
    const string HexDigits = "0123456789ABCDEF";

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<HexEditor, IBrush>(nameof(Foreground), Brushes.Gainsboro);

    public static readonly StyledProperty<IBrush> AddressBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush>(nameof(AddressBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush> AsciiBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush>(nameof(AsciiBrush), Brushes.DarkGray);

    public static readonly StyledProperty<IBrush> CursorBrushProperty =
        AvaloniaProperty.Register<HexEditor, IBrush>(nameof(CursorBrush), new SolidColorBrush(Color.Parse("#337ACC")));

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush AddressBrush
    {
        get => GetValue(AddressBrushProperty);
        set => SetValue(AddressBrushProperty, value);
    }

    public IBrush AsciiBrush
    {
        get => GetValue(AsciiBrushProperty);
        set => SetValue(AsciiBrushProperty, value);
    }

    public IBrush CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    /// <summary>Reads a byte of memory (must have no side effects).</summary>
    public Func<uint, byte> Peek { get; set; }

    /// <summary>Writes a byte of memory. Returns false if the region is not editable.</summary>
    public Func<uint, byte, bool> Poke { get; set; }

    /// <summary>Raised when the cursor changes position or the byte under it is modified.</summary>
    public event Action<uint, byte> CursorMoved;

    public uint CursorAddress => _cursor;

    uint _top;          // address of the first visible row (aligned to 16)
    uint _cursor;
    bool _lowNibble;    // false = high nibble, true = low nibble
    bool _asciiZone;    // false = editing in the hex zone, true = in the ASCII column

    readonly Typeface _typeface = new Typeface("Courier New");
    double _charW;
    double _lineH;

    static HexEditor()
    {
        FocusableProperty.OverrideDefaultValue<HexEditor>(true);
        AffectsRender<HexEditor>(ForegroundProperty, AddressBrushProperty, AsciiBrushProperty, CursorBrushProperty);
    }

    #region Public API

    /// <summary>Jumps to an address leaving a few rows of context above.</summary>
    public void GotoAddress(uint addr)
    {
        addr &= AddressMask;

        uint row = addr & ~(uint)(BytesPerRow - 1);
        uint context = 4 * BytesPerRow;
        _top = row > context ? row - context : 0;
        ClampTop();

        SetCursor(addr);
    }

    /// <summary>Redraws the contents and re-emits the cursor position (e.g. after a CPU Step).</summary>
    public void Refresh()
    {
        InvalidateVisual();
        RaiseCursorMoved();
    }

    #endregion

    #region Render

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();

        // Transparent fill so the whole area receives pointer events
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // Without a delegate (Avalonia designer) a sample pattern is drawn
        var peek = Peek ?? (a => (byte)(a ^ (a >> 8)));

        int rows = VisibleRows;
        var hexSb = new StringBuilder(BytesPerRow * 3 + 1);
        var asciiSb = new StringBuilder(BytesPerRow + 2);

        for (int r = 0; r < rows; r++)
        {
            long rowAddr = (long)_top + r * BytesPerRow;
            if (rowAddr > AddressMask)
                break;

            double y = PadY + r * _lineH;

            hexSb.Clear();
            asciiSb.Clear();
            asciiSb.Append('|');

            for (int i = 0; i < BytesPerRow; i++)
            {
                if (i == 8)
                    hexSb.Append(' ');

                byte b = peek((uint)(rowAddr + i));
                hexSb.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0x0F]).Append(' ');
                asciiSb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            asciiSb.Append('|');

            context.DrawText(Format($"{rowAddr:X6}:", AddressBrush), new Point(PadX, y));
            context.DrawText(Format(hexSb.ToString(), Foreground), new Point(PadX + AddrChars * _charW, y));
            context.DrawText(Format(asciiSb.ToString(), AsciiBrush), new Point(PadX + AsciiBarCol * _charW, y));
        }

        if (Peek != null)
            RenderCursor(context, rows);
    }

    void RenderCursor(DrawingContext context, int rows)
    {
        long ofs = (long)_cursor - _top;
        if (ofs < 0 || ofs >= (long)rows * BytesPerRow)
            return;

        int row = (int)(ofs / BytesPerRow);
        int col = (int)(ofs % BytesPerRow);
        double y = PadY + row * _lineH;
        double cellH = _lineH - 1;

        byte val = Peek(_cursor);
        char c = val is >= 0x20 and < 0x7F ? (char)val : '.';

        double hx = PadX + HexCol(col) * _charW;
        double ax = PadX + AsciiCol(col) * _charW;
        var hexRect = new Rect(hx, y, _charW * 2, cellH);
        var asciiRect = new Rect(ax, y, _charW, cellH);

        // The active zone's cell is drawn opaque; its mirror in the other zone, dimmed
        // (the semi-transparent fill lets the already-drawn text show through)
        if (_asciiZone)
        {
            using (context.PushOpacity(0.35))
                context.FillRectangle(CursorBrush, hexRect);

            context.FillRectangle(CursorBrush, asciiRect);
            context.DrawText(Format(c.ToString(), Brushes.White), new Point(ax, y));
        }
        else
        {
            context.FillRectangle(CursorBrush, hexRect);
            context.DrawText(Format($"{val:X2}", Brushes.White), new Point(hx, y));

            // Underline of the active nibble
            double nx = hx + (_lowNibble ? _charW : 0);
            context.FillRectangle(Brushes.White, new Rect(nx, y + cellH - 2, _charW, 2));

            using (context.PushOpacity(0.35))
                context.FillRectangle(CursorBrush, asciiRect);
        }
    }

    FormattedText Format(string text, IBrush brush) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSizePx, brush);

    void EnsureMetrics()
    {
        if (_charW > 0)
            return;

        var ft = Format("0", Brushes.White);
        _charW = ft.WidthIncludingTrailingWhitespace;
        _lineH = Math.Ceiling(ft.Height) + 2;
    }

    int VisibleRows
    {
        get
        {
            EnsureMetrics();
            return Math.Max(1, (int)((Bounds.Height - PadY * 2) / _lineH));
        }
    }

    static int HexCol(int i) => AddrChars + i * 3 + (i >= 8 ? 1 : 0);
    static int AsciiCol(int i) => AsciiBarCol + 1 + i;

    // Cursor and scrolling

    void SetCursor(uint addr)
    {
        _cursor = Math.Min(addr, AddressMask);
        _lowNibble = false;
        EnsureCursorVisible();
        InvalidateVisual();
        RaiseCursorMoved();
    }

    void MoveCursor(long delta) =>
        SetCursor((uint)Math.Clamp(_cursor + delta, 0, AddressMask));

    void EnsureCursorVisible()
    {
        int rows = VisibleRows;
        uint cursorRow = _cursor & ~(uint)(BytesPerRow - 1);

        if (cursorRow < _top)
            _top = cursorRow;
        else if (cursorRow >= _top + (uint)(rows * BytesPerRow))
            _top = cursorRow - (uint)((rows - 1) * BytesPerRow);

        ClampTop();
    }

    void ClampTop()
    {
        long maxTop = (AddressMask + 1L) - (long)VisibleRows * BytesPerRow;
        if (maxTop < 0)
            maxTop = 0;

        if (_top > maxTop)
            _top = (uint)maxTop;

        _top &= ~(uint)(BytesPerRow - 1);
    }

    void ScrollBy(long deltaBytes)
    {
        long maxTop = (AddressMask + 1L) - (long)VisibleRows * BytesPerRow;
        if (maxTop < 0)
            maxTop = 0;

        _top = (uint)(Math.Clamp(_top + deltaBytes, 0, maxTop) & ~(long)(BytesPerRow - 1));
        InvalidateVisual();
    }

    void RaiseCursorMoved()
    {
        if (Peek != null)
            CursorMoved?.Invoke(_cursor, Peek(_cursor));
    }

    // Editing

    void WriteNibble(int digit)
    {
        if (Peek == null || Poke == null)
            return;

        byte cur = Peek(_cursor);
        byte value = _lowNibble
            ? (byte)((cur & 0xF0) | digit)
            : (byte)((digit << 4) | (cur & 0x0F));

        // Non-editable region (ROM, I/O, void): the keystroke is ignored
        if (!Poke(_cursor, value))
            return;

        if (_lowNibble)
        {
            _lowNibble = false;
            MoveCursor(1);
        }
        else
        {
            _lowNibble = true;
            InvalidateVisual();
            RaiseCursorMoved();
        }
    }

    /// <summary>
    /// Writes the code of a character typed in the ASCII zone and advances the cursor.
    /// Returns false if the character is not representable or the region is not editable.
    /// </summary>
    bool WriteChar(char c)
    {
        if (Poke == null)
            return false;

        // Only characters representable in one byte (control characters excluded)
        if (c < 0x20 || c > 0xFF)
            return false;

        if (!Poke(_cursor, (byte)c))
            return false;

        MoveCursor(1);
        return true;
    }

    static int HexDigitFromKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        >= Key.A and <= Key.F => key - Key.A + 10,
        _ => -1
    };

    // Input

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Peek == null)
        {
            base.OnKeyDown(e);
            return;
        }

        int rows = VisibleRows;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Left: MoveCursor(-1); e.Handled = true; return;
            case Key.Right: MoveCursor(1); e.Handled = true; return;
            case Key.Up: MoveCursor(-BytesPerRow); e.Handled = true; return;
            case Key.Down: MoveCursor(BytesPerRow); e.Handled = true; return;
            case Key.PageUp: MoveCursor(-(long)rows * BytesPerRow); e.Handled = true; return;
            case Key.PageDown: MoveCursor((long)rows * BytesPerRow); e.Handled = true; return;

            case Key.Home:
                SetCursor(ctrl ? 0 : _cursor & ~(uint)(BytesPerRow - 1));
                e.Handled = true;
                return;

            case Key.End:
                SetCursor(ctrl ? AddressMask : (_cursor & ~(uint)(BytesPerRow - 1)) + BytesPerRow - 1);
                e.Handled = true;
                return;

            case Key.Tab:
                // Toggles the editing zone between the hex and ASCII columns
                _asciiZone = !_asciiZone;
                _lowNibble = false;
                InvalidateVisual();
                e.Handled = true;
                return;
        }

        // In the ASCII zone characters arrive via OnTextInput; only hex nibbles are edited here
        if (!_asciiZone && !ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            int digit = HexDigitFromKey(e.Key);
            if (digit >= 0)
            {
                WriteNibble(digit);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (Peek == null || !_asciiZone || string.IsNullOrEmpty(e.Text))
        {
            base.OnTextInput(e);
            return;
        }

        foreach (char c in e.Text)
            if (!WriteChar(c))
                break;

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        EnsureMetrics();

        var p = e.GetPosition(this);
        int row = Math.Max(0, (int)((p.Y - PadY) / _lineH));
        int col = (int)((p.X - PadX) / _charW);

        int byteIdx = -1;
        bool low = false;
        bool ascii = false;

        // Hex zone: each byte takes 3 columns (XX + space)
        for (int i = 0; i < BytesPerRow; i++)
        {
            int c = HexCol(i);
            if (col >= c && col < c + 3)
            {
                byteIdx = i;
                low = col > c;
                break;
            }
        }

        // ASCII zone
        if (byteIdx < 0 && col >= AsciiCol(0) && col < AsciiCol(0) + BytesPerRow)
        {
            byteIdx = col - AsciiCol(0);
            ascii = true;
        }

        if (byteIdx < 0)
            return;

        long addr = (long)_top + (long)row * BytesPerRow + byteIdx;
        if (addr > AddressMask)
            return;

        // The click also sets the editing zone (hex or ASCII)
        _asciiZone = ascii;
        SetCursor((uint)addr);

        if (!ascii && low)
        {
            _lowNibble = true;
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        int rowsDelta = (int)Math.Round(-e.Delta.Y * 3);
        if (rowsDelta == 0)
            rowsDelta = e.Delta.Y > 0 ? -1 : 1;

        ScrollBy((long)rowsDelta * BytesPerRow);
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (_charW > 0)
            ClampTop();
    }

    #endregion
}
