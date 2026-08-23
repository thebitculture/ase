/*
 *
 * ACSI hard disk emulation (Atari Computer System Interface).
 *
 * The ACSI bus hangs off the same DMA controller as the floppy: the HDC is
 * addressed through $FF8604/$FF8606 with bit 3 of the DMA mode register set,
 * and shares the FDC's interrupt line into MFP GPIP5 (see WD1772.cs).
 * Commands are a subset of SCSI; the protocol and command set are ported from
 * the Hatari emulator (hdc.c). Command execution is immediate (a very fast
 * drive) what matters to drivers is the register/IRQ protocol, which is
 * reproduced faithfully, including the short delay before the IRQ that
 * follows a DMA transfer.
 *
 * The disk itself is a raw image file (512-byte sectors). Writes go through
 * to the file: a hard disk that forgets is useless (games save to it), unlike
 * the floppy policy of never persisting.
 *
 * This part of the emulation has been ported directly from Hatari.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE
{
    /// <summary>One ACSI target: a raw hard disk image file.</summary>
    public sealed class AcsiDisk
    {
        public FileStream Image;
        public string Path;
        public bool ReadOnly;
        public long SectorCount;          // image size / 512
        public int LastError;             // REQUEST SENSE key (HD_REQSENS_*)
        public long LastBlockAddr;
        public bool SetLastBlockAddr;

        public bool Enabled => Image != null;
    }

    /// <summary>
    /// The ACSI bus controller: up to 8 targets (ASE attaches one, target 0, from the
    /// configuration). Reached from WD1772 when the DMA mode register selects the HDC.
    /// </summary>
    public static class Acsi
    {
        public const int SectorSize = 512;

        // ACSI/SCSI opcodes (class 0 unless noted)
        const byte HD_TEST_UNIT_RDY = 0x00;
        const byte HD_REQ_SENSE = 0x03;
        const byte HD_FORMAT_DRIVE = 0x04;
        const byte HD_READ_SECTOR = 0x08;
        const byte HD_WRITE_SECTOR = 0x0A;
        const byte HD_SEEK = 0x0B;
        const byte HD_INQUIRY = 0x12;
        const byte HD_MODESELECT = 0x15;
        const byte HD_SHIP = 0x1B;
        const byte HD_MODESENSE = 0x1A;
        const byte HD_READ_CAPACITY1 = 0x25;   // class 1
        const byte HD_READ_SECTOR1 = 0x28;     // class 1
        const byte HD_WRITE_SECTOR1 = 0x2A;    // class 1
        const byte HD_REPORT_LUNS = 0xA0;

        // Controller status byte (read back at $FF8604 with the HDC selected)
        const byte HD_STATUS_OK = 0x00;
        const byte HD_STATUS_ERROR = 0x02;

        // REQUEST SENSE keys
        const byte HD_REQSENS_OK = 0x00;
        const byte HD_REQSENS_NOSECTOR = 0x01;
        const byte HD_REQSENS_WRITEERR = 0x03;
        const byte HD_REQSENS_OPCODE = 0x20;
        const byte HD_REQSENS_INVADDR = 0x21;
        const byte HD_REQSENS_INVARG = 0x24;
        const byte HD_REQSENS_INVLUN = 0x25;

        // Some programs expect a minimum delay between the DMA transfer and the IRQ that
        // signals its completion (Hatari measured this with "Idris OS"; same constant).
        const long TRANSFER_IRQ_DELAY_CYCLES = 1000;

        static readonly AcsiDisk[] _devs = new AcsiDisk[8];

        /// <summary>True when at least one image is attached this power-on.</summary>
        public static bool EmulationOn { get; private set; }

        /// <summary>Primary partitions found in the attached image's root sector; the GEMDOS
        /// drive letter is placed after them (see GemdosHD).</summary>
        public static int PartitionCount { get; private set; }

        // Controller state
        static readonly byte[] _command = new byte[16];
        static int _byteCount;
        static byte _opcode;
        static int _target;
        static byte _status;
        static bool _dmaError;

        // Response data prepared by the last command, delivered by the DMA
        static byte[] _buffer = new byte[SectorSize];
        static int _dataLen;
        static AcsiDisk _pendingWrite;      // non-null: the DMA transfer must write into this image
        static long _pendingWriteLba;       // where it writes (captured with the command)

        // Delayed IRQ after a DMA transfer (absolute CPU clock; -1 = none pending)
        static long _irqRaiseClock = -1;

        static AcsiDisk TargetDev => _devs[_target] ?? _dummy;
        static readonly AcsiDisk _dummy = new AcsiDisk();

        /// <summary>
        /// Attaches (or detaches) the configured image. Called once per power-on from
        /// ASEMain.TurnOn like the real hardware, plugging a disk needs a power cycle.
        /// Failures report to the console and leave the bus empty, never a dead machine.
        /// </summary>
        public static void Initialize()
        {
            Shutdown();

            _byteCount = 0;
            _opcode = 0;
            _target = 0;
            _status = 0;
            _dmaError = false;
            _dataLen = 0;
            _pendingWrite = null;
            _irqRaiseClock = -1;

            if (!ConfigOptions.RunninConfig.AcsiImageEnabled)
                return;

            string path = ConfigOptions.RunninConfig.AcsiImagePath ?? "";

            if (!File.Exists(path))
            {
                ColoredConsole.WriteLine($"ACSI: image [[red]]{path}[[/red]] not found, hard disk disabled.");
                return;
            }

            long size = new FileInfo(path).Length;
            if (size == 0 || (size % SectorSize) != 0)
            {
                ColoredConsole.WriteLine($"ACSI: image [[red]]{path}[[/red]] size ({size} bytes) is not a multiple of {SectorSize}, hard disk disabled.");
                return;
            }

            var dev = new AcsiDisk { Path = path, SectorCount = size / SectorSize };

            try
            {
                dev.Image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            catch
            {
                try
                {
                    dev.Image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    dev.ReadOnly = true;
                    ColoredConsole.WriteLine($"ACSI: image [[yellow]]{path}[[/yellow]] is read-only, writes will fail.");
                }
                catch (Exception ex)
                {
                    ColoredConsole.WriteLine($"ACSI: cannot open image [[red]]{path}[[/red]]: {ex.Message}. Hard disk disabled.");
                    return;
                }
            }

            _devs[0] = dev;
            EmulationOn = true;
            PartitionCount = CountPartitions(dev);

            ColoredConsole.WriteLine($"ACSI: hard disk image [[green]]{path}[[/green]] " +
                $"({size / (1024.0 * 1024.0):F1} MB, {PartitionCount} partition{(PartitionCount == 1 ? "" : "s")}" +
                $"{(ConfigOptions.RunninConfig.BootFromHardDisk ? ", bootable" : ", boot disabled")}).",
                ConfigOptions.DebugModes.Quiet);
        }

        /// <summary>Closes the image file. Safe to call with nothing attached.</summary>
        public static void Shutdown()
        {
            for (int i = 0; i < _devs.Length; i++)
            {
                try { _devs[i]?.Image?.Dispose(); } catch { }
                _devs[i] = null;
            }
            EmulationOn = false;
            PartitionCount = 0;
        }

        /// <summary>Clears the controller status byte (called from the DMA/FDC reset).</summary>
        public static void ResetCommandStatus() => _status = 0;

        /// <summary>Status byte read at $FF8604 while the HDC is selected.</summary>
        public static byte ReadStatus() => EmulationOn ? _status : (byte)0xFF;

        /// <summary>
        /// A command byte written to $FF8604 with the HDC selected. <paramref name="addr"/>
        /// carries the A2..A0 register-select lines (bits 0-2 of the DMA mode register): A1 low
        /// starts a new command packet, whose first byte holds the target in its top 3 bits.
        /// No IRQ is raised for a target with no device — that silence is how drivers detect an
        /// empty bus (they time out polling GPIP5).
        /// </summary>
        public static void WriteCommandByte(int addr, byte value)
        {
            if (!EmulationOn)
                return;

            // A new byte retires the previous command's IRQ; it is raised again below
            // once the byte has been accepted.
            WD1772.SetHdcIrq(false);
            _irqRaiseClock = -1;

            // A1 pushed low starts a new command. The pin is ignored for the second byte of the
            // packet, as on real hardware (a known buggy driver relies on this, per Hatari).
            if ((addr & 2) == 0 && _byteCount != 1)
            {
                _byteCount = 0;
                _target = (value & 0xE0) >> 5;

                // 0x1F in the low bits is the ICD escape marker: the *next* byte carries a
                // class-1 opcode (>= 0x20). The marker itself is not part of the packet.
                if ((value & 0x1F) != 0x1F)
                {
                    WriteCommandPacket((byte)(value & 0x1F));
                }
                else
                {
                    _status = HD_STATUS_OK;
                    _dmaError = false;
                }
            }
            else
            {
                bool didCmd = WriteCommandPacket(value);

                // The command produced data (a read) or consumed it (a write): move it now if
                // the DMA is already enabled — otherwise the $FF8606 write enabling it triggers
                // the transfer (WD1772 calls DmaTransferIfPending).
                if (didCmd && _status == HD_STATUS_OK && _dataLen > 0)
                    DmaTransfer();
            }

            if (TargetDev.Enabled)
            {
                WD1772.SetDmaError(_dmaError);
                WD1772.SetHdcIrq(true);
            }
        }

        /// <summary>
        /// Accumulates one packet byte and executes the command once complete.
        /// Returns true when the command was executed by this byte.
        /// </summary>
        static bool WriteCommandPacket(byte b)
        {
            bool didCmd = false;
            AcsiDisk dev = TargetDev;

            // A target with no device never answers
            if (!dev.Enabled)
            {
                _status = HD_STATUS_ERROR;
                return false;
            }

            if (_byteCount == 0)
            {
                _opcode = b;
                _dmaError = false;
            }

            if (_byteCount < _command.Length)
                _command[_byteCount] = b;
            _byteCount++;

            // Complete packet? Class 0 = 6 bytes, class 1 (0x20-0x5F) = 10, REPORT LUNS = 12.
            if ((_opcode < 0x20 && _byteCount == 6) ||
                (_opcode >= 0x20 && _opcode < 0x60 && _byteCount == 10) ||
                (_opcode == HD_REPORT_LUNS && _byteCount == 12))
            {
                // Only LUN 0 exists; INQUIRY must answer for any LUN (SCSI standard),
                // and REQUEST SENSE still reports the invalid LUN.
                if (GetLun() == 0 || _opcode == HD_INQUIRY)
                {
                    EmulateCommandPacket();
                    didCmd = true;
                }
                else
                {
                    ColoredConsole.WriteLine($"ACSI: access to non-existing LUN, command ${_opcode:X2}.", ConfigOptions.DebugModes.Information);
                    dev.LastError = HD_REQSENS_INVLUN;
                    if (_opcode == HD_REQ_SENSE)
                    {
                        CmdRequestSense();
                        didCmd = true;
                    }
                    else
                    {
                        _status = HD_STATUS_ERROR;
                    }
                }
            }
            else if (_opcode >= 0x60 && _opcode != HD_REPORT_LUNS)
            {
                // Commands >= 0x60 are not supported
                _status = HD_STATUS_ERROR;
                dev.LastError = HD_REQSENS_OPCODE;
                dev.SetLastBlockAddr = false;
            }
            else
            {
                _status = HD_STATUS_OK;     // intermediate byte accepted
            }

            return didCmd;
        }

        // ---------------- Command packet fields ----------------

        static int GetLun() => (_command[1] & 0xE0) >> 5;

        static long GetLba() => _opcode < 0x20
            ? (uint)(((_command[1] << 16) | (_command[2] << 8) | _command[3]) & 0x1FFFFF)
            : (uint)((_command[2] << 24) | (_command[3] << 16) | (_command[4] << 8) | _command[5]);

        static int GetCount() => _opcode < 0x20
            ? _command[4]
            : (_command[7] << 8) | _command[8];

        static byte[] PrepRespBuf(int size)
        {
            _dataLen = size;
            if (size > _buffer.Length)
                _buffer = new byte[size];
            return _buffer;
        }

        // ---------------- Command execution ----------------

        static void EmulateCommandPacket()
        {
            AcsiDisk dev = TargetDev;
            _dataLen = 0;

            switch (_opcode)
            {
                case HD_TEST_UNIT_RDY:
                    _status = HD_STATUS_OK;
                    break;

                case HD_READ_CAPACITY1:
                    CmdReadCapacity();
                    break;

                case HD_READ_SECTOR:
                case HD_READ_SECTOR1:
                    CmdReadSector();
                    break;

                case HD_WRITE_SECTOR:
                case HD_WRITE_SECTOR1:
                    CmdWriteSector();
                    break;

                case HD_INQUIRY:
                    CmdInquiry();
                    break;

                case HD_SEEK:
                    CmdSeek();
                    break;

                case HD_SHIP:
                    _status = 0xFF;
                    break;

                case HD_REQ_SENSE:
                    CmdRequestSense();
                    break;

                case HD_MODESELECT:
                    _status = HD_STATUS_OK;
                    dev.LastError = HD_REQSENS_OK;
                    dev.SetLastBlockAddr = false;
                    break;

                case HD_MODESENSE:
                    CmdModeSense();
                    break;

                case HD_FORMAT_DRIVE:
                    // Should erase the whole image; accepted as a no-op (like Hatari)
                    _status = HD_STATUS_OK;
                    dev.LastError = HD_REQSENS_OK;
                    dev.SetLastBlockAddr = false;
                    break;

                case HD_REPORT_LUNS:
                    CmdReportLuns();
                    break;

                default:
                    ColoredConsole.WriteLine($"ACSI: unsupported command ${_opcode:X2}.", ConfigOptions.DebugModes.Information);
                    _status = HD_STATUS_ERROR;
                    dev.LastError = HD_REQSENS_OPCODE;
                    dev.SetLastBlockAddr = false;
                    break;
            }

            // Lights the hard disk LED (every command counts, as on the real drive)
            ASEMain.SignalHardDiskActivity();

            if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                ColoredConsole.WriteLine($"[[cyan]]ACSI[[/cyan]] cmd ${_opcode:X2} t={_target} lba={GetLba()} n={GetCount()} -> status ${_status:X2}");
        }

        static void CmdSeek()
        {
            AcsiDisk dev = TargetDev;
            dev.LastBlockAddr = GetLba();

            if (dev.LastBlockAddr < dev.SectorCount)
            {
                _status = HD_STATUS_OK;
                dev.LastError = HD_REQSENS_OK;
            }
            else
            {
                _status = HD_STATUS_ERROR;
                dev.LastError = HD_REQSENS_INVADDR;
            }

            dev.SetLastBlockAddr = true;
        }

        static void CmdInquiry()
        {
            AcsiDisk dev = TargetDev;
            int count = GetCount();

            // 8 header bytes + 8 vendor + 16 product + 4 revision
            byte[] inquiry =
            {
                0,                  // device type: direct access
                0,                  // type qualifier (non-removable)
                1,                  // ACSI/SCSI version
                0,
                31,                 // length of the following data
                0, 0, 0,
                (byte)'A', (byte)'S', (byte)'E', (byte)' ', (byte)' ', (byte)' ', (byte)' ', (byte)' ',
                (byte)'E', (byte)'m', (byte)'u', (byte)'l', (byte)'a', (byte)'t', (byte)'e', (byte)'d',
                (byte)'H', (byte)'a', (byte)'r', (byte)'d', (byte)'d', (byte)'i', (byte)'s', (byte)'k',
                (byte)'0', (byte)'1', (byte)'0', (byte)'0',
            };

            byte[] buf = PrepRespBuf(count);
            int copy = Math.Min(count, inquiry.Length);
            Array.Copy(inquiry, buf, copy);
            if (count > inquiry.Length)
                Array.Clear(buf, inquiry.Length, count - inquiry.Length);

            // Unsupported LUNs answer with peripheral qualifier/type 0x7F
            if (copy > 0)
                buf[0] = GetLun() == 0 ? (byte)0 : (byte)0x7F;

            _status = HD_STATUS_OK;
            dev.LastError = HD_REQSENS_OK;
            dev.SetLastBlockAddr = false;
        }

        static void CmdRequestSense()
        {
            AcsiDisk dev = TargetDev;
            int retLen = GetCount();

            // ACSI (SCSI v1) devices answer 4 bytes when asked for 0; cap at 22
            if (retLen == 0)
                retLen = 4;
            else if (retLen > 22)
                retLen = 22;

            byte[] buf = PrepRespBuf(retLen);
            Array.Clear(buf, 0, retLen);

            if (retLen <= 4)
            {
                buf[0] = (byte)dev.LastError;
                if (dev.SetLastBlockAddr)
                {
                    buf[0] |= 0x80;
                    buf[1] = (byte)(dev.LastBlockAddr >> 16);
                    buf[2] = (byte)(dev.LastBlockAddr >> 8);
                    buf[3] = (byte)dev.LastBlockAddr;
                }
            }
            else
            {
                buf[0] = 0x70;
                if (dev.SetLastBlockAddr)
                {
                    buf[0] |= 0x80;
                    buf[4] = (byte)(dev.LastBlockAddr >> 16);
                    buf[5] = (byte)(dev.LastBlockAddr >> 8);
                    buf[6] = (byte)dev.LastBlockAddr;
                }
                switch (dev.LastError)
                {
                    case HD_REQSENS_OK: buf[2] = 0; break;
                    case HD_REQSENS_OPCODE: buf[2] = 5; break;
                    case HD_REQSENS_INVADDR: buf[2] = 5; break;
                    case HD_REQSENS_INVARG: buf[2] = 5; break;
                    case HD_REQSENS_INVLUN: buf[2] = 5; break;
                    default: buf[2] = 4; break;
                }
                buf[7] = 14;
                buf[12] = (byte)dev.LastError;
                buf[19] = (byte)(dev.LastBlockAddr >> 16);
                buf[20] = (byte)(dev.LastBlockAddr >> 8);
                buf[21] = (byte)dev.LastBlockAddr;
            }

            _status = HD_STATUS_OK;
        }

        static void CmdModeSense()
        {
            AcsiDisk dev = TargetDev;
            dev.SetLastBlockAddr = false;

            // Subpages are not supported
            if (_command[3] != 0)
            {
                _status = HD_STATUS_ERROR;
                dev.LastError = HD_REQSENS_INVARG;
                return;
            }

            byte[] buf;
            switch (_command[2])
            {
                case 0x00:
                    buf = PrepRespBuf(16);
                    ModeSensePage00(dev, buf, 0);
                    break;

                case 0x04:
                    buf = PrepRespBuf(28);
                    ModeSensePage04(dev, buf, 4);
                    buf[0] = 24;
                    buf[1] = 0;
                    buf[2] = 0;
                    buf[3] = 0;
                    break;

                case 0x3F:
                    buf = PrepRespBuf(44);
                    ModeSensePage04(dev, buf, 4);
                    ModeSensePage00(dev, buf, 28);
                    buf[0] = 43;
                    buf[1] = 0;
                    buf[2] = 0;
                    buf[3] = 0;
                    break;

                default:
                    ColoredConsole.WriteLine($"ACSI: unsupported MODE SENSE page ${_command[2]:X2}.", ConfigOptions.DebugModes.Information);
                    _status = HD_STATUS_ERROR;
                    dev.LastError = HD_REQSENS_INVARG;
                    return;
            }

            _status = HD_STATUS_OK;
            dev.LastError = HD_REQSENS_OK;
        }

        // Vendor-specific page 00h (enough for AHDI 5.0's HDX tool)
        static void ModeSensePage00(AcsiDisk dev, byte[] buf, int o)
        {
            Array.Clear(buf, o, 16);
            buf[o + 1] = 14;
            buf[o + 3] = 8;
            buf[o + 5] = (byte)(dev.SectorCount >> 16);
            buf[o + 6] = (byte)(dev.SectorCount >> 8);
            buf[o + 7] = (byte)dev.SectorCount;
            buf[o + 10] = 2;    // block size 512
        }

        // Rigid disk geometry page
        static void ModeSensePage04(AcsiDisk dev, byte[] buf, int o)
        {
            Array.Clear(buf, o, 24);
            buf[o + 0] = 4;
            buf[o + 1] = 22;
            buf[o + 2] = (byte)(dev.SectorCount >> 23);   // cylinders (128 heads assumed)
            buf[o + 3] = (byte)(dev.SectorCount >> 15);
            buf[o + 4] = (byte)(dev.SectorCount >> 7);
            buf[o + 5] = 128;                             // heads
            buf[o + 20] = 0x1C;                           // rotation rate 7200
            buf[o + 21] = 0x20;
        }

        static void CmdReportLuns()
        {
            AcsiDisk dev = TargetDev;
            byte[] buf = PrepRespBuf(16);
            Array.Clear(buf, 0, 16);
            buf[3] = 8;     // LUN list length: one LUN, 8 bytes

            _status = HD_STATUS_OK;
            dev.LastError = HD_REQSENS_OK;
            dev.SetLastBlockAddr = false;
        }

        static void CmdReadCapacity()
        {
            AcsiDisk dev = TargetDev;
            long sectors = dev.SectorCount - 1;
            byte[] buf = PrepRespBuf(8);

            buf[0] = (byte)(sectors >> 24);
            buf[1] = (byte)(sectors >> 16);
            buf[2] = (byte)(sectors >> 8);
            buf[3] = (byte)sectors;
            buf[4] = 0;
            buf[5] = 0;
            buf[6] = SectorSize >> 8;
            buf[7] = SectorSize & 0xFF;

            _status = HD_STATUS_OK;
            dev.LastError = HD_REQSENS_OK;
            dev.SetLastBlockAddr = false;
        }

        static void CmdReadSector()
        {
            AcsiDisk dev = TargetDev;
            long lba = GetLba();
            int count = GetCount();
            dev.LastBlockAddr = lba;

            if (lba + count > dev.SectorCount)
            {
                _status = HD_STATUS_ERROR;
                dev.LastError = HD_REQSENS_INVADDR;
            }
            else
            {
                byte[] buf = PrepRespBuf(count * SectorSize);
                try
                {
                    dev.Image.Seek(lba * SectorSize, SeekOrigin.Begin);
                    dev.Image.ReadExactly(buf, 0, count * SectorSize);
                    _status = HD_STATUS_OK;
                    dev.LastError = HD_REQSENS_OK;

                    // Boot disabled: hand the DMA a root sector whose words no longer sum to
                    // $1234, so TOS skips the DMA boot. Only the delivered copy is touched —
                    // the image keeps its boot sector, and a driver loaded from floppy still
                    // sees a valid partition table (the checksum word is not partition data).
                    if (lba == 0 && count > 0 && !ConfigOptions.RunninConfig.BootFromHardDisk)
                    {
                        ushort sum = 0;
                        for (int i = 0; i < SectorSize; i += 2)
                            sum += (ushort)((buf[i] << 8) | buf[i + 1]);
                        if (sum == 0x1234)
                            buf[SectorSize - 1] ^= 0xFF;
                    }
                }
                catch (Exception ex)
                {
                    ColoredConsole.WriteLine($"ACSI: read error at LBA {lba}: {ex.Message}", ConfigOptions.DebugModes.Quiet);
                    _dataLen = 0;
                    _status = HD_STATUS_ERROR;
                    dev.LastError = HD_REQSENS_NOSECTOR;
                }
            }

            dev.SetLastBlockAddr = true;
        }

        static void CmdWriteSector()
        {
            AcsiDisk dev = TargetDev;
            long lba = GetLba();
            int count = GetCount();
            dev.LastBlockAddr = lba;

            if (lba + count > dev.SectorCount)
            {
                _status = HD_STATUS_ERROR;
                dev.LastError = HD_REQSENS_INVADDR;
            }
            else if (count == 0)
            {
                _status = HD_STATUS_ERROR;
                dev.LastError = HD_REQSENS_WRITEERR;
            }
            else
            {
                // The data comes from RAM when the DMA transfer runs; only note what to do.
                // The target sector is captured here, not read back from the device later:
                // any command in between (a REQUEST SENSE, say) would move LastBlockAddr.
                _dataLen = count * SectorSize;
                _pendingWrite = dev;
                _pendingWriteLba = lba;
                _status = HD_STATUS_OK;
                dev.LastError = HD_REQSENS_OK;
            }

            dev.SetLastBlockAddr = true;
        }

        // ---------------- DMA ----------------

        /// <summary>
        /// Called by WD1772 when a write to $FF8606 enables the DMA (bits 6-7 dropping to 0)
        /// while data from an HDC command is still waiting.
        /// </summary>
        public static void DmaTransferIfPending()
        {
            if (EmulationOn && _dataLen > 0)
                DmaTransfer();
        }

        /// <summary>
        /// Moves the prepared data between RAM and the controller through the DMA: reads land
        /// in RAM at the DMA address, writes are pulled from RAM into the image file. The DMA
        /// address counter advances; the IRQ is re-raised after a short delay (see Tick).
        /// </summary>
        static void DmaTransfer()
        {
            ushort mode = WD1772.DmaMode;

            // DMA disabled (bits 6-7 set) or nothing to transfer
            if ((mode & 0xC0) != 0 || _dataLen == 0)
                return;

            bool dirToDevice = (mode & 0x100) != 0;      // 1 = RAM -> device
            bool haveWrite = _pendingWrite != null;

            if (haveWrite != dirToDevice)
            {
                ColoredConsole.WriteLine("ACSI: DMA direction does not match the SCSI command.", ConfigOptions.DebugModes.Information);
                return;
            }

            uint dmaAddr = WD1772.DmaAddress;

            // The DMA reaches RAM only; a transfer programmed anywhere else is what the DMA
            // status' error bit is for (the controller does not quietly transfer nothing).
            if (!ASEMain._mem.IsRamArea(dmaAddr, (uint)_dataLen))
            {
                ColoredConsole.WriteLine($"ACSI: DMA outside RAM (${dmaAddr:X6}+{_dataLen}), transfer refused.", ConfigOptions.DebugModes.Quiet);
                _dmaError = true;
                _status = HD_STATUS_ERROR;
                _pendingWrite = null;
                _dataLen = 0;
                WD1772.SetDmaError(_dmaError);
                WD1772.SetHdcIrq(false);
                _irqRaiseClock = CPU._moira.Clock + TRANSFER_IRQ_DELAY_CYCLES;
                return;
            }

            if (haveWrite)
            {
                AcsiDisk dev = _pendingWrite;
                _pendingWrite = null;

                if (dev.ReadOnly)
                {
                    ColoredConsole.WriteLine("ACSI: write refused, the image file is read-only.", ConfigOptions.DebugModes.Quiet);
                    _status = HD_STATUS_ERROR;
                    dev.LastError = HD_REQSENS_WRITEERR;
                }
                else
                {
                    try
                    {
                        byte[] data = new byte[_dataLen];
                        ASEMain._mem.ReadBytes(dmaAddr, data, 0, _dataLen);
                        dev.Image.Seek(_pendingWriteLba * SectorSize, SeekOrigin.Begin);
                        dev.Image.Write(data, 0, _dataLen);
                        dev.Image.Flush();
                    }
                    catch (Exception ex)
                    {
                        ColoredConsole.WriteLine($"ACSI: write error at LBA {_pendingWriteLba}: {ex.Message}", ConfigOptions.DebugModes.Quiet);
                        _status = HD_STATUS_ERROR;
                        dev.LastError = HD_REQSENS_WRITEERR;
                    }
                }
            }
            else
            {
                ASEMain._mem.WriteBytes(dmaAddr, _buffer, 0, _dataLen);
            }

            WD1772.DmaAddress = dmaAddr + (uint)_dataLen;
            WD1772.ConsumeDmaSectors(_dataLen);
            _dataLen = 0;

            WD1772.SetDmaError(_dmaError);

            // The transfer itself is instant, but the IRQ that reports it follows after a
            // short, measured-on-real-hardware-ish delay (some programs rely on the gap).
            WD1772.SetHdcIrq(false);
            _irqRaiseClock = CPU._moira.Clock + TRANSFER_IRQ_DELAY_CYCLES;
        }

        /// <summary>Raises the post-transfer IRQ once its due cycle is reached. Called from
        /// WD1772.Tick every CPU slice; a single compare when nothing is pending.</summary>
        public static void Tick()
        {
            if (_irqRaiseClock >= 0 && CPU._moira.Clock >= _irqRaiseClock)
            {
                _irqRaiseClock = -1;
                WD1772.SetHdcIrq(true);
            }
        }

        // ---------------- Partition table ----------------

        /// <summary>
        /// Primary-partition count from the image's root sector — Atari (4 entries at $1C6,
        /// flag bit 0 = exists) or DOS MBR (4 entries at $1BE, non-zero type) layout. Used to
        /// place the GEMDOS drive letter after the image's partitions.
        /// </summary>
        static int CountPartitions(AcsiDisk dev)
        {
            byte[] root = new byte[SectorSize];

            try
            {
                dev.Image.Seek(0, SeekOrigin.Begin);
                dev.Image.ReadExactly(root, 0, SectorSize);
            }
            catch
            {
                return 0;
            }

            int parts = 0;

            if (root[0x1FE] == 0x55 && root[0x1FF] == 0xAA)
            {
                // DOS MBR
                for (int i = 0; i < 4; i++)
                {
                    if (root[0x1BE + i * 16 + 4] != 0)
                        parts++;
                }
            }
            else
            {
                // Atari root sector
                for (int i = 0; i < 4; i++)
                {
                    if ((root[0x1C6 + i * 12] & 0x01) != 0)
                        parts++;
                }
            }

            return parts;
        }
    }
}
