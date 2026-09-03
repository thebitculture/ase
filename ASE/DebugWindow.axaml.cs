using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
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

        bool _isBreakpoint;
        public bool IsBreakpoint
        {
            get => _isBreakpoint;
            set
            {
                if (_isBreakpoint == value) return;
                _isBreakpoint = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBreakpoint)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Total number of instructions disassembled in each listing
    const int InstructionsToDisasm = 4096;
    // Window in bytes behind the anchor used to synchronize the disassembly
    const int BackContextBytes = 1024;
    // Visible context lines around the current instruction when scrolling
    const int ScrollContextLines = 5;

    public ObservableCollection<DasmLine> DasmListing { get; } = new();

    DasmLine _pcLine;

    /// <summary>
    /// Address of the breakpoint planted by "Run to this line", if it is still armed.
    /// Static because the window is destroyed while the machine runs and a new one is
    /// built on every stop (see ASEMain.EmulatorLoop). It is null for as long as the
    /// window is open: the constructor clears it, and setting it closes the window.
    /// </summary>
    static uint? _tempBreakpoint;

    public DebugWindow()
    {
        InitializeComponent();
        DataContext = this;

        if (!Design.IsDesignMode)
        {
            // The emulator pauses when entering the debugger. IsPaused is only checked
            // once per frame, so the barrier waits until the emulation thread is truly
            // parked before sampling PC/registers/memory; without it a stale mid-frame
            // state was displayed.
            ASEMain.EnterUiPause();
            ASEMain.RunWhilePaused(() => { }, out _);
            ClearTemporaryBreakpoint();
            BuildListing(CPU._moira.PC);
            UpdateRegisters();
            UpdateBreakpointControls();

            // Memory monitor: debug accesses with no side effects on the bus
            hexView.Peek = a => ASEMain._mem.DebugPeek8(a);
            hexView.Poke = (a, v) => ASEMain._mem.DebugPoke8(a, v);
            hexView.CursorMoved += (addr, val) => txtMemStatus.Text = $"${addr:X6} = ${val:X2}";
            hexView.GotoAddress(0);

            // Bitmap explorer: needs the memory monitor above it (the "From memory cursor"
            // button reads hexView.CursorAddress) and the machine already parked.
            InitBitmapExplorer();
            // Scrolling must happen once the ListBox has a size and has created its containers
            Opened += (_, _) => Dispatcher.UIThread.Post(
                () => ScrollToLine(IndexOfAddress(CPU._moira.PC)), DispatcherPriority.Background);
        }
        else
            BuildDesignListing();
    }

    public void OnContinueClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (!Design.IsDesignMode)
        {
            _bitmapSurface?.Dispose();
            _bitmapSurface = null;
            ASEMain.ExitUiPause();
        }
    }

    public void OnSaveListingClick(object sender, RoutedEventArgs e)
    {
        SaveListingWindow window = new SaveListingWindow();
        window.ShowDialog(this);
    }

    /// <summary>
    /// Sets or removes a breakpoint on the selected instruction. The button has already
    /// changed its state by the time Click arrives, so IsChecked reflects the desired state.
    /// </summary>
    public void OnBreakpointToggle(object sender, RoutedEventArgs e)
    {
        if (lstListing.SelectedItem is not DasmLine line)
        {
            tglBreakpoint.IsChecked = false;
            return;
        }

        ToggleBreakpoint(line);
        UpdateBreakpointControls();
    }

    public void OnClearBreakpointClick(object sender, RoutedEventArgs e)
    {
        CPU._moira.RemoveAllBreakpoints();

        // The GEMDOS hard drive rides on breakpoints of its own inside its cartridge code:
        // they are wiring, not the user's, so they go straight back (see GemdosHD).
        GemdosHD.RearmHooks();

        // The red marks in the listing are copies of Moira's state: they must be cleared too
        foreach (var line in DasmListing)
            line.IsBreakpoint = false;

        // Unchecks the toggle if the selected line had a breakpoint and disables "Run until breakpoint"
        UpdateBreakpointControls();
    }

    /// <summary>
    /// Right-clicking selects the line under the pointer, so the context menu always acts
    /// on what was pointed at (and the Breakpoint button, which follows the selection, agrees
    /// with it). The menu entries themselves read <c>lstListing.SelectedItem</c>.
    /// </summary>
    public void OnListingContextRequested(object sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control c &&
            c.FindAncestorOfType<ListBoxItem>(true) is { DataContext: DasmLine line })
            lstListing.SelectedItem = line;
    }

    /// <summary>Context menu: sets or clears a breakpoint on the selected line.</summary>
    public void OnContextToggleBreakpointClick(object sender, RoutedEventArgs e)
    {
        if (lstListing.SelectedItem is not DasmLine line)
            return;

        ToggleBreakpoint(line);
        UpdateBreakpointControls();
    }

    /// <summary>
    /// Context menu: resumes emulation until the selected line is reached. Moira has no
    /// one-shot breakpoints, so a normal one is planted and remembered in
    /// <see cref="_tempBreakpoint"/>; the next time the machine stops (this line, another
    /// breakpoint or a manual open of the debugger) the constructor removes it again.
    /// A line the user had already guarded is left alone — that breakpoint is not ours.
    /// </summary>
    public void OnRunToLineClick(object sender, RoutedEventArgs e)
    {
        if (lstListing.SelectedItem is not DasmLine line)
            return;

        if (!CPU._moira.IsBreakpoint(line.Address))
        {
            CPU._moira.SetBreakpoint(line.Address);
            _tempBreakpoint = line.Address;
        }

        // Closing resumes the machine; breakpoints are always armed (see OnRunToBreakpointClick)
        Close();
    }

    /// <summary>
    /// Context menu: moves the PC to the selected line without executing anything in between.
    /// </summary>
    public void OnSetPcToLineClick(object sender, RoutedEventArgs e)
    {
        if (lstListing.SelectedItem is not DasmLine line)
            return;

        JumpTo(line.Address);
        ShowCurrentPC();
    }

    /// <summary>
    /// Sets or clears the breakpoint on a line, both in Moira and in the listing's red mark.
    /// The decision is taken from Moira's own state so the button and the context menu agree
    /// whatever the visual state of either was.
    /// </summary>
    void ToggleBreakpoint(DasmLine line)
    {
        // The GEMDOS hard drive's hooks are not breakpoints the user may play with: clearing
        // one leaves its cartridge branching on undefined flags.
        if (GemdosHD.IsHookAddress(line.Address))
            return;

        bool wasSet = CPU._moira.IsBreakpoint(line.Address);

        if (wasSet)
            CPU._moira.RemoveBreakpoint(line.Address);
        else
            CPU._moira.SetBreakpoint(line.Address);

        line.IsBreakpoint = !wasSet;
    }

    /// <summary>
    /// Removes the breakpoint left behind by "Run to this line". Called when the debugger
    /// opens, which is what makes it last exactly until the machine stops again.
    /// </summary>
    static void ClearTemporaryBreakpoint()
    {
        if (_tempBreakpoint is not uint addr)
            return;

        CPU._moira.RemoveBreakpoint(addr);
        _tempBreakpoint = null;
    }

    /// <summary>
    /// Resumes emulation until the next breakpoint. Closing the window is enough (it
    /// unpauses in OnClosing): breakpoints are always armed inside Moira and, when one
    /// is hit, the emulation loop pauses the machine and reopens this window with the
    /// PC on the stopped instruction (see ASEMain.EmulatorLoop).
    /// </summary>
    public void OnRunToBreakpointClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>The breakpoint ToggleButton follows the selected line in the listing.</summary>
    public void OnListingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Design.IsDesignMode)
            return;

        UpdateBreakpointControls();
    }

    void UpdateBreakpointControls()
    {
        var line = lstListing.SelectedItem as DasmLine;
        bool hook = line != null && GemdosHD.IsHookAddress(line.Address);

        tglBreakpoint.IsEnabled = line != null && !hook;
        tglBreakpoint.IsChecked = line != null && !hook && CPU._moira.IsBreakpoint(line.Address);

        // The hard drive's own hooks don't count: with only those armed there is nothing
        // for "Run until breakpoint" to stop at.
        btnRunToBp.IsEnabled = CPU._moira.BreakpointCount > GemdosHD.HookBreakpointCount;
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

        addr &= ~1u; // instructions are always word-aligned
        BuildListing(addr);
        ScrollToLine(IndexOfAddress(addr));
    }

    public void OnRegisterLostFocus(object sender, FocusChangedEventArgs e)
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
            // Full machine state (ASESNAP2 format, see Snapshot.cs);
            // restorable from File > Restore snapshot in the main window
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
            // Invalid text: restore the register's current value
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

        // setSR can toggle supervisor mode, which swaps the visible stack pointer
        UpdateRegisters();
    }

    void StepInstruction()
    {
        CPU._moira.Step();
        ShowCurrentPC();
        // The executed instruction may have modified memory visible in the monitor
        hexView.Refresh();
    }

    /// <summary>
    /// Moves the PC to a new address leaving the CPU at a valid instruction boundary.
    /// In Moira, at the start of each instruction pc == pc0 point to the current instruction,
    /// IRD holds its opcode and IRC the next word, so besides PC/PC0 the prefetch queue must
    /// be filled too (equivalent to Moira's internal fullPrefetch).
    /// </summary>
    void JumpTo(uint addr)
    {
        CPU._moira.PC = addr;
        CPU._moira.PC0 = addr;
        CPU._moira.IRD = ASEMain._mem.Read16(addr);
        CPU._moira.IRC = ASEMain._mem.Read16(addr + 2);
    }

    /// <summary>
    /// Highlights the instruction at the current PC, rebuilding the listing if the PC has left it.
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
    /// Rebuilds the listing by disassembling forward from an address synchronized with
    /// <paramref name="anchor"/>, so the anchor always shows up as a line of the listing.
    /// </summary>
    void BuildListing(uint anchor)
    {
        anchor &= ~1u;
        uint start = FindSyncStart(anchor);
        uint pc = CPU._moira.PC;

        // Breakpoints live in Moira (they persist across window openings); addresses are
        // only queried one by one if any is set.
        bool anyBreakpoints = CPU._moira.BreakpointCount > 0;

        DasmListing.Clear();
        _pcLine = null;

        uint addr = start;

        // Disassembling reads memory through the normal bus (Moira's read16Dasm does), so a
        // listing that wanders into undecoded space would schedule a bus error the machine
        // would take on resume. These reads are the debugger's, not the CPU's.
        ASEMain._mem.ReadWithoutBusErrors(() =>
        {
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

                // The GEMDOS hard drive's hooks are internal wiring: shown as ordinary code
                line.IsBreakpoint = anyBreakpoints && CPU._moira.IsBreakpoint(addr) && !GemdosHD.IsHookAddress(addr);

                DasmListing.Add(line);
                addr += (uint)disSize;
            }

            return true;
        });
    }

    /// <summary>
    /// Finds the farthest address within the window before the anchor from which a forward
    /// disassembly lands exactly on the anchor. Since 68000 instructions have variable
    /// length, this guarantees the listing stays aligned with the anchor's instruction
    /// (normally the PC, which is always a valid instruction boundary).
    /// </summary>
    uint FindSyncStart(uint anchor)
    {
        uint windowStart = anchor > BackContextBytes ? anchor - BackContextBytes : 0;
        int slots = (int)((anchor - windowStart) / 2);

        if (slots <= 0)
            return anchor;

        // Instruction size at each even address of the window
        var sizeInWords = new int[slots];
        for (int i = 0; i < slots; i++)
        {
            var (_, disSize) = CPU._moira.Disassemble(windowStart + (uint)i * 2, 250);
            sizeInWords[i] = disSize / 2;
        }

        // reachable[i] == true if disassembling from windowStart + i*2 lands exactly on the anchor
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

    /// <summary>Binary search for an address in the listing (addresses are in ascending order).</summary>
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

    /// <summary>Scrolls the listing so the line is visible with some context above and below.</summary>
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
    /// This method only exists so the layout can be previewed in the Avalonia designer in Visual Studio
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
                IsBreakpoint = (i == 32 || i == 20),
                DasmCodeLine = $"{lineAddr + i * 2:X8} 1A1B 1C1D 1E1F             move.l #$FFFF0000,(a1)"
            });
        }
    }

    // ------------------------------------------------------------------------------------
    // Bitmap explorer
    //
    // Paints a stretch of RAM as if the shifter were fetching it: bytes are read as Atari
    // bitplanes (planes interleaved word by word, most significant bit leftmost) and coloured
    // with the machine's live palette registers. It is a way of *finding* graphics — a sprite
    // sheet only lines up when the bytes-per-line matches its real width, so the picture
    // snapping into place is what tells you the format is right.
    //
    // The machine is frozen for as long as the debugger is open (EnterUiPause in the
    // constructor), so a render is a snapshot: nothing behind it changes until the user
    // continues, and there is no need to poll.
    // ------------------------------------------------------------------------------------

    /// <summary>Colour registers on the shifter ($FF8240-$FF825F).</summary>
    const int PaletteEntries = 16;

    /// <summary>
    /// False until the controls exist and the machine has been sampled. The NumericUpDowns
    /// raise ValueChanged while the XAML is being loaded, long before the constructor has
    /// reached its own initialization, and a render at that point would dereference nulls.
    /// </summary>
    bool _bitmapReady;

    /// <summary>
    /// The bitmap currently shown, kept so it can be exported and disposed. It is built at the
    /// ST's own resolution (one texel per ST pixel) and magnified by the Image's Width/Height,
    /// so the zoom costs no memory and the PNG export is 1:1 without a second render.
    /// </summary>
    WriteableBitmap _bitmapSurface;

    readonly Border[] _palSwatches = new Border[PaletteEntries];
    readonly ushort[] _palRaw = new ushort[PaletteEntries];

    // Geometry of the dump currently on screen. The pointer readout reads these rather than
    // recomputing them from the controls, so what it names can never disagree with what was
    // actually drawn — the column count in particular depends on the viewport, not on a control.
    int _bmpRows, _bmpCols, _bmpColWidth, _bmpLineBytes, _bmpPlanes, _bmpZoom;
    uint _bmpStart;

    void InitBitmapExplorer()
    {
        // Past the configured RAM the bus reads back zeros, so there is nothing to look at:
        // the spinner stops where the machine's memory does.
        numBmpAddr.Maximum = Math.Max(0, ASEMain._mem.RamSize - 1);

        for (int i = 0; i < PaletteEntries; i++)
        {
            var swatch = new Border
            {
                Height = 15,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Black
            };

            _palSwatches[i] = swatch;
            palStrip.Children.Add(swatch);
        }

        // The wheel walks the dump, which is the gesture the whole tab is built around. The
        // handler has to tunnel: the ScrollViewer marks the event handled in its own
        // OnPointerWheelChanged, so a bubbling handler would never see it.
        bmpScroll.AddHandler(InputElement.PointerWheelChangedEvent, OnBitmapWheel, RoutingStrategies.Tunnel);
        bmpScroll.PointerMoved += OnBitmapPointerMoved;

        _bitmapReady = true;

        // First address written from here, not from the XAML: NumericUpDown only runs the
        // converter over a value that CHANGES, so a value declared in the markup leaves the
        // box empty — which is how a converted spinner presents when it looks broken.
        numBmpAddr.Value = 0;

        // ValueChanged has normally rendered already by now; the explicit call covers the
        // case where the value was 0 to begin with and nothing was raised.
        RenderBitmap();
    }

    /// <summary>Bytes one line of the dump consumes: the width in bytes, once per bitplane.</summary>
    int BitmapLineBytes()
        => (int)(numBmpBytes.Value ?? 40) * (int)(numBmpPlanes.Value ?? 4);

    /// <summary>
    /// Rebuilds the whole visible dump. Rows are chosen to fill the viewport at the current
    /// zoom, so the picture always reaches the bottom edge and nothing hides below the fold —
    /// which is why only the horizontal axis has a scrollbar.
    /// </summary>
    void RenderBitmap()
    {
        if (!_bitmapReady)
            return;

        int bytesWide = (int)(numBmpBytes.Value ?? 40);
        int planes = (int)(numBmpPlanes.Value ?? 4);
        int zoom = (int)(numBmpZoom.Value ?? 4);
        uint start = (uint)(numBmpAddr.Value ?? 0);

        int lineBytes = bytesWide * planes;

        // The spinner's step is one line of the picture being shown, so holding the arrow
        // scrolls the dump instead of tearing it: a sprite sheet keeps its phase.
        if (numBmpAddr.Increment != lineBytes)
            numBmpAddr.Increment = lineBytes;

        // Width of one strip of the dump. With wrap on, several of them sit side by side.
        int colWidth = bytesWide * 8;

        // Viewport, not Bounds: a horizontal scrollbar eats into the height from the inside and
        // Bounds would keep counting the rows it covers.
        double viewH = bmpScroll.Viewport.Height > 0 ? bmpScroll.Viewport.Height : bmpScroll.Bounds.Height;
        double viewW = bmpScroll.Viewport.Width > 0 ? bmpScroll.Viewport.Width : bmpScroll.Bounds.Width;

        int rows = Math.Max(1, (int)(viewH / zoom));

        // A strip wider than the pane still gets a single column and the horizontal scrollbar;
        // wrapping only ever adds columns that actually fit.
        int cols = tglBmpWrap.IsChecked == true
                 ? Math.Max(1, (int)(viewW / ((double)colWidth * zoom)))
                 : 1;

        int width = colWidth * cols;

        uint[] pal = ReadActivePalette(planes);
        UpdatePaletteStrip(pal, planes);

        var mem = ASEMain._mem;
        uint[] pixels = new uint[width * rows];

        // Planes are interleaved in WORD units, which is how the shifter fetches them: plane 0's
        // word, plane 1's word, and so on, then the next 16 pixels. A width with an odd byte
        // count cannot fill its last unit, so that byte is stored on its own, one per plane —
        // the layout is not a real ST one, but it keeps the line exactly lineBytes long so the
        // dump stays in phase instead of drifting a byte per row.
        int fullUnits = bytesWide >> 1;
        int tailBase = fullUnits * planes * 2;

        // The line is fetched once and then decoded out of this buffer: going through
        // DebugPeek8 per plane per pixel repeats every byte eight times, and at zoom 1 a
        // full-width dump is over a million reads — enough to make dragging a spinner stutter.
        byte[] lineBuf = new byte[lineBytes];

        // Column by column: memory runs down one strip and carries on at the top of the next,
        // so a run of sprites fills the pane instead of trailing off the bottom of a single
        // strip — which is what turns it into a sheet. With wrap off there is exactly one
        // column and this is the plain top-to-bottom walk it replaces.
        for (int c = 0; c < cols; c++)
        {
            uint colBase = start + (uint)(c * rows * lineBytes);
            int colX = c * colWidth;

            for (int y = 0; y < rows; y++)
            {
                uint lineBase = colBase + (uint)(y * lineBytes);
                int rowBase = y * width + colX;

                // DebugPeek8, not Read8: this walks wherever the user points it, and the ordinary
                // bus path would schedule bus errors the parked machine takes on resume (the same
                // reason the renderer fetches through ReadVideoWord — see Video.cs).
                for (int i = 0; i < lineBytes; i++)
                    lineBuf[i] = mem.DebugPeek8(lineBase + (uint)i);

                for (int x = 0; x < colWidth; x++)
                {
                    int b = x >> 3;          // byte of the plane this pixel lives in
                    int bit = 7 - (x & 7);   // leftmost pixel is the most significant bit
                    int unit = b >> 1;
                    bool whole = unit < fullUnits;
                    int baseOff = whole ? (unit * planes) * 2 + (b & 1) : tailBase;
                    int planeStride = whole ? 2 : 1;

                    int idx = 0;
                    for (int p = 0; p < planes; p++)
                        idx |= ((lineBuf[baseOff + p * planeStride] >> bit) & 1) << p;

                    pixels[rowBase + x] = pal[idx];
                }
            }
        }

        // StColorToArgb8888 lays the components out the way the emulator's framebuffer does
        // (0xAABBGGRR), which is Rgba8888 in memory — the same pairing ASEMain's screenshot uses.
        var bmp = new WriteableBitmap(new PixelSize(width, rows), new Vector(96, 96),
                                      PixelFormats.Rgba8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            byte[] row = new byte[width * 4];
            for (int y = 0; y < rows; y++)
            {
                Buffer.BlockCopy(pixels, y * width * 4, row, 0, row.Length);
                Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
        }

        PixelImage.Source = bmp;
        PixelImage.Width = width * zoom;
        PixelImage.Height = rows * zoom;

        // Swapped in before the old one goes, so the Image never holds a disposed bitmap.
        _bitmapSurface?.Dispose();
        _bitmapSurface = bmp;

        _bmpRows = rows;
        _bmpCols = cols;
        _bmpColWidth = colWidth;
        _bmpLineBytes = lineBytes;
        _bmpPlanes = planes;
        _bmpZoom = zoom;
        _bmpStart = start;

        uint last = start + (uint)(cols * rows * lineBytes) - 1;
        string shape = cols > 1 ? $"{cols} cols of {colWidth}x{rows} px" : $"{colWidth}x{rows} px";
        txtBmpInfo.Text = $"${start:X6} - ${last:X6}\n{lineBytes} bytes/line, {shape}, {1 << planes} colours";
        txtBmpStatus.Text = $"${start:X6}";
    }

    /// <summary>
    /// The machine's live colour registers, converted with the same routine the renderer uses
    /// (so an STE gives its 4096-colour reading and an ST its 512-colour one).
    /// </summary>
    uint[] ReadActivePalette(int planes)
    {
        var mem = ASEMain._mem;
        uint[] pal = new uint[PaletteEntries];

        for (int i = 0; i < PaletteEntries; i++)
        {
            uint reg = Memory.STPortAdress.ST_PALLETE + (uint)(i * 2);
            _palRaw[i] = (ushort)((mem.DebugPeek8(reg) << 8) | mem.DebugPeek8(reg + 1));
            pal[i] = Video.AtariStRenderer.StColorToArgb8888(_palRaw[i]);
        }

        // High resolution never runs the registers through the DAC: register 0 only picks normal
        // vs reverse video and the rest keep whatever colour a previous mode left in them (see
        // Video.BlitLineMono). A 1-plane dump on an SM124 is therefore black and white, not the
        // leftover pair the registers would otherwise paint it in.
        if (VideoTiming.Mono && planes == 1)
        {
            bool white = (_palRaw[0] & 0x0FFF) != 0;
            pal[0] = white ? 0xFFFFFFFFu : 0xFF000000u;
            pal[1] = white ? 0xFF000000u : 0xFFFFFFFFu;
        }

        return pal;
    }

    /// <summary>
    /// Repaints the 16 swatches. Entries the current bitplane count cannot reach are dimmed, so
    /// it is plain which part of the palette the dump is actually addressing.
    /// </summary>
    void UpdatePaletteStrip(uint[] pal, int planes)
    {
        int used = 1 << planes;

        for (int i = 0; i < PaletteEntries; i++)
        {
            // The colours the dump is actually painted with, so the monochrome override above
            // shows up here too instead of the leftover registers behind it.
            uint c = pal[i];
            // 0xAABBGGRR, see RenderBitmap: red is the low byte.
            _palSwatches[i].Background = new SolidColorBrush(
                Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16)));
            _palSwatches[i].Opacity = i < used ? 1.0 : 0.25;
            ToolTip.SetTip(_palSwatches[i], $"{i}: ${_palRaw[i]:X4}");
        }
    }

    /// <summary>Moves the start address by a number of bytes, clamped to the spinner's range.</summary>
    void ScrollBitmapAddress(int bytes)
    {
        decimal value = (numBmpAddr.Value ?? 0) + bytes;
        numBmpAddr.Value = Math.Clamp(value, numBmpAddr.Minimum, numBmpAddr.Maximum);
    }

    public void OnBitmapParamChanged(object sender, NumericUpDownValueChangedEventArgs e)
    {
        RenderBitmap();
    }

    public void OnBitmapViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The height decides how many rows fit and, when wrapping, the width decides how many
        // columns do.
        if (e.HeightChanged || e.WidthChanged)
            RenderBitmap();
    }

    public void OnBitmapWrapChanged(object sender, RoutedEventArgs e)
    {
        RenderBitmap();
    }

    /// <summary>
    /// Wheel up walks BACKWARDS through memory, so the picture moves the way the wheel does:
    /// the dump scrolls like any other document, and the address behind it follows. Reading it
    /// the other way round (wheel up = higher address) is defensible on a strip of memory, but
    /// it means pushing the wheel away to make the content come down, and that is confusing.
    ///
    /// Ctrl is the reason the tab is usable at all on real game data: a sprite sheet almost
    /// never begins on a boundary of the line width being drawn, and a byte at a time is what
    /// rolls it into phase — with only whole lines, a sheet sitting two bytes further on can
    /// never be brought into view. Shift is the same gesture eight times faster, for crossing
    /// a bank of memory looking for something that resolves into a picture.
    /// </summary>
    void OnBitmapWheel(object sender, PointerWheelEventArgs e)
    {
        if (!_bitmapReady)
            return;

        int dir = Math.Sign(e.Delta.Y);
        if (dir == 0)
            return;

        int step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1
                 : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? BitmapLineBytes() * 8
                 : BitmapLineBytes();

        ScrollBitmapAddress(-dir * step);
        e.Handled = true;
    }

    /// <summary>
    /// Reports the address under the pointer. This is the point of the whole tab: you spot a
    /// sprite, put the mouse on its top-left corner and the status pill names the byte it
    /// starts at.
    /// </summary>
    void OnBitmapPointerMoved(object sender, PointerEventArgs e)
    {
        if (!_bitmapReady || _bitmapSurface == null)
            return;

        var pos = e.GetPosition(PixelImage);
        int px = (int)(pos.X / _bmpZoom);
        int py = (int)(pos.Y / _bmpZoom);

        if (px < 0 || py < 0 || px >= _bmpColWidth * _bmpCols || py >= _bmpRows)
            return;

        // Which strip the pointer is over, and how far into it. Each column consumed a whole
        // pane's worth of lines before this one started.
        int col = px / _bmpColWidth;
        int cx = px - col * _bmpColWidth;

        // Byte holding this pixel in plane 0 — the address a sprite ripper wants.
        int b = cx >> 3;
        int unit = b >> 1;
        int fullUnits = (_bmpColWidth >> 3) >> 1;
        int off = unit < fullUnits ? (unit * _bmpPlanes) * 2 + (b & 1) : fullUnits * _bmpPlanes * 2;

        uint line = _bmpStart + (uint)((col * _bmpRows + py) * _bmpLineBytes);
        txtBmpStatus.Text = $"x{cx,4} y{py,4}  line ${line:X6}  byte ${line + (uint)off:X6}";
    }

    public void OnBitmapScreenBaseClick(object sender, RoutedEventArgs e)
    {
        var mem = ASEMain._mem;
        bool isSTE = Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE;

        // The video BASE registers, not the live counter: this is where the frame starts.
        // The low byte only exists on an STE, and its bit 0 is not part of the address.
        uint screen = ((uint)mem.DebugPeek8(Memory.STPortAdress.ST_SCRHIGHADDR) << 16)
                    | ((uint)mem.DebugPeek8(Memory.STPortAdress.ST_SCRMIDADDR) << 8)
                    | (isSTE ? (uint)(mem.DebugPeek8(Memory.STPortAdress.ST_SCRLOWADDR) & 0xFE) : 0u);

        numBmpAddr.Value = Math.Clamp((decimal)screen, numBmpAddr.Minimum, numBmpAddr.Maximum);
    }

    public void OnBitmapMemoryCursorClick(object sender, RoutedEventArgs e)
    {
        numBmpAddr.Value = Math.Clamp((decimal)hexView.CursorAddress, numBmpAddr.Minimum, numBmpAddr.Maximum);
    }

    /// <summary>
    /// Writes the visible dump as a PNG at 1:1 — one file pixel per ST pixel, so the sprites can
    /// be cut out of it in a paint program without undoing the zoom.
    /// </summary>
    public void OnBitmapExportClick(object sender, RoutedEventArgs e)
    {
        if (_bitmapSurface == null)
            return;

        uint start = (uint)(numBmpAddr.Value ?? 0);

        // Proposed inside the configured screenshots directory, next to the F11 captures
        Directory.CreateDirectory(Config.ScreenshotsDir);
        var (canceled, path) = TinyDialogs.SaveFileDialog("Export bitmap dump",
            Path.Combine(Config.ScreenshotsDir, $"ase_bitmap_{start:X6}.png"),
            new FileFilter("PNG image", ["*.png"]));

        if (canceled || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            _bitmapSurface.Save(path, PngBitmapEncoderOptions.Default);
            txtBmpStatus.Text = $"Saved: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            TinyDialogs.MessageBox("Error", $"Could not export the bitmap: {ex.Message}",
                MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
        }
    }
}
