using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using TinyDialogsNet;

namespace ASE;

public partial class DebugWindow : Window
{
    public class DasmLine : INotifyPropertyChanged
    {
        public uint Address { get; init; }
        public string DasmCodeLine { get; init; } = "";

        bool _isPC;
        public bool IsPC
        {
            get => _isPC;
            set
            {
                if (_isPC == value) return;
                _isPC = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPC)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Número total de instrucciones desensambladas en cada listado
    const int InstructionsToDisasm = 4096;
    // Ventana en bytes por detrás del ancla usada para sincronizar el desensamblado
    const int BackContextBytes = 1024;
    // Líneas de contexto visibles alrededor de la instrucción actual al hacer scroll
    const int ScrollContextLines = 5;

    public ObservableCollection<DasmLine> DasmListing { get; } = new();

    DasmLine _pcLine;

    public DebugWindow()
    {
        InitializeComponent();
        DataContext = this;

        if (!Design.IsDesignMode)
        {
            // El emulador entra en pausa cuando se entra en depuración. IsPaused solo se
            // comprueba una vez por frame, así que la barrera espera a que el hilo de
            // emulación se aparque de verdad antes de muestrear PC/registros/memoria;
            // sin ella se mostraba un estado de mitad de frame ya desfasado.
            ASEMain.IsPaused = true;
            ASEMain.RunWhilePaused(() => { }, out _);
            BuildListing(CPU._moira.PC);
            UpdateRegisters();

            // Monitor de memoria: accesos de depuración sin efectos laterales sobre el bus
            hexView.Peek = a => ASEMain._mem.DebugPeek8(a);
            hexView.Poke = (a, v) => ASEMain._mem.DebugPoke8(a, v);
            hexView.CursorMoved += (addr, val) => txtMemStatus.Text = $"${addr:X6} = ${val:X2}";
            hexView.GotoAddress(0);
            // El scroll hay que hacerlo cuando el ListBox ya tiene tamaño y ha creado los contenedores
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => ScrollToLine(IndexOfAddress(CPU._moira.PC)), DispatcherPriority.Background);
        }
        else
            BuildDesignListing();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (!Design.IsDesignMode)
        {
            ASEMain.IsPaused = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Design.IsDesignMode && e.Key == Key.F10)
        {
            StepInstruction();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    public void OnStepClick(object sender, RoutedEventArgs e) => StepInstruction();

    public void OnGotoPCClick(object sender, RoutedEventArgs e) => ShowCurrentPC();

    public void OnGotoAddrClick(object sender, RoutedEventArgs e)
    {
        string text = (txtGotoAddr.Text ?? "").Trim().TrimStart('$');

        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
            return;

        addr &= ~1u; // las instrucciones siempre están alineadas a palabra
        BuildListing(addr);
        ScrollToLine(IndexOfAddress(addr));
    }

    public void OnRegisterLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox tb)
            CommitRegister(tb);
    }

