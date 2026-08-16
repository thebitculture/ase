using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        tglBreakpoint.IsEnabled = line != null;
        tglBreakpoint.IsChecked = line != null && CPU._moira.IsBreakpoint(line.Address);
        btnRunToBp.IsEnabled = CPU._moira.BreakpointCount > 0;
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

            line.IsBreakpoint = anyBreakpoints && CPU._moira.IsBreakpoint(addr);

            DasmListing.Add(line);
            addr += (uint)disSize;
        }
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
}
