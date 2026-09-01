/*
 *
 * Machine state snapshots (save / restore).
 *
 * Binary format "ASESNAP2", all multi-byte values BIG-ENDIAN (the 68000's native
 * order, so RAM and registers read naturally in a hex editor):
 *
 *   0x00  8 bytes   magic "ASESNAP2"
 *   ....  sections  each: 4 ASCII bytes id + uint32 payload length + payload
 *
 * Sections (unknown ids are skipped on load, so the format can grow):
 *
 *   "CFG "  ST model, RAM configuration and the floppy image paths that were
 *           inserted (re-inserted on restore when the files still exist).
 *   "CPU "  68000: D0-D7, A0-A7, USP, SSP, PC, PC0, SR, IRC/IRD (prefetch),
 *           IPL and the absolute cycle clock (disk rotation and motor state
 *           derive from it, so it is load-bearing for the FDC).
 *   "ROM "  TOS base address + full ROM contents (the snapshot is self-contained).
 *   "MEM "  physical RAM (not the MMU-translated logical view) + the $FF8000
 *           hardware port latch array.
 *   "MFP "  MFP 68901 registers, timer counters/prescalers and the HBL/VBL/MFP
 *           interrupt line state.
 *   "PSG "  YM2149 registers and synthesis counters.
 *   "ACIA"  ACIA 6850 + IKBD state, including the pending receive queue.
 *   "FDC "  WD1772 + DMA registers, head position, motor clock. A timed STX
 *           command in flight is completed (flushed) before saving.
 *   "DMAS"  STE DMA sound + Microwire/LMC1992 (ignored on ST/Mega restores).
 *   "BLIT"  Blitter registers and barrel-shifter state.
 *   "MIDI"  MIDI ACIA 6850 registers and transmit state (the host-side receive
 *           queue and whatever the port is wired to belong to the session, not
 *           to the machine). Absent in older snapshots: the chip stays reset.
 *   "GHD "  GEMDOS hard drive hook bookkeeping: the saved RAM has $84 pointing
 *           into the synthetic cartridge, so the chained TOS vector (and act_pd)
 *           must travel too. Host-side file handles and directory scans do NOT
 *           survive a restore — like unplugging and replugging the drive.
 *
 * Not included: the floppy image contents themselves (only their paths) and
 * Moira-internal state not exposed by the wrapper (e.g. a pending STOP).
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Text;
using static ASE.Config;

namespace ASE
{
    public static class Snapshot
    {
        const string Magic = "ASESNAP2";

        // ==================== Big-endian primitives ====================

        /// <summary>Big-endian writer used by every component's SaveState.</summary>
        public sealed class Writer
        {
            readonly Stream _s;
            public Writer(Stream s) { _s = s; }

            public void U8(byte v) => _s.WriteByte(v);
            public void Bool(bool v) => _s.WriteByte(v ? (byte)1 : (byte)0);
            public void U16(ushort v) { U8((byte)(v >> 8)); U8((byte)v); }
            public void I16(short v) => U16((ushort)v);
            public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
            public void I32(int v) => U32((uint)v);
            public void I64(long v) { U32((uint)((ulong)v >> 32)); U32((uint)v); }
            public void F32(float v) => I32(BitConverter.SingleToInt32Bits(v));
            public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));
            public void Bytes(byte[] b) => _s.Write(b, 0, b.Length);

            public void Str(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(s ?? "");
                U16((ushort)b.Length);
                Bytes(b);
            }
        }

        /// <summary>Big-endian reader over a section payload.</summary>
        public sealed class Reader
        {
            readonly byte[] _b;
            int _pos;
            public Reader(byte[] payload) { _b = payload; }

            public byte U8() => _b[_pos++];
            public bool Bool() => U8() != 0;
            public ushort U16() => (ushort)((U8() << 8) | U8());
            public short I16() => (short)U16();
            public uint U32() => (uint)((U16() << 16) | U16());
            public int I32() => (int)U32();
            public long I64() => ((long)U32() << 32) | U32();
            public float F32() => BitConverter.Int32BitsToSingle(I32());
            public double F64() => BitConverter.Int64BitsToDouble(I64());
            public int Remaining => _b.Length - _pos;

            public byte[] Bytes(int count)
            {
                var r = new byte[count];
                Array.Copy(_b, _pos, r, 0, count);
                _pos += count;
                return r;
            }

            public string Str() => Encoding.UTF8.GetString(Bytes(U16()));
        }

        /// <summary>Parsed snapshot file, ready to be applied to a freshly initialized machine.</summary>
        public sealed class SnapshotFile
        {
            public ConfigOptions.STModels Model;
            public ConfigOptions.RAMConfigurations RamConfig;
            public bool Mono;
            public bool DriveBConnected;
            public string DriveAPath = "";
            public string DriveBPath = "";
            public Dictionary<string, byte[]> Sections = new();
        }

        // ==================== Save ====================

        /// <summary>
        /// Dumps the complete machine state to <paramref name="path"/>. Must be called with the
        /// emulation paused (the debugger does this). Any timed STX floppy command still in
        /// flight is completed first so the FDC/DMA/RAM state saved is coherent.
        /// </summary>
        public static void Save(string path)
        {
            // Complete any in-flight STX operation: its remaining bytes land in RAM and the
            // command finishes, so the snapshot never contains a half-delivered DMA transfer.
            WD1772.FlushPendingOp();

            var mem = ASEMain._mem;

            using var fs = File.Create(path);
            fs.Write(Encoding.ASCII.GetBytes(Magic));

            WriteSection(fs, "CFG ", w =>
            {
                w.U8((byte)ConfigOptions.RunninConfig.STModel);
                w.U8((byte)ConfigOptions.RunninConfig.RAMConfiguration);
                w.Str(ASEMain.driveA.ImagePath);
                w.Str(ASEMain.driveB.ImagePath);
                // Appended after the original fields so older readers (which stop here) still load.
                w.U8((byte)(ConfigOptions.RunninConfig.MonochromeMonitor ? 1 : 0));

                // Whether drive B was plugged in. The path above says what was in it, not whether
                // the machine had the drive at all — and an unplugged B: is a different machine to
                // the software (see WD1772.DriveSelected).
                w.U8((byte)(ConfigOptions.RunninConfig.DriveBEnabled ? 1 : 0));
            });

            WriteSection(fs, "CPU ", WriteCpu);

            WriteSection(fs, "ROM ", w =>
            {
                w.U32(mem.TosBase);
                w.Bytes(mem.ROM);
            });

            WriteSection(fs, "MEM ", w =>
            {
                w.U32((uint)mem.RamSize);
                w.Bytes(mem.RAM);
                w.Bytes(mem.Ports);
            });

            WriteSection(fs, "MFP ", ASEMain._mfp.SaveState);
            WriteSection(fs, "PSG ", ASEMain._ym.SaveState);
            WriteSection(fs, "ACIA", ACIA.SaveState);
            WriteSection(fs, "FDC ", WD1772.SaveState);
            WriteSection(fs, "DMAS", STEDmaSound.SaveState);
            WriteSection(fs, "BLIT", Blitter.SaveState);
            WriteSection(fs, "MIDI", MidiAcia.SaveState);
            WriteSection(fs, "GHD ", GemdosHD.SaveState);
        }

        static void WriteSection(Stream fs, string id, Action<Writer> body)
        {
            using var ms = new MemoryStream();
            body(new Writer(ms));

            fs.Write(Encoding.ASCII.GetBytes(id));
            var w = new Writer(fs);
            w.U32((uint)ms.Length);
            ms.Position = 0;
            ms.CopyTo(fs);
        }

        static void WriteCpu(Writer w)
        {
            var cpu = CPU._moira;

            for (int i = 0; i < 8; i++) w.U32(cpu.D[i]);
            for (int i = 0; i < 8; i++) w.U32(cpu.A[i]);

            // Moira only exposes the ACTIVE stack pointer. To save both banks, the SR's S bit
            // is toggled temporarily (setSR swaps USP/SSP) and then restored.
            ushort sr = cpu.SR;
            uint usp, ssp;

            if ((sr & 0x2000) != 0)
            {
                ssp = cpu.SP;
                cpu.SR = (ushort)(sr & ~0x2000);
                usp = cpu.SP;
            }
            else
            {
                usp = cpu.SP;
                cpu.SR = (ushort)(sr | 0x2000);
                ssp = cpu.SP;
            }

            cpu.SR = sr;

            w.U32(usp);
            w.U32(ssp);
            w.U32(cpu.PC);
            w.U32(cpu.PC0);
            w.U16(sr);
            w.U16(cpu.IRC);
            w.U16(cpu.IRD);
            w.U8(cpu.IPL);
            w.U8(0);            // reserved
            w.I64(cpu.Clock);
        }

        // ==================== Load ====================

        static readonly int[] RamSizesByConfig =
        {
            512 * 1024,     // RAM_512KB
            1024 * 1024,    // RAM_1MB
            2048 * 1024,    // RAM_2MB
            4096 * 1024,    // RAM_4MB
        };

        /// <summary>
        /// Reads and validates a snapshot file. Throws with a descriptive message when the file
        /// is not a valid ASESNAP2 snapshot. Does not touch the running machine.
        /// </summary>
        public static SnapshotFile ReadFile(string path)
        {
            byte[] file = File.ReadAllBytes(path);

            if (file.Length < 8 || Encoding.ASCII.GetString(file, 0, 8) != Magic)
                throw new InvalidDataException($"Not an {Magic} snapshot file.");

            var snap = new SnapshotFile();
            int pos = 8;

            while (pos + 8 <= file.Length)
            {
                string id = Encoding.ASCII.GetString(file, pos, 4);
                int len = (file[pos + 4] << 24) | (file[pos + 5] << 16) | (file[pos + 6] << 8) | file[pos + 7];
                pos += 8;

                if (len < 0 || pos + len > file.Length)
                    throw new InvalidDataException($"Corrupt snapshot: section '{id}' overruns the file.");

                var payload = new byte[len];
                Array.Copy(file, pos, payload, 0, len);
                snap.Sections[id] = payload;    // unknown sections are simply carried and ignored
                pos += len;
            }

            foreach (string required in new[] { "CFG ", "CPU ", "ROM ", "MEM " })
                if (!snap.Sections.ContainsKey(required))
                    throw new InvalidDataException($"Corrupt snapshot: missing section '{required}'.");

            // CFG: model, RAM configuration and inserted disks
            var cfg = new Reader(snap.Sections["CFG "]);
            byte model = cfg.U8();
            byte ram = cfg.U8();

            if (!Enum.IsDefined(typeof(ConfigOptions.STModels), (int)model))
                throw new InvalidDataException($"Unknown ST model ({model}) in snapshot.");
            if (ram >= RamSizesByConfig.Length)
                throw new InvalidDataException($"Unknown RAM configuration ({ram}) in snapshot.");

            snap.Model = (ConfigOptions.STModels)model;
            snap.RamConfig = (ConfigOptions.RAMConfigurations)ram;
            snap.DriveAPath = cfg.Str();
            snap.DriveBPath = cfg.Str();
            // Monitor type and drive B are appended after the original fields; absent in older
            // snapshots, which then restore with drive B unplugged.
            snap.Mono = cfg.Remaining > 0 && cfg.Bool();
            snap.DriveBConnected = cfg.Remaining > 0 && cfg.Bool();

            // Consistency checks that don't need the machine
            int romLen = snap.Sections["ROM "].Length - 4;
            if (romLen != 192 * 1024 && romLen != 256 * 1024)
                throw new InvalidDataException($"Invalid TOS size in snapshot ({romLen} bytes).");

            var memR = new Reader(snap.Sections["MEM "]);
            uint ramSize = memR.U32();
            if (ramSize != (uint)RamSizesByConfig[ram])
                throw new InvalidDataException("Snapshot RAM size does not match its RAM configuration.");

            return snap;
        }

        /// <summary>
        /// Applies a snapshot to a machine that has just been (re)initialized with the snapshot's
        /// model and RAM configuration (see <see cref="ASEMain.RestoreSnapshot"/>). Runs before
        /// the emulation thread starts, so no synchronization is needed.
        /// </summary>
        public static void Apply(SnapshotFile snap)
        {
            var mem = ASEMain._mem;

            // ROM: the snapshot is self-contained, the TOS it was taken with replaces the
            // one loaded from the configured path.
            var romR = new Reader(snap.Sections["ROM "]);
            uint tosBase = romR.U32();
            byte[] rom = romR.Bytes(romR.Remaining);

            mem.ROM = rom;
            mem.TosSize = rom.Length;
            mem.TosBase = tosBase;

            // RAM (physical) + hardware port latches, then re-derive the MMU banks from $FF8001
            var memR = new Reader(snap.Sections["MEM "]);
            uint ramSize = memR.U32();
            if (ramSize != (uint)mem.RamSize)
                throw new InvalidDataException("Snapshot RAM size does not match the machine configuration.");

            Array.Copy(memR.Bytes((int)ramSize), mem.RAM, ramSize);
            Array.Copy(memR.Bytes(mem.Ports.Length), mem.Ports, mem.Ports.Length);
            mem.RestoreMMUFromPorts();

            // CPU registers and clock
            ReadCpu(new Reader(snap.Sections["CPU "]));

            // Hardware chips (each section optional: older/newer snapshots still load)
            if (snap.Sections.TryGetValue("MFP ", out var mfp)) ASEMain._mfp.LoadState(new Reader(mfp));
            if (snap.Sections.TryGetValue("PSG ", out var psg)) ASEMain._ym.LoadState(new Reader(psg));
            if (snap.Sections.TryGetValue("ACIA", out var acia)) ACIA.LoadState(new Reader(acia));
            if (snap.Sections.TryGetValue("FDC ", out var fdc)) WD1772.LoadState(new Reader(fdc));
            if (snap.Sections.TryGetValue("DMAS", out var dmas)) STEDmaSound.LoadState(new Reader(dmas));
            if (snap.Sections.TryGetValue("BLIT", out var blit)) Blitter.LoadState(new Reader(blit));
            if (snap.Sections.TryGetValue("MIDI", out var midi)) MidiAcia.LoadState(new Reader(midi));
            if (snap.Sections.TryGetValue("GHD ", out var ghd)) GemdosHD.LoadState(new Reader(ghd));

            // The GLUE sync/resolution latches mirror the restored port bytes
            VideoTiming.RestoreFromPorts();

            // Best effort: put back the disks that were inserted when the snapshot was taken
            ReinsertDrive(ASEMain.driveA, snap.DriveAPath, 'A');

            // Only if the machine has the drive: a snapshot written before drive B could be
            // unplugged carries a path but no connection flag, and a disk sitting in a drive
            // that is not there is a state the UI has no way to show or clear.
            if (ConfigOptions.RunninConfig.DriveBEnabled)
                ReinsertDrive(ASEMain.driveB, snap.DriveBPath, 'B');
        }

        static void ReadCpu(Reader r)
        {
            var cpu = CPU._moira;
            var d = cpu.D;
            var a = cpu.A;

            for (int i = 0; i < 8; i++) d[i] = r.U32();

            // A7 is skipped: it is the active stack pointer and is rebuilt from USP/SSP + SR
            for (int i = 0; i < 8; i++)
            {
                uint v = r.U32();
                if (i < 7) a[i] = v;
            }

            uint usp = r.U32();
            uint ssp = r.U32();
            uint pc = r.U32();
            uint pc0 = r.U32();
            ushort sr = r.U16();
            ushort irc = r.U16();
            ushort ird = r.U16();
            byte ipl = r.U8();
            r.U8();             // reserved
            long clock = r.I64();

            // Load both stack banks by toggling the S bit; the final SR activates the right one
            cpu.SR = 0x2700;
            cpu.SP = ssp;
            cpu.SR = 0x0700;
            cpu.SP = usp;
            cpu.SR = sr;

            cpu.PC = pc;
            cpu.PC0 = pc0;
            cpu.IRC = irc;
            cpu.IRD = ird;
            cpu.IPL = ipl;
            cpu.Clock = clock;
        }

        static void ReinsertDrive(FloppyImage drive, string path, char letter)
        {
            if (string.IsNullOrEmpty(path) || drive.ImagePath == path)
                return;

            // Zip entries are encoded as "volume.zip|entry"; the file to check is the volume
            string fileOnDisk = path.Split('|')[0];
            if (!File.Exists(fileOnDisk))
            {
                ColoredConsole.WriteLine($"Snapshot: floppy image for drive {letter} not found: [[red]]{path}[[/red]]", ConfigOptions.DebugModes.Quiet);
                return;
            }

            if (drive.Insert(path, out string message))
                ColoredConsole.WriteLine($"Snapshot: drive {letter} -> {message}", ConfigOptions.DebugModes.Quiet);
            else
                ColoredConsole.WriteLine($"Snapshot: could not re-insert drive {letter} image: {message}", ConfigOptions.DebugModes.Quiet);
        }
    }
}