    public void OnRegisterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            CommitRegister(tb);
            e.Handled = true;
        }
    }

    public void OnGotoAddrKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnGotoAddrClick(sender, e);
            e.Handled = true;
        }
    }

    public void OnMemGotoClick(object sender, RoutedEventArgs e)
    {
        string text = (txtMemAddr.Text ?? "").Trim().TrimStart('$');

        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
            return;

        hexView.GotoAddress(addr);
        hexView.Focus();
    }

    public void OnMemGotoKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnMemGotoClick(sender, e);
            e.Handled = true;
        }
    }

    public void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        // Proposed inside the configured snapshots directory (same one F11 uses)
        Directory.CreateDirectory(Config.SnapshotsDir);
        var (canceled, path) = TinyDialogs.SaveFileDialog("Save ST snapshot",
            Path.Combine(Config.SnapshotsDir, $"ase_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.snap"),
            new FileFilter("ASE snapshot", ["*.snap"]));

        if (canceled || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            // Estado completo de la máquina (formato ASESNAP2, ver Snapshot.cs);
            // restaurable desde File > Restore snapshot en la ventana principal
            Snapshot.Save(path);
            txtMemStatus.Text = $"Snapshot saved: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            TinyDialogs.MessageBox("Error", $"Could not save snapshot: {ex.Message}",
                MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
        }
    }

    void CommitRegister(TextBox tb)
    {
        if (tb.Tag is not string reg)
            return;

        string text = (tb.Text ?? "").Trim().TrimStart('$');

        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            // Texto no válido: se restaura el valor actual del registro
            UpdateRegisters();
            return;
        }

        switch (reg)
        {
            case "PC":
                JumpTo(value & ~1u);
                ShowCurrentPC();
                return;

            case "SP":
                CPU._moira.SP = value;
                break;

            default:
                int idx = reg[1] - '0';
                var bank = reg[0] == 'A' ? CPU._moira.A : CPU._moira.D;
                bank[idx] = value;
                break;
        }

        UpdateRegisters();
    }

    public void OnSRFlagClick(object sender, RoutedEventArgs e)
    {
        ushort sr = 0;

        if (chkT.IsChecked == true) sr |= 0x8000;
        if (chkS.IsChecked == true) sr |= 0x2000;
        if (chkI2.IsChecked == true) sr |= 0x0400;
        if (chkI1.IsChecked == true) sr |= 0x0200;
        if (chkI0.IsChecked == true) sr |= 0x0100;
        if (chkX.IsChecked == true) sr |= 0x0010;
        if (chkN.IsChecked == true) sr |= 0x0008;
        if (chkZ.IsChecked == true) sr |= 0x0004;
        if (chkV.IsChecked == true) sr |= 0x0002;
        if (chkC.IsChecked == true) sr |= 0x0001;

        CPU._moira.SR = sr;

        // setSR puede conmutar el modo supervisor, lo que intercambia el puntero de pila visible
        UpdateRegisters();
    }

    void StepInstruction()
    {
        CPU._moira.Step();
        ShowCurrentPC();
        // La instrucción ejecutada puede haber modificado la memoria visible en el monitor
        hexView.Refresh();
    }

    /// <summary>
    /// Mueve el PC a una nueva dirección dejando la CPU en un límite de instrucción válido.
    /// En Moira, al inicio de cada instrucción pc == pc0 apuntan a la instrucción actual, IRD
    /// contiene su opcode e IRC la palabra siguiente, así que además de PC/PC0 hay que rellenar
    /// la cola de prefetch (equivale al fullPrefetch interno de Moira).
    /// </summary>
    void JumpTo(uint addr)
    {
        CPU._moira.PC = addr;
        CPU._moira.PC0 = addr;
        CPU._moira.IRD = ASEMain._mem.Read16(addr);
        CPU._moira.IRC = ASEMain._mem.Read16(addr + 2);
    }

    /// <summary>
    /// Resalta la instrucción del PC actual, reconstruyendo el listado si el PC se ha salido de él.
    /// </summary>
    void ShowCurrentPC()
    {
        uint pc = CPU._moira.PC;
        int index = IndexOfAddress(pc);

        if (index < 0)
        {
            BuildListing(pc);
            index = IndexOfAddress(pc);
        }
        else
        {
            if (_pcLine != null)
                _pcLine.IsPC = false;

            _pcLine = DasmListing[index];
            _pcLine.IsPC = true;
        }

        UpdateRegisters();
        ScrollToLine(index);
    }

    /// <summary>
    /// Reconstruye el listado desensamblando hacia delante desde una dirección sincronizada
    /// con <paramref name="anchor"/>, de modo que el ancla siempre aparece como una línea del listado.
    /// </summary>
    void BuildListing(uint anchor)
    {
        anchor &= ~1u;
        uint start = FindSyncStart(anchor);
        uint pc = CPU._moira.PC;

        DasmListing.Clear();
        _pcLine = null;

        uint addr = start;

        for (int i = 0; i < InstructionsToDisasm; i++)
        {
            var (disStr, disSize) = CPU._moira.Disassemble(addr, 250);

            var data = new StringBuilder(32);
            for (uint x = 0; x < disSize; x += 2)
                data.Append($"{ASEMain._mem.Read16(addr + x):X4} ");

            var line = new DasmLine
            {
                Address = addr,
                DasmCodeLine = $"{addr:X8} {data.ToString().PadRight(25)} {disStr}"
            };

            if (addr == pc)
            {
                line.IsPC = true;
                _pcLine = line;
            }

            DasmListing.Add(line);
            addr += (uint)disSize;
        }
    }

    /// <summary>
    /// Busca la dirección más lejana dentro de la ventana anterior al ancla desde la que un
    /// desensamblado hacia delante cae exactamente en el ancla. Como las instrucciones del 68000
    /// tienen longitud variable, esto garantiza que el listado quede alineado con la instrucción
    /// del ancla (normalmente el PC, que siempre es un límite de instrucción válido).
    /// </summary>
    uint FindSyncStart(uint anchor)
    {
        uint windowStart = anchor > BackContextBytes ? anchor - BackContextBytes : 0;
        int slots = (int)((anchor - windowStart) / 2);

        if (slots <= 0)
            return anchor;

        // Tamaño de la instrucción en cada dirección par de la ventana
        var sizeInWords = new int[slots];
        for (int i = 0; i < slots; i++)
        {
            var (_, disSize) = CPU._moira.Disassemble(windowStart + (uint)i * 2, 250);
            sizeInWords[i] = disSize / 2;
        }

        // reachable[i] == true si desensamblando desde windowStart + i*2 se llega justo al ancla
        var reachable = new bool[slots + 1];
        reachable[slots] = true;

        for (int i = slots - 1; i >= 0; i--)
        {
            int next = i + sizeInWords[i];
            reachable[i] = next <= slots && reachable[next];
        }

        for (int i = 0; i < slots; i++)
            if (reachable[i])
                return windowStart + (uint)i * 2;

        return anchor;
    }

    /// <summary>Búsqueda binaria de una dirección en el listado (las direcciones van en orden ascendente).</summary>
    int IndexOfAddress(uint addr)
    {
        int lo = 0, hi = DasmListing.Count - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            uint a = DasmListing[mid].Address;

            if (a == addr) return mid;
            if (a < addr) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }

    /// <summary>Desplaza el listado dejando la línea visible con algo de contexto por arriba y por abajo.</summary>
    void ScrollToLine(int index)
    {
        if (index < 0 || DasmListing.Count == 0)
            return;

        lstListing.SelectedIndex = index;
        lstListing.ScrollIntoView(Math.Max(index - ScrollContextLines, 0));
        lstListing.ScrollIntoView(Math.Min(index + ScrollContextLines, DasmListing.Count - 1));
        lstListing.ScrollIntoView(index);
    }

    void UpdateRegisters()
    {
        txtPC.Text = $"{CPU._moira.PC:X8}";
        txtSP.Text = $"{CPU._moira.SP:X8}";
        txtA0.Text = $"{CPU._moira.A[0]:X8}";
        txtD0.Text = $"{CPU._moira.D[0]:X8}";
        txtA1.Text = $"{CPU._moira.A[1]:X8}";
        txtD1.Text = $"{CPU._moira.D[1]:X8}";
        txtA2.Text = $"{CPU._moira.A[2]:X8}";
        txtD2.Text = $"{CPU._moira.D[2]:X8}";
        txtA3.Text = $"{CPU._moira.A[3]:X8}";
        txtD3.Text = $"{CPU._moira.D[3]:X8}";
        txtA4.Text = $"{CPU._moira.A[4]:X8}";
        txtD4.Text = $"{CPU._moira.D[4]:X8}";
        txtA5.Text = $"{CPU._moira.A[5]:X8}";
        txtD5.Text = $"{CPU._moira.D[5]:X8}";
        txtA6.Text = $"{CPU._moira.A[6]:X8}";
        txtD6.Text = $"{CPU._moira.D[6]:X8}";
        txtD7.Text = $"{CPU._moira.D[7]:X8}";

        ushort sr = CPU._moira.SR;
        txtSRValue.Text = $"${sr:X4}";
        chkT.IsChecked = (sr & 0x8000) != 0;
        chkS.IsChecked = (sr & 0x2000) != 0;
        chkI2.IsChecked = (sr & 0x0400) != 0;
        chkI1.IsChecked = (sr & 0x0200) != 0;
        chkI0.IsChecked = (sr & 0x0100) != 0;
        chkX.IsChecked = (sr & 0x0010) != 0;
        chkN.IsChecked = (sr & 0x0008) != 0;
        chkZ.IsChecked = (sr & 0x0004) != 0;
        chkV.IsChecked = (sr & 0x0002) != 0;
        chkC.IsChecked = (sr & 0x0001) != 0;
    }

    /// <summary>
    /// Este método sólo existe para que pueda ver cómo queda el diseño en el diseñador de Avalonia en Visual Studio
    /// </summary>
    void BuildDesignListing()
    {
        DasmListing.Clear();

        uint lineAddr = 0xC000000;

        for (int i = 0; i < 64; i++)
        {
            DasmListing.Add(new DasmLine
            {
                Address = (uint)(lineAddr + i * 2),
                IsPC = (i == 32),
                DasmCodeLine = $"{lineAddr + i * 2:X8} 1A1B 1C1D 1E1F             move.l #$FFFF0000,(a1)"
            });
        }
    }
}
