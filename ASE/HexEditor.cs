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
/// Monitor de memoria hexadecimal editable. Muestra 16 bytes por fila (dirección, hex y ASCII)
/// sobre el espacio de direcciones de 24 bits del 68000. El contenido se lee y escribe a través
/// de los delegados <see cref="Peek"/> y <see cref="Poke"/>, de modo que el control no sabe nada
/// del bus: el llamante decide qué regiones son visibles/editables.
///
/// Navegación: cursores, RePág/AvPág, Inicio/Fin (Ctrl+Inicio/Fin va al principio/final de la
/// memoria), rueda del ratón y clic sobre un byte (en la zona hex o en la ASCII).
/// </summary>
public class HexEditor : Control
{
    public const uint AddressMask = 0xFFFFFF;   // espacio de direcciones de 24 bits

    const int BytesPerRow = 16;
    const int AddrChars = 8;                    // "AAAAAA: "
    const int AsciiBarCol = AddrChars + BytesPerRow * 3 + 1;    // columna del '|' inicial
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

    /// <summary>Lectura de un byte de memoria (no debe tener efectos laterales).</summary>
    public Func<uint, byte> Peek { get; set; }

    /// <summary>Escritura de un byte de memoria. Devuelve false si la región no es editable.</summary>
    public Func<uint, byte, bool> Poke { get; set; }

    /// <summary>Se dispara cuando el cursor cambia de posición o el byte bajo él se modifica.</summary>
    public event Action<uint, byte> CursorMoved;

    public uint CursorAddress => _cursor;

    uint _top;          // dirección de la primera fila visible (alineada a 16)
    uint _cursor;
    bool _lowNibble;    // false = nibble alto, true = nibble bajo
    bool _asciiZone;    // false = se edita en la zona hex, true = en la columna ASCII

    readonly Typeface _typeface = new Typeface("Courier New");
    double _charW;
    double _lineH;

    static HexEditor()
    {
        FocusableProperty.OverrideDefaultValue<HexEditor>(true);
        AffectsRender<HexEditor>(ForegroundProperty, AddressBrushProperty, AsciiBrushProperty, CursorBrushProperty);
    }

    // -------------------- API pública --------------------

    /// <summary>Salta a una dirección dejando unas filas de contexto por encima.</summary>
    public void GotoAddress(uint addr)
    {
        addr &= AddressMask;

        uint row = addr & ~(uint)(BytesPerRow - 1);
        uint context = 4 * BytesPerRow;
        _top = row > context ? row - context : 0;
        ClampTop();

        SetCursor(addr);
    }

    /// <summary>Redibuja el contenido y reemite la posición del cursor (p.ej. tras un Step de CPU).</summary>
    public void Refresh()
    {
        InvalidateVisual();
        RaiseCursorMoved();
    }

    // -------------------- Render --------------------

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();

        // Relleno transparente para que todo el área reciba eventos de puntero
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // Sin delegado (diseñador de Avalonia) se pinta un patrón de ejemplo
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

        // La celda de la zona activa se pinta opaca; su reflejo en la otra zona, atenuado
        // (el relleno semitransparente deja ver el texto ya dibujado debajo)
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

            // Subrayado del nibble activo
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

    // Cursor y scroll

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

    // Edición

    void WriteNibble(int digit)
    {
        if (Peek == null || Poke == null)
            return;

        byte cur = Peek(_cursor);
        byte value = _lowNibble
            ? (byte)((cur & 0xF0) | digit)
            : (byte)((digit << 4) | (cur & 0x0F));

        // Región no editable (ROM, E/S, void): se ignora la pulsación
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
    /// Escribe el código de un carácter tecleado en la zona ASCII y avanza el cursor.
    /// Devuelve false si el carácter no es representable o la región no es editable.
    /// </summary>
    bool WriteChar(char c)
    {
        if (Poke == null)
            return false;

        // Sólo caracteres representables en un byte (se excluyen los de control)
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

    // Entrada

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
                // Alterna la zona de edición entre las columnas hex y ASCII
                _asciiZone = !_asciiZone;
                _lowNibble = false;
                InvalidateVisual();
                e.Handled = true;
                return;
        }

        // En la zona ASCII los caracteres llegan por OnTextInput; aquí sólo se editan nibbles hex
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

        // Zona hex: cada byte ocupa 3 columnas (XX + espacio)
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

        // Zona ASCII
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

        // El clic también fija la zona de edición (hex o ASCII)
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
}
