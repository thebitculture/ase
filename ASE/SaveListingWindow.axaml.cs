using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TinyDialogsNet;

namespace ASE;

/// <summary>
/// Dumps the disassembly to a plain-text, tab-separated file that a 68k assembler can
/// re-assemble. Opened as a modal dialog from the Debug window, so the emulator is already
/// parked (see <see cref="DebugWindow"/>) and CPU/memory can be sampled directly here.
///
/// Each code line is emitted as   [empty label] TAB mnemonic TAB operands TAB ; $addr: bytes
/// so the assembler sees a leading-whitespace source line (no label), the operation and its
/// operands as separate fields, and the original address/opcode words as a trailing comment.
///
/// Any instruction that references an address landing on another instruction inside the
/// listing gets that address turned into a label (<c>Lxxxxxxxx:</c>, on its own line above the
/// target), and its operand rewritten to reference the label — so the cross-references survive
/// re-assembly even after the code is edited and moved around, instead of pointing at
/// now-stale absolute addresses. Covered:
///  - control flow — BRA/Bcc/BSR, DBcc (PC-relative, decoded from the opcode);
///  - effective-address references — JMP/JSR/LEA/PEA/MOVE/CMP/… in absolute (<c>$x.w</c>/<c>$x.l</c>)
///    or PC-relative (<c>(d16,PC)</c>) form, spotted in the operand text (which the emulator's
///    Moira disassembler always suffixes/annotates unambiguously).
/// Register-relative (<c>(d16,An)</c>) and immediate (<c>#…</c>) operands are left untouched — they
/// carry no fixed address.
/// </summary>
public partial class SaveListingWindow : Window
{
    // Maximum number of instructions emitted in segment mode (safety net against a huge range)
    const int MaxSegmentInstructions = 200000;

    /// <summary>How a referenced address maps back onto the operand text when rewriting it.</summary>
    enum RefKind
    {
        Branch,     // BRA/Bcc/BSR: the whole operand is the (PC-relative) target
        Dbcc,       // DBcc: operand is "Dn,<target>"
        Abs,        // absolute EA: the target appears verbatim as "$x.w"/"$x.l" (Token)
        PcRel       // PC-relative EA: operand has a "(d16,PC)" token plus a "; ($abs)" annotation
    }

    /// <summary>One address referenced by an instruction (an instruction may have several).</summary>
    struct AddrRef
    {
        public uint Target;     // resolved absolute address
        public RefKind Kind;
        public string Token;    // exact operand substring to replace (Abs/PcRel only)
    }

    /// <summary>One disassembled instruction, kept from the collection pass to the emit pass.</summary>
    sealed class Instr
    {
        public uint Addr;
        public int Size;
        public string Mnem = "";
        public string Ops = "";
        public string Hex = "";
        public List<AddrRef> Refs = new();
    }

    // Bcc mnemonics (without size suffix). BRA (cond 0) and BSR (cond 1) included; all are
    // PC-relative. "bhs"/"blo" are accepted as aliases in case the disassembler uses them.
    static readonly HashSet<string> BranchMnemonics = new()
    {
        "bra", "bsr",
        "bhi", "bls", "bcc", "bcs", "bne", "beq", "bvc", "bvs",
        "bpl", "bmi", "bge", "blt", "bgt", "ble", "bhs", "blo"
    };

    // Absolute effective address in Moira syntax: "$<hex>.w" or "$<hex>.l". The size suffix is
    // what tells it apart from a branch target (bare "$hex"), an immediate ("#$hex") or a
    // register displacement ("($hex,An)" — no suffix).
    static readonly Regex AbsRefRegex = new(@"\$([0-9A-Fa-f]+)\.([wlWL])", RegexOptions.Compiled);
    // A "(...,PC)" displacement token (not PC-indexed, which ends in ",PC,Xn)" and has no fixed target).
    static readonly Regex PcRelTokenRegex = new(@"\([^()]*,PC\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Moira appends the resolved absolute of a PC-relative EA as a trailing "; ($abs)" annotation.
    static readonly Regex PcRelAnnotationRegex = new(@";\s*\(\$([0-9A-Fa-f]+)\)\s*$", RegexOptions.Compiled);

    public SaveListingWindow()
    {
        InitializeComponent();

        if (!Design.IsDesignMode)
        {
            // The usual case is dumping around where execution stopped, so "From" is
            // pre-filled with the current PC and "To" with a small stretch ahead.
            uint pc = CPU._moira.PC;
            txtFrom.Text = $"{pc:X8}";
            txtTo.Text = $"{pc + 0x100:X8}";
        }
    }

    public void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    public void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = BuildListing();
        }
        catch (Exception ex)
        {
            ShowError($"Could not build the listing: {ex.Message}");
            return;
        }

        // Invalid input: BuildListing has already notified the user
        if (text == null)
            return;

        var (canceled, path) = TinyDialogs.SaveFileDialog("Save assembler listing",
            "listing.asm",
            new FileFilter("68k assembler source", ["*.asm", "*.s", "*.i", "*.txt"]));

        if (canceled || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            ShowError($"Could not save the listing: {ex.Message}");
            return;
        }

        Close();
    }

    /// <summary>
    /// Generates the full listing text according to the window options.
    /// Returns <c>null</c> if the input is invalid (the user has already been notified).
    /// </summary>
    string BuildListing()
    {
        // 1) Collect the instructions to emit (either around the PC or over a From/To segment).
        List<Instr> instrs = rbWrapPc.IsChecked == true ? CollectWrapPc() : CollectSegment();
        if (instrs == null)
            return null;

        // 2) Turn referenced addresses into labels. An address only qualifies if it lands
        //    exactly on one of the instructions we are emitting (an even, aligned start); a
        //    reference into data or into the middle of an instruction keeps its raw address.
        var starts = new HashSet<uint>();
        foreach (var ins in instrs)
            starts.Add(ins.Addr);

        var labels = new Dictionary<uint, string>();
        foreach (var ins in instrs)
            foreach (var r in ins.Refs)
                if (starts.Contains(r.Target) && !labels.ContainsKey(r.Target))
                    labels[r.Target] = LabelName(r.Target);

        // 3) Emit.
        var sb = new StringBuilder();

        sb.Append("; 68000 disassembly listing generated by ASE").Append(Environment.NewLine);
        if (labels.Count > 0)
            sb.Append("; Labels Lxxxxxxxx mark addresses referenced from inside the listing.").Append(Environment.NewLine);
        sb.Append(';').Append(Environment.NewLine);

        if (tglRegisters.IsChecked == true)
            AppendRegisters(sb);

        // Locate the code at the address it was disassembled from, so the re-assembled binary
        // lands where the original did (and the labels resolve to the same addresses).
        if (instrs.Count > 0)
            sb.Append("\t\tORG\t$").Append(instrs[0].Addr.ToString("X8")).Append(Environment.NewLine);

        // The per-line "; $addr: opcode words" comment is handy reference info but roughly
        // doubles the source size; dropping it keeps the file small enough to re-assemble on
        // a 1 MB ST.
        bool addressDump = tglAddressDump.IsChecked == true;

        foreach (var ins in instrs)
        {
            if (labels.TryGetValue(ins.Addr, out var here))
                sb.Append(here).Append(':').Append(Environment.NewLine);

            string ops = RewriteOperands(ins, labels);

            sb.Append("\t\t").Append(ins.Mnem).Append('\t').Append(ops);
            if (addressDump)
                sb.Append("\t; $").Append(ins.Addr.ToString("X8")).Append(": ").Append(ins.Hex);
            sb.Append(Environment.NewLine);
        }

        return sb.ToString();
    }

    /// <summary>Collects instructions over the From/To segment. Returns null on invalid input.</summary>
    List<Instr> CollectSegment()
    {
        if (!TryParseHex(txtFrom.Text, out uint from) || !TryParseHex(txtTo.Text, out uint to))
        {
            ShowError("\"From\" and \"To\" must be hexadecimal addresses.");
            return null;
        }

        // 68000 instructions are word-aligned
        from &= ~1u;
        to &= ~1u;
        if (to < from)
            (from, to) = (to, from);

        var list = new List<Instr>();
        uint addr = from;
        for (int i = 0; i < MaxSegmentInstructions && addr <= to; i++)
        {
            var ins = DisasmOne(addr);
            list.Add(ins);

            uint next = addr + (uint)ins.Size;
            if (next <= addr)   // address counter overflow
                break;
            addr = next;
        }

        return list;
    }

    /// <summary>Collects instructions centered on the PC. Returns null on invalid input.</summary>
    List<Instr> CollectWrapPc()
    {
        if (!TryParseCount(txtWrapPc.Text, out int count) || count <= 0)
        {
            ShowError("The \"Wrap PC\" value must be a positive number of instructions.");
            return null;
        }

        // Half of the instructions go before the PC to leave it centered in the listing
        int before = count / 2;
        uint addr = FindStartBefore(CPU._moira.PC, before);

        var list = new List<Instr>();
        for (int i = 0; i < count; i++)
        {
            var ins = DisasmOne(addr);
            list.Add(ins);

            uint next = addr + (uint)ins.Size;
            if (next <= addr)   // address counter overflow
                break;
            addr = next;
        }

        return list;
    }

    /// <summary>
    /// Disassembles the instruction at <paramref name="addr"/>, splitting it into the
    /// re-assemblable fields and collecting the addresses it references.
    /// </summary>
    Instr DisasmOne(uint addr)
    {
        var (dis, size) = CPU._moira.Disassemble(addr, 250);
        if (size <= 0)
            size = 2;

        // Original opcode words (for the comment that preserves the disassembler info)
        var hex = new StringBuilder();
        for (uint x = 0; x < size; x += 2)
        {
            if (x > 0)
                hex.Append(' ');
            hex.Append($"{ASEMain._mem.Read16(addr + x):X4}");
        }

        // Moira pads the mnemonic up to column 8 with spaces, so the first run of spaces
        // separates the mnemonic from the operands.
        string dl = dis.TrimEnd();
        int sp = dl.IndexOf(' ');
        string mnem = sp < 0 ? dl : dl.Substring(0, sp);
        string ops = sp < 0 ? "" : dl.Substring(sp + 1).TrimStart();

        return new Instr
        {
            Addr = addr,
            Size = size,
            Mnem = mnem,
            Ops = ops,
            Hex = hex.ToString(),
            Refs = FindReferences(addr, mnem, ops)
        };
    }

    /// <summary>
    /// Collects the addresses referenced by the instruction. Control-flow branches (whose target
    /// the disassembler prints as a bare "$addr") are decoded from the opcode words; every other
    /// address reference (JMP/JSR/LEA/PEA/MOVE/CMP/… in absolute or PC-relative form) is read off
    /// the operand text, which Moira renders with an unambiguous "$x.w"/"$x.l" suffix or a
    /// "; ($abs)" PC-relative annotation.
    /// </summary>
    List<AddrRef> FindReferences(uint addr, string mnem, string ops)
    {
        var refs = new List<AddrRef>();

        string m = mnem.ToLowerInvariant();
        int dot = m.IndexOf('.');
        string baseM = dot < 0 ? m : m.Substring(0, dot);

        // BRA/Bcc/BSR: 8-bit displacement in the opcode, or a 16-bit one in the next word when
        // the byte field is 0. Displacement is relative to addr+2 (68000).
        if (BranchMnemonics.Contains(baseM))
        {
            ushort op = ASEMain._mem.Read16(addr);
            int lo = op & 0xFF;
            uint target = lo == 0x00
                ? addr + 2 + (uint)(short)ASEMain._mem.Read16(addr + 2)
                : addr + 2 + (uint)(sbyte)lo;
            refs.Add(new AddrRef { Target = target, Kind = RefKind.Branch });
            return refs;
        }

        // DBcc (dbra/dbf/dbeq/...): always a 16-bit displacement relative to addr+2.
        if (baseM.StartsWith("db"))
        {
            uint target = addr + 2 + (uint)(short)ASEMain._mem.Read16(addr + 2);
            refs.Add(new AddrRef { Target = target, Kind = RefKind.Dbcc });
            return refs;
        }

        // Absolute effective addresses — one per "$x.w"/"$x.l" token (MOVE can have two).
        foreach (Match mt in AbsRefRegex.Matches(ops))
        {
            if (!uint.TryParse(mt.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
                continue;
            // ".w" is a sign-extended 16-bit address; ".l" is the 32-bit value as-is.
            uint target = char.ToLowerInvariant(mt.Groups[2].Value[0]) == 'w' ? (uint)(short)(ushort)val : val;
            refs.Add(new AddrRef { Target = target, Kind = RefKind.Abs, Token = mt.Value });
        }

        // PC-relative effective address — resolved absolute taken from Moira's trailing annotation.
        var ann = PcRelAnnotationRegex.Match(ops);
        if (ann.Success)
        {
            var tok = PcRelTokenRegex.Match(ops);
            if (tok.Success && uint.TryParse(ann.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint target))
                refs.Add(new AddrRef { Target = target, Kind = RefKind.PcRel, Token = tok.Value });
        }

        return refs;
    }

    /// <summary>
    /// Rewrites the instruction's operands so every reference whose target got a label points at
    /// the label instead of a raw address. References with no in-listing target are left as-is.
    /// </summary>
    static string RewriteOperands(Instr ins, Dictionary<uint, string> labels)
    {
        string ops = ins.Ops;
        bool pcAnnotationStripped = false;

        foreach (var r in ins.Refs)
        {
            if (!labels.TryGetValue(r.Target, out var label))
                continue;

            switch (r.Kind)
            {
                case RefKind.Branch:
                    // The whole operand is the target address.
                    ops = label;
                    break;

                case RefKind.Dbcc:
                    // "Dn,<target>" — keep the counter register, swap the address for the label.
                    int comma = ops.IndexOf(',');
                    ops = comma < 0 ? label : ops.Substring(0, comma + 1) + label;
                    break;

                case RefKind.Abs:
                    ops = ops.Replace(r.Token, label);
                    break;

                case RefKind.PcRel:
                    ops = ops.Replace(r.Token, label + "(pc)");
                    if (!pcAnnotationStripped)
                    {
                        // Drop Moira's now-redundant "; ($abs)" resolved-address annotation.
                        ops = PcRelAnnotationRegex.Replace(ops, "").TrimEnd();
                        pcAnnotationStripped = true;
                    }
                    break;
            }
        }

        return ops;
    }

    /// <summary>Label emitted for an in-listing referenced address.</summary>
    static string LabelName(uint addr) => $"L{addr:X8}";

    /// <summary>Dumps all registers as tab-separated comment lines at the beginning of the file.</summary>
    void AppendRegisters(StringBuilder sb)
    {
        var m = CPU._moira;

        sb.Append("; ------------------------------------------------------------").Append(Environment.NewLine);
        sb.Append("; Register dump").Append(Environment.NewLine);
        sb.Append("; ------------------------------------------------------------").Append(Environment.NewLine);

        for (int i = 0; i < 8; i++)
            sb.Append($";\tD{i}\t${m.D[i]:X8}").Append(Environment.NewLine);
        for (int i = 0; i < 7; i++)
            sb.Append($";\tA{i}\t${m.A[i]:X8}").Append(Environment.NewLine);

        sb.Append($";\tSP\t${m.SP:X8}").Append(Environment.NewLine);   // A7
        sb.Append($";\tPC\t${m.PC:X8}").Append(Environment.NewLine);
        sb.Append($";\tSR\t${m.SR:X4}").Append(Environment.NewLine);
        sb.Append("; ------------------------------------------------------------").Append(Environment.NewLine);
        sb.Append(';').Append(Environment.NewLine);
    }

    /// <summary>
    /// Locates the start address from which a forward disassembly produces
    /// <paramref name="wanted"/> instructions before <paramref name="anchor"/>, leaving the
    /// anchor (the PC) centered. Since 68000 instructions have variable length, it looks for
    /// the farthest aligned start that "fits" with the anchor (same as the debug window's
    /// disassembler) and counts backwards from there. If there is not enough valid code
    /// behind, it returns the farthest start found (best effort).
    /// </summary>
    uint FindStartBefore(uint anchor, int wanted)
    {
        anchor &= ~1u;
        if (wanted <= 0 || anchor == 0)
            return anchor;

        // 68000 instructions take at most ~10 bytes; plenty of slack is left.
        uint windowBytes = (uint)wanted * 10u;
        uint windowStart = anchor > windowBytes ? anchor - windowBytes : 0;
        int slots = (int)((anchor - windowStart) / 2);
        if (slots <= 0)
            return anchor;

        // Size (in words) of the instruction starting at each even address of the window
        var sizeInWords = new int[slots];
        for (int i = 0; i < slots; i++)
        {
            var (_, size) = CPU._moira.Disassemble(windowStart + (uint)i * 2, 250);
            sizeInWords[i] = size > 0 ? size / 2 : 1;
        }

        // reachable[i] == true if disassembling from windowStart + i*2 lands exactly on the anchor
        var reachable = new bool[slots + 1];
        reachable[slots] = true;
        for (int i = slots - 1; i >= 0; i--)
        {
            int next = i + sizeInWords[i];
            reachable[i] = next <= slots && reachable[next];
        }

        int startSlot = -1;
        for (int i = 0; i < slots; i++)
            if (reachable[i]) { startSlot = i; break; }

        if (startSlot < 0)
            return anchor;

        // Start addresses of each instruction from the synchronized point up to the anchor;
        // pick the one exactly `wanted` instructions behind (or the farthest one).
        var addrs = new List<uint>();
        uint a = windowStart + (uint)startSlot * 2;
        while (a < anchor)
        {
            addrs.Add(a);
            var (_, size) = CPU._moira.Disassemble(a, 250);
            a += (uint)(size > 0 ? size : 2);
        }

        if (addrs.Count == 0)
            return anchor;
        if (addrs.Count <= wanted)
            return addrs[0];
        return addrs[addrs.Count - wanted];
    }

    static bool TryParseHex(string s, out uint value)
        => uint.TryParse((s ?? "").Trim().TrimStart('$'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    static bool TryParseCount(string s, out int value)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    static void ShowError(string message)
        => TinyDialogs.MessageBox("Save listing", message,
            MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
}
