/*
 * 
 * Western Digital WD1772 FDC emulation for Atari ST
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using Avalonia.Threading;
using static ASE.Config;

namespace ASE
{
    public static class WD1772
    {
        // Registros
        static byte commandRegister;
        static byte trackRegister;
        static byte sectorRegister;
        static byte statusRegister;
        static byte dataRegister;
        static ushort dmaModeRegister;
        static byte dmaSectorCount;
        static uint dmaAddress;
        static ushort prevMode;

        // Last value read from or written to $FF8604 while it addressed the FDC/HDC registers
        // (that is, not the sector count). The DMA chip has no storage for the unused bits of
        // $FF8604/$FF8606, so they read back as whatever went through that path last -- and the
        // sector count register cannot be read at all, it answers with this instead. Verified on
        // a real STF by Hatari (fdc.c, ff8604_recent_val).
        static ushort ff8604RecentVal;

        // Bytes still owed to the sector the DMA counter is currently on. The counter steps once
        // per 512 bytes actually moved, never once per command, which is what keeps a 4-byte
        // INQUIRY from consuming a whole sector of the count the host programmed. The FDC path
        // tracks the same position through its FIFO (stxDmaByteCount); ResetDma restarts both.
        static int dmaBytesInSector;

        // Estado
        static int currentDrive = -1;
        static int currentSide;

        // Head position, per drive. Each drive parks its own head where it left it: the FDC has a
        // single track register, and the TOS floppy driver reloads it with the track it believes
        // the selected drive is at, skipping the seek when that already matches what it wants. A
        // shared head therefore compared against the *other* drive's position the moment TOS
        // alternated between A: and B:, and the access came back Record Not Found. Slot 2 is the
        // scratch the step pulses fall into with no drive selected -- nothing out there moves.
        static readonly int[] headTracks = new int[3];
        static int HeadIndex => currentDrive == 0 ? 0 : currentDrive == 1 ? 1 : 2;
        static int headTrack
        {
            get => headTracks[HeadIndex];
            set => headTracks[HeadIndex] = value;
        }

        static int stepDirection = 1;
        static bool dmaError;
        static int nextIdSector = 0;    // rotating sector index for READ ADDRESS

        // The FDC's INTRQ and the ACSI controller's IRQ are combined onto the same MFP line
        // (GPIP5, active low): the line is asserted while EITHER source is. Reading the FDC
        // status only retires the FDC's side; the HDC's is managed from Acsi.cs.
        static bool fdcIrq;
        static bool hdcIrq;

        // Active disk according to the drive selection made through the YM2149's port A
        static FloppyImage ActiveDrive => currentDrive == 1 ? ASEMain.driveB : ASEMain.driveA;

        // Drive B is a box on the end of a cable that may simply not be there (File > Connect
        // drive B:). Unplugged, the select line reaches nothing: TR00, INDEX and WPT are
        // open-collector inputs pulled inactive at the FDC, so they all read as false and every
        // command that needs a drive to answer fails. That is what TOS' boot-time probe reads to
        // decide whether this machine has a second floppy at all -- RESTORE steps out looking for
        // TR00 and gets a seek error instead.
        static bool DriveBConnected => ConfigOptions.RunninConfig.DriveBEnabled;

        /// <summary>Whether a drive is actually on the other end of the current selection.</summary>
        static bool DriveSelected => currentDrive == 0 || (currentDrive == 1 && DriveBConnected);

        // Head position ("A: T05 S09"), captured when each command starts and shown
        // in the status bar next to the LED while the drive has activity.
        static string ActivityText =>
            $"{(currentDrive == 1 ? 'B' : 'A')}: T{headTrack:00} S{sectorRegister:00}";

        // Bits Status
        private const byte STATUS_BUSY = 0x01;
        private const byte STATUS_DRQ = 0x02;
        private const byte STATUS_LOST_DATA = 0x04;
        private const byte STATUS_CRC_ERROR = 0x08;
        private const byte STATUS_RECORD_NOT_FOUND = 0x10;
        private const byte STATUS_RECORD_TYPE = 0x20;
        // Same bit read in Type I form, where it means the spin-up sequence completed
        private const byte STATUS_SPIN_UP = 0x20;
        private const byte STATUS_WRITE_PROTECT = 0x40;
        private const byte STATUS_MOTOR_ON = 0x80;
        private const byte STATUS_INDEX_PULSE = 0x02;
        private const byte STATUS_TRACK0 = 0x04;

        // Bits DMA
        private const int DMA_A0 = 1;
        private const int DMA_A1 = 2;
        private const int DMA_HDC_SELECT = 3;
        private const int DMA_SECTOR_COUNT_REG = 4;
        private const int DMA_RW_DIRECTION = 8;

        // Comandos
        private const byte CMD_RESTORE = 0x00;
        private const byte CMD_SEEK = 0x10;
        private const byte CMD_STEP = 0x20;
        private const byte CMD_STEP_IN = 0x40;
        private const byte CMD_STEP_OUT = 0x60;
        private const byte CMD_READ_SECTOR = 0x80;
        private const byte CMD_WRITE_SECTOR = 0xA0;
        private const byte CMD_READ_ADDRESS = 0xC0;
        private const byte CMD_READ_TRACK = 0xE0;
        private const byte CMD_WRITE_TRACK = 0xF0;
        private const byte CMD_FORCE_INTERRUPT = 0xD0;

        public static void Reset()
        {
            commandRegister = 0;
            trackRegister = 0;
            sectorRegister = 1;
            dataRegister = 0;
            dmaAddress = 0;
            currentDrive = -1;
            currentSide = 0;
            Array.Clear(headTracks, 0, headTracks.Length);
            stepDirection = 1;
            dmaSectorCount = 0;
            dmaBytesInSector = DMA_SECTOR_SIZE;
            ff8604RecentVal = 0;
            statusRegister = 0;
            motorStopClock = 0;
            lastCommandTypeI = true;
            stxOp = null;
            stxDmaByteCount = 0;
            dmaFifoCount = 0;

            // A pending disk change has nothing left to notify after a reset: TOS re-reads the
            // disk from scratch on the way up.
            ASEMain.driveA.ClearDiskTransition();
            ASEMain.driveB.ClearDiskTransition();

            fdcIrq = false;
            hdcIrq = false;
            Acsi.ResetCommandStatus();

            if (ASEMain._mfp != null)
                ASEMain._mfp.SetGPIOBit(5, true);
        }

        public static void WriteByte(uint address, byte value)
        {
            switch (address)
            {
                case 0xFF8604: 
                    break;
                case 0xFF8605: 
                    WriteToFDCOrSectorCount(value); 
                    break;
                case 0xFF8606: 
                    dmaModeRegister = (ushort)((value << 8) | (dmaModeRegister & 0x00FF)); 
                    HandleDMAModeChange(); 
                    break;
                case 0xFF8607: 
                    dmaModeRegister = (ushort)((dmaModeRegister & 0xFF00) | value); 
                    HandleDMAModeChange(); 
                    break;
                case 0xFF8609: 
                    dmaAddress = (dmaAddress & 0x00FFFF) | (((uint)value & 0x3F) << 16); 
                    break;
                case 0xFF860B:
                    dmaAddress = RippleCarry(dmaAddress, (dmaAddress & 0xFF00FF) | ((uint)value << 8));
                    break;
                case 0xFF860D:
                    dmaAddress = RippleCarry(dmaAddress, (dmaAddress & 0xFFFF00) | ((uint)value & 0xFE));
                    break;
            }
        }

        public static void WriteWord(uint address, ushort value)
        {
            switch (address)
            {
                case 0xFF8604:
                    WriteToFDCOrSectorCount((byte)(value & 0xFF));
                    return;

                case 0xFF8606:
                    dmaModeRegister = value;
                    HandleDMAModeChange();
                    return;

                case 0xFF8608: // -> $FF8609 High
                    WriteByte(0xFF8609, (byte)(value & 0xFF));
                    return;

                case 0xFF860A: // -> $FF860B Mid
                    WriteByte(0xFF860B, (byte)(value & 0xFF));
                    return;

                case 0xFF860C: // -> $FF860D Low
                    WriteByte(0xFF860D, (byte)(value & 0xFF));
                    return;
            }

            WriteByte(address, (byte)(value >> 8));
            WriteByte(address + 1, (byte)(value & 0xFF));
        }

        public static byte ReadByte(uint address)
        {
            // Bring any STX command in progress up to date with the CPU clock, so the
            // status/DMA registers reflect the exact state at the instant of the poll.
            if (stxOp != null)
                ProcessStxOp(CPU._moira.Clock);

            switch (address)
            {
                case 0xFF8604: 
                    return ASEMain._mem.Ports[address - Memory.PortsBase];
                case 0xFF8605: 
                    return ReadFromFDCOrSectorCount();
                case 0xFF8606: 
                    return (byte)(GetDMAStatus() >> 8);
                case 0xFF8607: 
                    return (byte)(GetDMAStatus() & 0xFF);
                case 0xFF8609: 
                    return (byte)((dmaAddress >> 16) & 0x3F);
                case 0xFF860B: 
                    return (byte)((dmaAddress >> 8) & 0xFF);
                case 0xFF860D: 
                    return (byte)(dmaAddress & 0xFE);
                default: 
                    return ASEMain._mem.Ports[address - Memory.PortsBase];
            }
        }

        public static ushort ReadWord(uint address)
        {
            if (stxOp != null)
                ProcessStxOp(CPU._moira.Clock);

            switch (address)
            {
                case 0xFF8604:
                    // The high byte is not driven by the FDC/HDC: it reads back as zero, not as
                    // open bus. A driver that tests the whole word ("move.w $FFFF8604,d0" then
                    // tst/cmp, with no masking -- a common shape) would see a failed command for
                    // every successful one if this returned $FF00.
                    return ReadFromFDCOrSectorCount();
                case 0xFF8606: 
                    return GetDMAStatus();
                case 0xFF8608: 
                    return (ushort)(ReadByte(0xFF8609));
                case 0xFF860A: 
                    return (ushort)(ReadByte(0xFF860B));
                case 0xFF860C: 
                    return (ushort)(ReadByte(0xFF860D));
                default:
                    return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
            }
        }

        private static void HandleDMAModeChange()
        {
            // Toggling bit 8 (the transfer direction) resets the DMA chip. This is not a corner
            // case: it is how every ACSI driver arms a transfer. The boot sector of an
            // ICD-formatted disk writes $0190 then $0090 to $FF8606 for exactly this reset,
            // right before programming the sector count, and AHDI and the drivers derived from
            // it do the same. Reproducing only half of it (clearing the error bit, as this did
            // before) left the sector counter and the 16-byte FIFO carrying over whatever the
            // previous command had put there.
            if (((prevMode ^ dmaModeRegister) & 0x0100) != 0)
                ResetDma();

            // DMA just enabled (bits 6-7 dropping to 0) with ACSI data still waiting: this is
            // the moment the transfer of an already-executed HDC command actually runs.
            if ((prevMode & 0x00C0) != 0 && (dmaModeRegister & 0x00C0) == 0)
                Acsi.DmaTransferIfPending();

            prevMode = dmaModeRegister;
        }

        /// <summary>
        /// DMA reset, as produced by toggling bit 8 of the mode register (Hatari's
        /// <c>FDC_ResetDMA</c>, verified on a real STF): the 16-byte FIFO is emptied, the byte
        /// count inside the current sector restarts, the sector counter goes to zero and the
        /// HDC's command status is cleared.
        /// </summary>
        static void ResetDma()
        {
            dmaFifoCount = 0;
            stxDmaByteCount = 0;
            dmaBytesInSector = DMA_SECTOR_SIZE;
            dmaSectorCount = 0;
            dmaError = false;
            Acsi.ResetCommandStatus();
        }

        /// <summary>
        /// The STF builds its DMA address register out of a ripple carry adder, so writing the
        /// low or middle byte can carry into the byte above it: when bit 7 (or bit 15) goes from
        /// 1 to 0, the next byte up is incremented. Verified on real hardware (Hatari's
        /// <c>FDC_DmaAddress_WriteByte</c>); the STE's MCU does not do this.
        /// </summary>
        static uint RippleCarry(uint oldAddr, uint newAddr)
        {
            if (ConfigOptions.RunninConfig.STModel != ConfigOptions.STModels.ST)
                return newAddr;

            if ((oldAddr & 0x80) != 0 && (newAddr & 0x80) == 0)
                newAddr += 0x100;
            else if ((oldAddr & 0x8000) != 0 && (newAddr & 0x8000) == 0)
                newAddr += 0x10000;

            return newAddr & DMA_ADDRESS_MASK;
        }

        // ==================== ACSI (HDC) plumbing ====================
        // The hard disk controller shares this chip's DMA and interrupt wiring; Acsi.cs uses
        // these accessors instead of owning copies of the registers.

        /// <summary>Current DMA mode register ($FF8606), read by Acsi to check enable/direction.</summary>
        internal static ushort DmaMode => dmaModeRegister;

        /// <summary>DMA address counter ($FF8609/0B/0D); Acsi advances it past each transfer.</summary>
        internal static uint DmaAddress
        {
            get => dmaAddress;
            set => dmaAddress = value & DMA_ADDRESS_MASK;
        }

        // The DMA address is word-aligned (bit 0 of $FF860D always reads back 0) and, on a
        // machine limited to 4MB, its high byte is masked with $3F -- the same masks the
        // per-byte writes above already apply, so a transfer advancing the counter must not
        // escape them either.
        const uint DMA_ADDRESS_MASK = 0x3FFFFE;

        /// <summary>Bytes per sector as far as the DMA sector counter is concerned.</summary>
        const int DMA_SECTOR_SIZE = 512;

        /// <summary>
        /// Accounts a completed ACSI transfer against the DMA sector counter ($FF8604 with the
        /// mode register's sector-count bit set), exactly as the FDC paths already do.
        /// <para>
        /// The counter is the chip's own record of how much of the programmed transfer is still
        /// outstanding, and the host reads it back — directly, and through bit 1 of the DMA status
        /// register, which is simply "sector count is not zero". A driver uses it to confirm the
        /// transfer completed. Leaving it untouched after an ACSI transfer told every driver that
        /// the DMA had not finished, whatever had actually been moved into RAM: the ICD driver
        /// then abandons the rest of a multi-command read, so a large file arrives truncated with
        /// the tail of the destination buffer left at zero. That is silent unless the data is
        /// checked — a game whose module got cut in half still plays, just with half its
        /// instruments turned into square waves and silence.
        /// </para>
        /// </summary>
        internal static void ConsumeDmaSectors(int bytes)
        {
            if (bytes <= 0)
                return;

            // The counter steps once per 512 bytes actually moved and keeps the remainder for
            // the next transfer, exactly as the chip does through its FIFO. Rounding up instead
            // (the first version of this) made every short command -- INQUIRY, REQUEST SENSE,
            // MODE SENSE, each a handful of bytes -- swallow a whole sector of the count the
            // host had programmed, so a driver that probes the device before reading found its
            // transfer already "finished" before a single sector had moved.
            dmaBytesInSector -= bytes;
            while (dmaBytesInSector <= 0)
            {
                dmaBytesInSector += DMA_SECTOR_SIZE;
                if (dmaSectorCount > 0)
                    dmaSectorCount--;
            }
        }

        /// <summary>DMA status bit 0 (set = no error), shared between FDC and HDC transfers.</summary>
        internal static void SetDmaError(bool error) => dmaError = error;

        /// <summary>Asserts or retires the ACSI side of the shared GPIP5 interrupt line.</summary>
        internal static void SetHdcIrq(bool state)
        {
            hdcIrq = state;
            UpdateSharedIrq();
        }

        static void UpdateSharedIrq()
        {
            // Active low at the MFP: pulled low while either chip requests service
            ASEMain._mfp?.SetGPIOBit(5, !(fdcIrq || hdcIrq));
        }

        public static void SetDriveAndSide(int drive, int side)
        {
            if (currentDrive != drive || currentSide != side)
            {
                currentDrive = drive;
                currentSide = side;
            }
        }

        private static ushort GetDMAStatus()
        {
            ushort status = 0;

            if (!dmaError) 
                status |= 0x0001;
            if (dmaSectorCount != 0) 
                status |= 0x0002;
            if ((statusRegister & STATUS_DRQ) != 0)
                status |= 0x0004;

            // Bits 3-15 are not driven by anything: they read back as the last value that went
            // through $FF8604 in FDC/HDC mode (verified on a real STF).
            return (ushort)(status | (ff8604RecentVal & 0xFFF8));
        }

        private static void WriteToFDCOrSectorCount(byte value)
        {
            bool selectSectorCount = ((dmaModeRegister >> DMA_SECTOR_COUNT_REG) & 1) == 1;

            if (selectSectorCount)
            {
                dmaSectorCount = value;
                return;
            }

            // Anything else written here is an FDC/HDC register access, and the byte stays
            // visible in the undriven bits of $FF8604/$FF8606 afterwards.
            ff8604RecentVal = (ushort)((ff8604RecentVal & 0xFF00) | value);

            // HDC selected: the byte goes to the ACSI controller, whose A1 line (bit 1 of
            // the mode register) tells a packet's first byte from the rest.
            if (((dmaModeRegister >> DMA_HDC_SELECT) & 1) == 1)
            {
                Acsi.WriteCommandByte(dmaModeRegister & 0x07, value);
                return;
            }

            int fdcRegister = (dmaModeRegister >> DMA_A0) & 0x03;

            switch (fdcRegister)
            {
                case 0: // Command
                    ExecuteCommand(value);
                    break;
                case 1: // Track
                    trackRegister = value;
                    break;
                case 2: // Sector
                    sectorRegister = value;
                    break;
                case 3: // Data
                    dataRegister = value;
                    break;
            }
        }

        private static byte ReadFromFDCOrSectorCount()
        {
            bool selectSectorCount = ((dmaModeRegister >> DMA_SECTOR_COUNT_REG) & 1) == 1;

            // The sector count register is write-only: reading it back gives the last value that
            // went through $FF8604 in FDC/HDC mode (verified on a real STF), not the counter.
            if (selectSectorCount)
                return (byte)ff8604RecentVal;

            byte value;

            // HDC selected: the ACSI controller's status byte (0xFF with no emulation on,
            // like an empty bus)
            if (((dmaModeRegister >> DMA_HDC_SELECT) & 1) == 1)
            {
                value = Acsi.ReadStatus();
            }
            else
            {
                int fdcRegister = (dmaModeRegister >> DMA_A0) & 0x03;
                switch (fdcRegister)
                {
                    case 0: // STATUS REGISTER
                        ClearInterrupt();
                        value = ComposeStatus();
                        break;
                    case 1:
                        value = trackRegister;
                        break;
                    case 2:
                        value = sectorRegister;
                        break;
                    default:
                        value = dataRegister;
                        break;
                }
            }

            ff8604RecentVal = (ushort)((ff8604RecentVal & 0xFF00) | value);
            return value;
        }

        // 300 RPM → 1 revolution per 200 ms → 1,600,000 CPU cycles at 8 MHz.
        private const long CYCLES_PER_REVOLUTION = 1_600_000;
        // The WD1772 drops the motor line after 9 revolutions of inactivity (~1.8 s)
        private const long MOTOR_SPINDOWN_CYCLES = 9 * CYCLES_PER_REVOLUTION;
        // Index pulse is active during a small window (~2.5 ms) of each revolution
        private const long INDEX_PULSE_WIDTH_CYCLES = 20_000;

        // Motor state and disk rotation derive from the absolute CPU clock, so status
        // bits and sector positions stay exact no matter how often the FDC is ticked.
        static long motorStopClock = 0;
        static bool MotorOn => CPU._moira.Clock < motorStopClock;

        /// <summary>
        /// Whether anything is turning in front of the head right now. The drive answering the
        /// select line puts an index pulse on the bus once per revolution while its motor runs,
        /// with or without a disk in it: on a 3.5" drive the sensor reads the spindle, not the
        /// medium, which is why TOS finds an empty drive A: at boot exactly as it finds a loaded
        /// one. With drive B unplugged — or no drive selected at all — the line has nothing
        /// driving it and no pulse ever arrives, which is what stalls the spin-up sequence
        /// (see ExecuteCommand).
        /// </summary>
        static bool IndexPulsesPresent => MotorOn && DriveSelected;
        // Bit 1 of the status is index pulse after Type I / Force Interrupt, DRQ after Type II/III
        static bool lastCommandTypeI = true;

        /// <summary>Advances any STX command in progress (timed DMA delivery). Called
        /// from the CPU slice loop so bytes land within a few cycles of their due time.</summary>
        public static void Tick()
        {
            if (stxOp != null)
                ProcessStxOp(CPU._moira.Clock);

            // Pending ACSI post-transfer IRQ (a single compare when idle)
            Acsi.Tick();
        }

        /// <summary>
        /// Completes any timed STX command in flight right now: the remaining bytes are
        /// delivered to the DMA and the command finishes (status/IRQ included). Used before
        /// saving a snapshot so no half-delivered transfer has to be serialized. Note that a
        /// protection timing the sector data would see the tail arrive instantly, so avoid
        /// snapshotting in the middle of a protected load.
        /// </summary>
        public static void FlushPendingOp()
        {
            // FinishStxOp can chain the next sector of a multi-sector command; keep
            // flushing until the whole chain has completed (the DMA count bounds it).
            // A command parked waiting for index pulses is left alone: it carries no data
            // and its whole point is that it never completes, so it travels in the snapshot
            // as the flag it is (see SaveState).
            while (stxOp != null && !stxOp.WaitingForIndex)
                ProcessStxOp(stxOp.CompleteClock);
        }

        // ==================== Snapshot ====================

        public static void SaveState(Snapshot.Writer w)
        {
            w.U8(commandRegister);
            w.U8(trackRegister);
            w.U8(sectorRegister);
            w.U8(statusRegister);
            w.U8(dataRegister);
            w.U16(dmaModeRegister);
            w.U16(prevMode);
            w.U8(dmaSectorCount);
            w.Bool(lastCommandTypeI);
            w.Bool(dmaError);
            w.U8((byte)(sbyte)currentDrive);
            w.U8((byte)currentSide);
            w.U8((byte)(sbyte)stepDirection);
            w.I32(headTrack);
            w.I32(nextIdSector);
            w.U32(dmaAddress);
            w.I64(motorStopClock);
            w.I32(stxDmaByteCount);

            // Appended for back-compat: the two sources behind the shared GPIP5 line
            w.Bool(fdcIrq);
            w.Bool(hdcIrq);

            // Appended: the DMA chip's undriven-bit latch and its position inside the sector
            w.U16(ff8604RecentVal);
            w.I32(dmaBytesInSector);

            // Appended: both heads (the headTrack above is only the selected drive's)
            w.I32(headTracks[0]);
            w.I32(headTracks[1]);

            // Appended: a Type I command parked in the spin-up sequence with nothing answering
            // the select line. It is the one operation FlushPendingOp does not complete before
            // saving, so without this the restored machine would come back busy with nothing
            // left to retire it (a Force Interrupt still would, as on the real chip).
            w.Bool(stxOp != null && stxOp.WaitingForIndex);
        }

        public static void LoadState(Snapshot.Reader r)
        {
            commandRegister = r.U8();
            trackRegister = r.U8();
            sectorRegister = r.U8();
            statusRegister = r.U8();
            dataRegister = r.U8();
            dmaModeRegister = r.U16();
            prevMode = r.U16();
            dmaSectorCount = r.U8();
            lastCommandTypeI = r.Bool();
            dmaError = r.Bool();
            currentDrive = (sbyte)r.U8();
            currentSide = r.U8();
            stepDirection = (sbyte)r.U8();
            headTrack = r.I32();
            nextIdSector = r.I32();
            dmaAddress = r.U32();
            motorStopClock = r.I64();
            stxDmaByteCount = r.I32();

            // Older snapshots stop here: derive nothing, the MFP section already carries the
            // resulting GPIP5 level, and the sources start clear.
            fdcIrq = r.Remaining > 0 && r.Bool();
            hdcIrq = r.Remaining > 0 && r.Bool();

            ff8604RecentVal = r.Remaining > 0 ? r.U16() : (ushort)0;
            dmaBytesInSector = r.Remaining > 0 ? r.I32() : DMA_SECTOR_SIZE;

            // Both heads, appended after headTrack (which has already landed in the selected
            // drive's slot): an older snapshot leaves the other drive's head where it was.
            if (r.Remaining > 0)
            {
                headTracks[0] = r.I32();
                headTracks[1] = r.I32();
            }

            // An in-flight STX operation is never serialized (it completes before saving)
            stxOp = null;

            // ...with one exception: a Type I parked in the spin-up sequence, which by
            // definition never completes and so has to be put back (see SaveState).
            if (r.Remaining > 0 && r.Bool())
                stxOp = new StxOp { CompleteClock = long.MaxValue, WaitingForIndex = true };
        }

        /// <summary>
        /// Composes the status byte the CPU sees: the stored command result plus the bits the
        /// WD1772 takes live from its input lines — bit 7 (motor on) always, and after a Type I
        /// command also bit 1 (index pulse), bit 2 (track 0) and bit 6 (write protect). Loaders
        /// poll those transitions and TOS polls bit 6 to detect a disk change, so they can't be
        /// a frozen snapshot of the moment the command ended.
        /// </summary>
        private static byte ComposeStatus()
        {
            byte st = statusRegister;

            bool motorOn = MotorOn;

            if (motorOn)
                st |= STATUS_MOTOR_ON;
            else
                st &= unchecked((byte)~STATUS_MOTOR_ON);

            if (lastCommandTypeI)
            {
                // In Type I form the chip presents the drive's input lines as they are at the
                // instant of the read, not as they were when the command ended: bit 1 (index
                // pulse), bit 2 (track 0) and bit 6 (write protect). Programs poll them without
                // issuing a new command — and bit 6 is where TOS looks to notice a disk change
                // (see FloppyImage.SignalDiskTransition), so it can't be a frozen snapshot.
                st &= unchecked((byte)~(STATUS_INDEX_PULSE | STATUS_TRACK0 | STATUS_WRITE_PROTECT));

                // With no drive selected -- or drive B selected while it is unplugged -- the
                // TR00, INDEX and WPRT inputs have nothing driving them and all read inactive
                if (!DriveSelected)
                    return st;

                if (headTrack == 0)
                    st |= STATUS_TRACK0;

                // No index pulses without a spinning motor (see IndexPulsesPresent: an empty
                // but connected drive still pulses, its sensor sits on the motor's spindle)
                if (IndexPulsesPresent &&
                    CPU._moira.Clock % CYCLES_PER_REVOLUTION < INDEX_PULSE_WIDTH_CYCLES)
                    st |= STATUS_INDEX_PULSE;

                if (WriteProtectAsserted)
                    st |= STATUS_WRITE_PROTECT;
            }

            return st;
        }

        /// <summary>
        /// State of the selected drive's write protect line. An empty drive is indistinguishable
        /// from a write-protected disk (one sensor, no disk-change line on the ST), and the line
        /// is also held high for a few VBLs after a disk is inserted or ejected: that transition
        /// is what TOS watches to detect a disk change, see
        /// <see cref="FloppyImage.SignalDiskTransition"/>.
        /// </summary>
        private static bool WriteProtectAsserted =>
            !ActiveDrive.HasDisk || ActiveDrive.WriteProtected || ActiveDrive.WpTransitionActive;

        private static void UpdateTypeIStatus()
        {
            statusRegister = 0;
            statusRegister |= STATUS_SPIN_UP;

            // Nothing answering the select line: the drive inputs stay inactive
            if (!DriveSelected)
                return;

            if (headTrack == 0)
                statusRegister |= STATUS_TRACK0;
            if (WriteProtectAsserted)
                statusRegister |= STATUS_WRITE_PROTECT;
        }

        private static void ExecuteCommand(byte command)
        {
            // Force Interrupt terminates a command in progress; any other command written
            // while the FDC is busy is ignored, like on the real WD1772.
            bool forceInterrupt = (command & 0xF0) == CMD_FORCE_INTERRUPT;

            if (forceInterrupt)
                stxOp = null;
            else if (stxOp != null)
                return;

            // A Force Interrupt has to know whether it interrupted anything: it keeps the
            // status of a command in progress and only rewrites it when the chip was idle
            // (see the Type IV path below), so both are captured before the reset.
            bool wasBusy = (statusRegister & STATUS_BUSY) != 0;
            byte statusBeforeCommand = statusRegister;

            commandRegister = command;

            // Type I commands are 0x00-0x7F. Force Interrupt presents Type I status too, but
            // only when it did not interrupt anything — otherwise the interrupted command's
            // status stays as it is, in its own form.
            if (!forceInterrupt)
                lastCommandTypeI = command < 0x80;
            else if (!wasBusy)
                lastCommandTypeI = true;

            // Every command spins up the motor; it stays on for ~9 revolutions afterwards.
            // Whether it was already spinning decides the Type II/III spin-up delay on STX.
            bool motorWasOn = MotorOn;
            motorStopClock = CPU._moira.Clock + MOTOR_SPINDOWN_CYCLES;

            statusRegister = 0;
            statusRegister |= STATUS_BUSY;
            ClearInterrupt();

            // Captured here (emulation thread) so drive/track/sector match the command that
            // turns on the LED, not the moment the UI processes the queued work
            string activity = ActivityText;
            int ledDrive = currentDrive == 1 ? 1 : 0;

            Dispatcher.UIThread.InvokeAsync(() => {
                ASEMain.MainWindow.DriveLed(ledDrive, true, activity);
            }, DispatcherPriority.Background);

            // A Type I command whose h bit (3) is clear asks the chip for the spin-up sequence:
            // with the motor stopped it starts it and waits for SIX INDEX PULSES before the
            // command runs at all. Nothing answers the select line when drive B is unplugged, so
            // those pulses never come and the command never starts, never ends and never
            // interrupts — the chip stays busy until a Force Interrupt retires it.
            //
            // That silence is how a program tells a second drive from an empty cable, and
            // completing the command with a seek error instead (which is what a WD1772 reports
            // only after it has really stepped 255 times looking for TR00) answers "there IS a
            // drive there" to the shape of probe everyone uses. Psygnosis' Barbarian II is the
            // case that found it: its loader selects drive B, sends RESTORE ($03) and spins on
            // GPIP5 for ~3 s; the interrupt arriving at all is what it stores as "two drives",
            // so it asked for disk two in B: on a machine that has no B: — and then read from a
            // drive that cannot answer. TOS' own probe is built the same way and copes either
            // way: it times out after 2 s (1 s with the motor already running) and issues the
            // Force Interrupt itself ($E0175A in TOS 1.06).
            //
            // Only Type I is modelled here. A Type II/III command with no drive answering hangs
            // on the real chip too (it hunts for an ID field it needs index pulses to give up
            // on), but it still completes with Record Not Found in ASE — nothing has needed it
            // and that path is where every loader lives.
            if (command < 0x80 && (command & 0x08) == 0 && !motorWasOn && !DriveSelected)
            {
                // Busy is already set and the interrupt already cleared above: parking the
                // command is all that is left. Only Force Interrupt (or a reset) clears it —
                // a drive appearing mid-wait does not resume it, unlike the real chip.
                //
                // Traced because the consequence is total: the chip stays busy, every later
                // command is ignored, and a program waiting on the interrupt waits forever. It
                // is meant to happen only for a selection nothing answers (drive B unplugged);
                // seeing it for currentDrive = -1 means no drive was selected when the command
                // was written, and that is the first thing to check if a game that used to load
                // suddenly stops.
                ColoredConsole.WriteLine(
                    $"[[cyan]]FDC[[/cyan]] Type I ${command:X2} parked waiting for index pulses " +
                    $"(drive={currentDrive}, motor off) — only a Force Interrupt can retire it",
                    ConfigOptions.DebugModes.Information);

                stxOp = new StxOp { CompleteClock = long.MaxValue, WaitingForIndex = true };
                return;
            }

            // Type I (0xF0)
            byte hiNibble = (byte)(command & 0xF0);

            if (hiNibble == CMD_RESTORE)
            {
                ExecuteRestore();
                EndCommandOK();
                return;
            }
            if (hiNibble == CMD_SEEK)
            {
                ExecuteSeek();
                EndCommandOK();
                return;
            }

            // Type I: STEP, STEP-IN, STEP-OUT (decoded with 0xE0 mask, bit 4 = T flag)
            byte typeI = (byte)(command & 0xE0);

            if (typeI == 0x20) // STEP
            {
                ExecuteStep(command);
                EndCommandOK();
                return;
            }
            if (typeI == 0x40) // STEP-IN
            {
                stepDirection = 1;
                ExecuteStep(command);
                EndCommandOK();
                return;
            }
            if (typeI == 0x60) // STEP-OUT
            {
                stepDirection = -1;
                ExecuteStep(command);
                EndCommandOK();
                return;
            }

            // Type II/III on an STX (Pasti) image: run with real disk timing — busy stays
            // set and the data flows to the DMA byte by byte while the disk rotates,
            // which is what timing-based protections (Copylock etc.) measure.
            if (command >= 0x80 && (command & 0xF0) != CMD_FORCE_INTERRUPT
                && DriveSelected && ActiveDrive.Stx != null)
            {
                StartStxCommand(command, motorWasOn);
                return;
            }

            // Type II (0xE0)
            byte hi3 = (byte)(command & 0xE0);

            if (hi3 == 0x80) // 0x80 read sector
            {
                ExecuteReadSector();

                // Multiple, and every sector the DMA wanted arrived: the chip keeps hunting
                // for the next one instead of interrupting (see KeepSearchingAfterMultiple)
                if ((command & 0x10) != 0 && statusRegister == 0)
                    KeepSearchingAfterMultiple();
                else
                    EndCommandOK();
                return;
            }

            if (hi3 == 0xA0) // 0xA0 write sector
            {
                ExecuteWriteSector();
                EndCommandOK();
                return;
            }

            // Resto
            if (hiNibble == CMD_READ_ADDRESS)
            {
                ExecuteReadAddress();
                EndCommandOK();
                return;
            }
            if (hiNibble == CMD_READ_TRACK)
            {
                ExecuteReadTrack();
                EndCommandOK();
                return;
            }
            if (hiNibble == CMD_WRITE_TRACK)
            {
                ExecuteWriteTrack();
                EndCommandOK();
                return;
            }

            // Type IV: Force Interrupt (0xD0-0xDF)
            if (forceInterrupt)
            {
                // The status register is NOT rebuilt here. A Force Interrupt that stops a
                // command in progress only drops the busy bit and leaves everything the
                // interrupted command put there — contents and Type I/II form alike; only a
                // Force Interrupt arriving at an idle chip switches the register to Type I
                // form, and even then it just clears the spin-up bit and raises motor on
                // (Hatari's FDC_TypeIV_ForceInterrupt, verified on an STF).
                //
                // Rewriting it as a fresh Type I status is what broke Turrican (Kixx, .STX):
                // its boot loader reads a whole track with READ SECTOR MULTIPLE, waits for the
                // DMA address to reach its target, stops the chip with $D0 and then tests the
                // status against #$1C — lost data / CRC / record not found. In Type I form bit 2
                // is TR00, not lost data, so a perfectly good read of track 0 came back as an
                // error. The loader's retry path re-enters its read routine with D0 still
                // holding the $D0 opcode, which it writes to the sector register: from there it
                // asked for sector 208 forever, which is where it was found parked.
                statusRegister = (byte)(wasBusy
                    ? statusBeforeCommand & ~STATUS_BUSY
                    : statusBeforeCommand & ~(STATUS_BUSY | STATUS_SPIN_UP));

                byte intFlags = (byte)(command & 0x0F);
                if (intFlags != 0)
                    PulseInterrupt();
                else
                    ClearInterrupt();

                return;
            }

            // Unrecognized command
            statusRegister &= unchecked((byte)~STATUS_BUSY);
            PulseInterrupt();
        }

        private static void EndCommandOK()
        {
            statusRegister &= unchecked((byte)~STATUS_BUSY);
            PulseInterrupt();
        }

        /// <summary>
        /// A read MULTIPLE that has given the DMA everything it asked for is *not* finished:
        /// the WD1772 goes on looking for the next sector and only reports Record Not Found
        /// after five index pulses, so no interrupt arrives in between — programs stop the chip
        /// with a Force Interrupt once they have their data. Loaders build on that gap: Sega's
        /// Super Hang-On checks once per VBL whether the DMA address has reached its target,
        /// but tests the FDC interrupt *first* and reads an early one as a disk error, so it
        /// retried the same sector forever when the command ended by itself. The command
        /// therefore stays busy on a timed operation (the same one the STX engine uses) instead
        /// of ending here. Write Multiple is left alone: no loader depends on it and finishing
        /// it early has never been a problem.
        /// </summary>
        private static void KeepSearchingAfterMultiple()
        {
            statusRegister |= STATUS_BUSY;
            stxOp = new StxOp
            {
                CompleteClock = CPU._moira.Clock + RNF_REVOLUTIONS * CYCLES_PER_REVOLUTION,
                FinalStatus = STATUS_RECORD_NOT_FOUND
            };
        }

        private static void PulseInterrupt()
        {
            fdcIrq = true;
            UpdateSharedIrq();
        }

        private static void ClearInterrupt()
        {
            fdcIrq = false;
            UpdateSharedIrq();
        }

        private static void ExecuteRestore()
        {
            headTrack = 0;
            trackRegister = 0;
            UpdateTypeIStatus();

            // RESTORE steps outwards until TR00 comes back and gives up after 255 pulses with a
            // seek error (bit 4, the bit that carries RNF in Type II form). With drive B
            // unplugged that error is the whole answer to TOS' "is there a second drive?".
            if (!DriveSelected)
                statusRegister |= STATUS_RECORD_NOT_FOUND;

            // V flag (bit 2): verify reads an ID field, which fails with no disk
            if ((commandRegister & 0x04) != 0 && !ActiveDrive.HasDisk)
                statusRegister |= STATUS_RECORD_NOT_FOUND;

            statusRegister &= 0xFE;
        }

        private static void ExecuteSeek()
        {
            // SEEK -> move head where Data Register indicates
            headTrack = dataRegister;
            trackRegister = dataRegister;

            if (headTrack < 0)
                headTrack = 0;

            if (ActiveDrive.HasDisk && headTrack > ActiveDrive.DiskConfig.Tracks - 1)
                headTrack = ActiveDrive.DiskConfig.Tracks - 1;

            UpdateTypeIStatus();

            // V flag (bit 2): verify reads an ID field, which fails with no disk (or no drive)
            if ((commandRegister & 0x04) != 0 && (!DriveSelected || !ActiveDrive.HasDisk))
                statusRegister |= STATUS_RECORD_NOT_FOUND;

            statusRegister &= 0xFE;
        }

        private static void ExecuteStep(byte command)
        {
            headTrack += stepDirection;

            if (headTrack < 0)
                headTrack = 0;
            if (ActiveDrive.HasDisk && headTrack >= ActiveDrive.DiskConfig.Tracks)
                headTrack = ActiveDrive.DiskConfig.Tracks - 1;

            // T flag (bit 4): update track register
            if ((command & 0x10) != 0)
                trackRegister = (byte)headTrack;

            UpdateTypeIStatus();
        }

        private static void ExecuteReadSector()
        {
            bool multi = (commandRegister & 0x10) != 0; // bit 4 = multiple

            if (!DriveSelected || !ActiveDrive.HasDisk)
            {
                // No drive selected or no disk: no ID field can be read -> Record Not Found
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            int spt = ActiveDrive.DiskConfig.SectorsPerTrack;
            int sides = ActiveDrive.DiskConfig.Sides;
            int bps = ActiveDrive.DiskConfig.SectorSize;

            // The FDC compares the track number of the ID field with the track register:
            // if they don't match the sector is never found
            if (trackRegister != headTrack)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            if (sectorRegister < 1 || sectorRegister > spt)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            int sectorsToRead;
            if (multi)
            {
                if (dmaSectorCount == 0)
                {
                    dmaError = true;
                    return;
                }
                // WD1772 multi-sector: reads from current sector to end of track,
                // limited by DMA sector count
                int sectorsRemaining = spt - sectorRegister + 1;
                sectorsToRead = Math.Min((int)dmaSectorCount, sectorsRemaining);
            }
            else
            {
                sectorsToRead = 1;
            }

            bool readError = false;
            string dump = string.Empty;
            for (int n = 0; n < sectorsToRead; n++)
            {
                int lba = ((headTrack * sides) + currentSide) * spt + (sectorRegister - 1);
                int offset = lba * bps;
                if (ActiveDrive.Data == null || offset + bps > ActiveDrive.Data.Length)
                {
                    statusRegister |= STATUS_RECORD_NOT_FOUND;
                    dmaError = true;
                    readError = true;
                    break;
                }

                for (int j = 0; j < bps; j++)
                {
                    ASEMain._mem.Write8(dmaAddress++, ActiveDrive.Data[offset + j]);
                    dump += $"{ActiveDrive.Data[offset + j]:X2} ";
                }

                if (dmaSectorCount > 0) dmaSectorCount--;

                // WD1772: in multi-sector mode, auto-increment sector register
                if (multi)
                    sectorRegister++;
            }

            if (ConfigOptions.RunninConfig.DiskDump)
            {
                Console.WriteLine($"READ SECTOR: DMA={dmaAddress:X6} T={headTrack} S={currentSide} R={sectorRegister} count={sectorsToRead}");
                Console.Write(" Data loaded: " + dump);
                Console.Write(Environment.NewLine);
            }

            if (!readError)
            {
                // On a multi-sector read the WD1772 keeps reading sequential sectors until it is
                // explicitly stopped (Force Interrupt) or it runs off the end of the track. The
                // Atari DMA chip stops storing data once its sector count reaches 0, so:
                //   - dmaSectorCount == 0  -> every sector the host asked for was transferred, the
                //     read succeeded even if the last one happened to be the final sector on the
                //     track (this is the common "read exactly N sectors" case).
                //   - dmaSectorCount  > 0  -> the track ended before the DMA was satisfied, so the
                //     next sector the FDC looked for does not exist -> RECORD NOT FOUND, which is
                //     the signal trackloaders use to know they must seek to the next track.
                // (The previous code keyed on "SR > SPT", which wrongly reported RNF whenever the
                //  requested sectors reached the end of the track, making loaders retry forever.)
                if (multi && dmaSectorCount > 0)
                    statusRegister = STATUS_RECORD_NOT_FOUND;
                else
                    statusRegister = 0x00;
            }
        }

        private static void ExecuteWriteSector()
        {
            if (!DriveSelected || !ActiveDrive.HasDisk)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            if (ActiveDrive.WriteProtected) { statusRegister |= STATUS_WRITE_PROTECT; statusRegister &= 0xFE; return; }

            bool multi = (commandRegister & 0x10) != 0;
            int spt = ActiveDrive.DiskConfig.SectorsPerTrack;
            int bps = ActiveDrive.DiskConfig.SectorSize;

            if (trackRegister != headTrack)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            if (sectorRegister < 1 || sectorRegister > spt)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            int sectorsToWrite;
            if (multi)
            {
                int sectorsRemaining = spt - sectorRegister + 1;
                sectorsToWrite = Math.Min(Math.Max((int)dmaSectorCount, 1), sectorsRemaining);
            }
            else
            {
                sectorsToWrite = 1;
            }

            bool writeError = false;
            for (int i = 0; i < sectorsToWrite; i++)
            {
                int offset = CalculateDiskOffset(headTrack, currentSide, sectorRegister);
                if (ActiveDrive.Data == null || offset + bps > ActiveDrive.Data.Length)
                {
                    statusRegister |= STATUS_RECORD_NOT_FOUND;
                    dmaError = true;
                    writeError = true;
                    break;
                }

                for (int j = 0; j < bps; j++)
                    ActiveDrive.Data[offset + j] = ASEMain._mem.Read8(dmaAddress++);

                if (dmaSectorCount > 0) dmaSectorCount--;

                // WD1772: in multi-sector mode, auto-increment sector register
                if (multi)
                    sectorRegister++;
            }

            if (!writeError)
            {
                if (multi && sectorRegister > spt)
                    statusRegister = STATUS_RECORD_NOT_FOUND;
                else
                    statusRegister = 0x00;
            }
        }

        private static void ExecuteReadAddress()
        {
            if (!DriveSelected || !ActiveDrive.HasDisk)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            // Returns the next ID field passing under the head (sector numbers rotate)
            nextIdSector = (nextIdSector % ActiveDrive.DiskConfig.SectorsPerTrack) + 1;

            byte sizeCode = (byte)SizeCode(ActiveDrive.DiskConfig.SectorSize);

            // The 6 bytes the FDC transfers are the ID field contents followed by its CRC.
            // The CRC is computed over the three 0xA1 sync marks, the 0xFE address mark and the
            // four ID bytes, exactly as it sits on the disk, so protections that verify it pass.
            byte[] idField = { 0xA1, 0xA1, 0xA1, 0xFE, (byte)headTrack, (byte)currentSide, (byte)nextIdSector, sizeCode };
            ushort crc = Crc16(idField, 0, idField.Length);

            ASEMain._mem.Write8(dmaAddress++, (byte)headTrack);
            ASEMain._mem.Write8(dmaAddress++, (byte)currentSide);
            ASEMain._mem.Write8(dmaAddress++, (byte)nextIdSector);
            ASEMain._mem.Write8(dmaAddress++, sizeCode);            // sector size code (2 -> 512 bytes)
            ASEMain._mem.Write8(dmaAddress++, (byte)(crc >> 8));
            ASEMain._mem.Write8(dmaAddress++, (byte)(crc & 0xFF));

            // The track address of the ID field is also written into the sector register
            sectorRegister = (byte)headTrack;

            statusRegister = 0x00;
        }

        // READ TRACK: streams the whole raw track (gaps, sync marks, address marks, sector data
        // and CRCs) into RAM through the DMA, just like the real chip does between two index
        // pulses. .ST/.MSA images only store the sector payloads, so the surrounding format bytes
        // are synthesized as a standard double-density IBM/ISO track. Loaders and copy tools that
        // analyze the raw track (or wait for the DMA to drain) need real data here, otherwise they
        // hang forever on an empty transfer.
        private static void ExecuteReadTrack()
        {
            if (!DriveSelected || !ActiveDrive.HasDisk)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            byte[] track = BuildRawTrack(headTrack, currentSide);

            int bps = ActiveDrive.DiskConfig.SectorSize;
            // The DMA stops storing once its sector count reaches 0; if the host did not program a
            // count, fall back to transferring the whole synthesized track.
            int maxBytes = dmaSectorCount > 0 ? dmaSectorCount * bps : track.Length;
            int toCopy = Math.Min(track.Length, maxBytes);

            for (int i = 0; i < toCopy; i++)
                ASEMain._mem.Write8(dmaAddress++, track[i]);

            // Decrement the DMA sector count for every full sector (SectorSize bytes) transferred.
            if (dmaSectorCount > 0)
                dmaSectorCount = (byte)Math.Max(0, dmaSectorCount - (toCopy / bps));

            statusRegister = 0x00;
        }

        // WRITE TRACK: low-level format. The host streams a raw track image through the DMA; the
        // FDC writes it to the media. .ST/.MSA images cannot hold arbitrary low-level formats, so
        // this is a best-effort pass that walks the format stream, pairs each ID field with its
        // following data field and copies the sector payloads into the in-memory image. Anything
        // that does not parse is ignored, but the DMA is always drained so formatters that wait for
        // the transfer to finish do not hang.
        private static void ExecuteWriteTrack()
        {
            if (!DriveSelected || !ActiveDrive.HasDisk)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            if (ActiveDrive.WriteProtected)
            {
                statusRegister |= STATUS_WRITE_PROTECT;
                statusRegister &= 0xFE;
                return;
            }

            var cfg = ActiveDrive.DiskConfig;
            int bps = cfg.SectorSize;
            int spt = cfg.SectorsPerTrack;

            // Drain the format stream the CPU prepared for the DMA (the host normally programs a
            // sector count large enough to cover one full track).
            int streamLen = dmaSectorCount > 0 ? dmaSectorCount * bps : bps;
            byte[] stream = new byte[streamLen];
            for (int i = 0; i < streamLen; i++)
                stream[i] = ASEMain._mem.Read8(dmaAddress++);

            if (dmaSectorCount > 0)
                dmaSectorCount = 0;

            // In the format stream sync bytes are written as 0xF5 (read back as 0xA1), the ID
            // address mark is 0xFE and the data address mark is 0xFB. Pair each ID field with the
            // next data field and store the payload at the matching position in the image.
            int curSide = currentSide;
            int curSector = -1;

            for (int i = 0; i < stream.Length; i++)
            {
                if (stream[i] == 0xFE && i + 4 < stream.Length)
                {
                    // IDAM: track, side, sector, size code
                    curSide = stream[i + 2];
                    curSector = stream[i + 3];
                    i += 4;
                }
                else if (stream[i] == 0xFB && curSector >= 1 && curSector <= spt)
                {
                    int offset = CalculateDiskOffset(headTrack, curSide, curSector);
                    if (ActiveDrive.Data != null && offset + bps <= ActiveDrive.Data.Length
                        && i + bps < stream.Length)
                    {
                        for (int j = 0; j < bps; j++)
                            ActiveDrive.Data[offset + j] = stream[i + 1 + j];
                    }
                    i += bps;
                    curSector = -1;
                }
            }

            statusRegister = 0x00;
        }

        /// <summary>
        /// Builds the raw byte stream of a track as it would be read back from a standard
        /// double-density IBM/ISO formatted disk (the layout TOS writes), including gaps, sync
        /// fields, ID/data address marks and CRCs around each sector payload.
        /// </summary>
        private static byte[] BuildRawTrack(int track, int side)
        {
            var cfg = ActiveDrive.DiskConfig;
            int spt = cfg.SectorsPerTrack;
            int bps = cfg.SectorSize;
            byte sizeCode = (byte)SizeCode(bps);

            var t = new List<byte>(6400);
            void Fill(byte v, int n) { for (int k = 0; k < n; k++) t.Add(v); }

            Fill(0x4E, 60);                                 // Gap 1 (post-index)

            for (int s = 1; s <= spt; s++)
            {
                // --- ID field ---
                Fill(0x00, 12);                             // sync
                int idStart = t.Count;
                t.Add(0xA1); t.Add(0xA1); t.Add(0xA1);      // sync marks (written as 0xF5)
                t.Add(0xFE);                                // ID address mark
                t.Add((byte)track);
                t.Add((byte)side);
                t.Add((byte)s);
                t.Add(sizeCode);
                ushort idCrc = Crc16(t, idStart, t.Count - idStart);
                t.Add((byte)(idCrc >> 8)); t.Add((byte)(idCrc & 0xFF));

                Fill(0x4E, 22);                             // Gap 2

                // --- Data field ---
                Fill(0x00, 12);                             // sync
                int daStart = t.Count;
                t.Add(0xA1); t.Add(0xA1); t.Add(0xA1);
                t.Add(0xFB);                                // data address mark
                int lba = (track * cfg.Sides + side) * spt + (s - 1);
                int offset = lba * bps;
                for (int j = 0; j < bps; j++)
                {
                    byte d = (ActiveDrive.Data != null && offset + j < ActiveDrive.Data.Length)
                        ? ActiveDrive.Data[offset + j] : (byte)0x00;
                    t.Add(d);
                }
                ushort daCrc = Crc16(t, daStart, t.Count - daStart);
                t.Add((byte)(daCrc >> 8)); t.Add((byte)(daCrc & 0xFF));

                Fill(0x4E, 40);                             // Gap 3
            }

            // Gap 4 (pre-index): pad to a typical DD track capacity (~6256 bytes)
            const int TrackCapacity = 6256;
            if (t.Count < TrackCapacity)
                Fill(0x4E, TrackCapacity - t.Count);

            return t.ToArray();
        }

        /// <summary>WD1772 sector size code: 0=128, 1=256, 2=512, 3=1024 bytes.</summary>
        private static int SizeCode(int bytesPerSector)
        {
            switch (bytesPerSector)
            {
                case 128: return 0;
                case 256: return 1;
                case 1024: return 3;
                default: return 2; // 512 bytes (standard ST sector)
            }
        }

        /// <summary>CRC-16-CCITT (poly 0x1021, init 0xFFFF) as used by the WD1772 ID/data fields.</summary>
        private static ushort Crc16(IReadOnlyList<byte> data, int start, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[start + i] << 8);
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        private static int CalculateDiskOffset(int track, int side, int sector)
        {
            return (track * ActiveDrive.DiskConfig.Sides * ActiveDrive.DiskConfig.SectorsPerTrack + side * ActiveDrive.DiskConfig.SectorsPerTrack + (sector - 1)) * ActiveDrive.DiskConfig.SectorSize;
        }

        // ------------------------------------------------------------------
        //  STX (Pasti) command engine
        //
        //  For STX images the Type II/III commands are not instantaneous: the
        //  target ID field is located by its real position within the disk
        //  revolution and every data byte is delivered to the DMA at the CPU
        //  cycle it would come off the drive (including the per-16-byte-block
        //  variable bit rate stored in the image). Copy protections measure
        //  exactly this: Copylock times the sector blocks, Dungeon Master
        //  re-reads fuzzy sectors expecting different bits, others check CRC
        //  errors or ID fields that don't match the physical track.
        // ------------------------------------------------------------------

        private const int CYCLES_PER_MFM_BYTE = 256;   // one MFM byte = 32 µs = 256 CPU cycles at 8 MHz
        private const int CYCLES_PER_MFM_BIT = 32;
        private const int RNF_REVOLUTIONS = 5;         // the WD1772 gives up the ID search after 5 index pulses

        private sealed class StxOp
        {
            public byte[] Bytes;              // bytes to stream to the DMA, or null (delay-only / write)
            public long[] Due;                // absolute CPU clock at which each byte comes off the disk
            public int Next;                  // next byte to deliver
            public long CompleteClock;        // when busy clears and the IRQ fires
            public byte FinalStatus;          // status register at completion (CRC/RNF/record type bits)
            public bool Multi;                // read/write multiple: chain the next sector
            public bool CountDmaBytes;        // transfers count against the DMA sector counter
            public bool IsWrite;              // write sector: RAM -> image at completion
            public STXImage.Sector Target;    // sector being written
            public bool DrainWriteTrack;      // write track: consume the format stream, keep the image
            public bool SetSectorReg;         // read address: ID track number -> sector register
            public byte SectorRegAtEnd;
            public bool WaitingForIndex;      // Type I parked in the spin-up sequence: no index
                                              // pulses, so it never completes (see ExecuteCommand)
        }

        static StxOp stxOp;
        static int stxDmaByteCount;           // bytes transferred toward the next 512-byte DMA count step

        // The DMA chip is not byte-addressed: bytes coming off the FDC are collected in a
        // 16-byte FIFO, and only when it fills does the block reach RAM and the address
        // register ($FF8609/0B/0D) step — by 16, never by 1. Copy protections measure exactly
        // that: Ocean's Batman the Movie loader samples the interval between consecutive steps
        // of $FF860D with MFP Timer A to read back the variable bit rate of a protected sector
        // (its 32 samples are the sector's 32 blocks of 16 bytes). Stepping the address per
        // byte makes every sample identical and the loader hangs. Same model as Hatari's
        // FDC_DMA_FIFO_Push / DMA_DISK_TRANSFER_SIZE.
        const int DMA_FIFO_SIZE = 16;
        static readonly byte[] dmaFifo = new byte[DMA_FIFO_SIZE];
        static int dmaFifoCount;

        /// <summary>
        /// Feeds one byte read from the disk into the DMA FIFO. Nothing is written to RAM
        /// until the 16th byte arrives; a partial FIFO left over at the end of a command is
        /// dropped, as on the real chip (every sector size and track image is a multiple of
        /// 16, so nothing is lost in practice).
        /// </summary>
        private static void DmaFifoPush(byte value)
        {
            // Every byte the FDC hands the DMA also stays visible in the undriven bits of
            // $FF8604/$FF8606, and so does the last word of each block the FIFO stores (below).
            ff8604RecentVal = (ushort)((ff8604RecentVal & 0xFF00) | value);

            dmaFifo[dmaFifoCount++] = value;
            if (dmaFifoCount < DMA_FIFO_SIZE)
                return;

            for (int i = 0; i < DMA_FIFO_SIZE; i++)
                ASEMain._mem.Write8(dmaAddress + (uint)i, dmaFifo[i]);

            dmaAddress = (dmaAddress + DMA_FIFO_SIZE) & DMA_ADDRESS_MASK;
            dmaFifoCount = 0;

            ff8604RecentVal = (ushort)((dmaFifo[DMA_FIFO_SIZE - 2] << 8) | dmaFifo[DMA_FIFO_SIZE - 1]);

            // The sector counter goes down as blocks are stored, not as bytes come in
            stxDmaByteCount += DMA_FIFO_SIZE;
            if (stxDmaByteCount == 512)
            {
                stxDmaByteCount = 0;
                dmaSectorCount--;
            }
        }

        private static void StartStxCommand(byte command, bool motorWasOn)
        {
            stxDmaByteCount = 0;
            dmaFifoCount = 0;      // whatever the previous command left half-collected is gone

            // Spin-up: 6 revolutions when the motor was stopped and the h bit (3) is clear
            long spinUp = (!motorWasOn && (command & 0x08) == 0) ? 6 * CYCLES_PER_REVOLUTION : 0;

            byte hi3 = (byte)(command & 0xE0);

            if (hi3 == 0x80) // read sector
            {
                bool multi = (command & 0x10) != 0;
                if (multi && dmaSectorCount == 0)
                {
                    dmaError = true;
                    EndCommandOK();
                    return;
                }
                StartStxReadSector(multi, spinUp);
            }
            else if (hi3 == 0xA0) // write sector
            {
                StartStxWriteSector((command & 0x10) != 0, spinUp);
            }
            else
            {
                switch (command & 0xF0)
                {
                    case CMD_READ_ADDRESS: StartStxReadAddress(spinUp); break;
                    case CMD_READ_TRACK: StartStxReadTrack(spinUp); break;
                    case CMD_WRITE_TRACK: StartStxWriteTrack(spinUp); break;
                }
            }

            // Any path that did not install a timed operation already put its error
            // bits into the status register: complete the command right away.
            if (stxOp == null)
                EndCommandOK();
        }

        private static void StartStxRnf(long now, long revCycles)
        {
            stxOp = new StxOp
            {
                CompleteClock = now + RNF_REVOLUTIONS * revCycles,
                FinalStatus = STATUS_RECORD_NOT_FOUND
            };
        }

        private static void StartStxReadSector(bool multi, long spinUp)
        {
            long now = CPU._moira.Clock + spinUp;
            var track = ActiveDrive.Stx.GetTrack(headTrack, currentSide);

            if (track == null || track.Sectors.Count == 0)
            {
                StartStxRnf(now, CYCLES_PER_REVOLUTION);
                return;
            }

            long revCycles = (long)track.TrackSizeBytes * CYCLES_PER_MFM_BYTE;
            var sec = FindNextId(track, now, trackRegister, sectorRegister, true,
                                 out long delay, out bool badCrcSeen);

            if (sec == null)
            {
                stxOp = new StxOp
                {
                    CompleteClock = now + RNF_REVOLUTIONS * revCycles,
                    FinalStatus = (byte)(STATUS_RECORD_NOT_FOUND | (badCrcSeen ? STATUS_CRC_ERROR : 0))
                };
                return;
            }

            if ((sec.FdcStatus & STXImage.SECTOR_FLAG_RNF) != 0)
            {
                // The ID field exists but its data field is unreadable/absent: the chip
                // retries until it has seen 5 index pulses, then reports Record Not Found
                StartStxRnf(now, revCycles);
                return;
            }

            // From the first ID sync byte to the first data byte: 3x$A1 + $FE + 4 ID
            // bytes + 2 CRC + GAP3a + GAP3b + 3x$A1 + DAM
            long dataStart = now + delay
                + (4 + 4 + 2 + STXImage.GAP3a + STXImage.GAP3b + 4) * CYCLES_PER_MFM_BYTE;

            byte[] bytes = new byte[sec.SectorSize];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = sec.Data[i];
                // Fuzzy bits: mask bit 1 = stable, 0 = reads randomly on every pass
                if (sec.FuzzyMask != null)
                    b = (byte)((b & sec.FuzzyMask[i]) | (Random.Shared.Next(256) & ~sec.FuzzyMask[i]));
                bytes[i] = b;
            }

            long[] due = BuildDueTimes(sec, dataStart);

            stxOp = new StxOp
            {
                Bytes = bytes,
                Due = due,
                CompleteClock = due[^1] + 2 * CYCLES_PER_MFM_BYTE, // the data CRC still passes under the head
                FinalStatus = (byte)(sec.FdcStatus & (STATUS_CRC_ERROR | STATUS_RECORD_TYPE)),
                Multi = multi,
                CountDmaBytes = true
            };
        }

        private static void StartStxWriteSector(bool multi, long spinUp)
        {
            if (ActiveDrive.WriteProtected)
            {
                statusRegister |= STATUS_WRITE_PROTECT;
                statusRegister &= 0xFE;
                return;
            }

            long now = CPU._moira.Clock + spinUp;
            var track = ActiveDrive.Stx.GetTrack(headTrack, currentSide);

            if (track == null || track.Sectors.Count == 0)
            {
                StartStxRnf(now, CYCLES_PER_REVOLUTION);
                return;
            }

            long revCycles = (long)track.TrackSizeBytes * CYCLES_PER_MFM_BYTE;
            var sec = FindNextId(track, now, trackRegister, sectorRegister, true,
                                 out long delay, out bool badCrcSeen);

            if (sec == null)
            {
                stxOp = new StxOp
                {
                    CompleteClock = now + RNF_REVOLUTIONS * revCycles,
                    FinalStatus = (byte)(STATUS_RECORD_NOT_FOUND | (badCrcSeen ? STATUS_CRC_ERROR : 0))
                };
                return;
            }

            if ((sec.FdcStatus & STXImage.SECTOR_FLAG_RNF) != 0)
            {
                StartStxRnf(now, revCycles);
                return;
            }

            long dataStart = now + delay
                + (4 + 4 + 2 + STXImage.GAP3a + STXImage.GAP3b + 4) * CYCLES_PER_MFM_BYTE;

            stxOp = new StxOp
            {
                IsWrite = true,
                Target = sec,
                Multi = multi,
                // data is always written at the standard rate, plus the closing CRC
                CompleteClock = dataStart + (long)(sec.SectorSize + 2) * CYCLES_PER_MFM_BYTE,
                FinalStatus = 0
            };
        }

        private static void StartStxReadAddress(long spinUp)
        {
            long now = CPU._moira.Clock + spinUp;
            var track = ActiveDrive.Stx.GetTrack(headTrack, currentSide);

            if (track == null || track.Sectors.Count == 0)
            {
                StartStxRnf(now, CYCLES_PER_REVOLUTION);
                return;
            }

            // The next ID field passing under the head, whatever its content (a bad ID
            // CRC is still transferred, only flagged in the status)
            var sec = FindNextId(track, now, -1, -1, false, out long delay, out _);

            byte[] bytes =
            {
                sec.IdTrack, sec.IdHead, sec.IdSector, sec.IdSize,
                (byte)(sec.IdCrc >> 8), (byte)(sec.IdCrc & 0xFF)
            };

            long[] due = new long[6];
            for (int i = 0; i < 6; i++)
                due[i] = now + delay + (4 + i + 1) * CYCLES_PER_MFM_BYTE;

            stxOp = new StxOp
            {
                Bytes = bytes,
                Due = due,
                CompleteClock = due[5],
                FinalStatus = sec.IdCrcOk ? (byte)0 : STATUS_CRC_ERROR,
                // like the real chip, the track number of the ID field lands in the sector register
                SetSectorReg = true,
                SectorRegAtEnd = sec.IdTrack
            };
        }

        private static void StartStxReadTrack(long spinUp)
        {
            long now = CPU._moira.Clock + spinUp;
            var track = ActiveDrive.Stx.GetTrack(headTrack, currentSide);

            byte[] raw = BuildStxRawTrack(track);
            long revCycles = (long)raw.Length * CYCLES_PER_MFM_BYTE;
            long start = now + (revCycles - now % revCycles);  // the transfer begins at the index pulse

            long[] due = new long[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                due[i] = start + (long)(i + 1) * CYCLES_PER_MFM_BYTE;

            stxOp = new StxOp
            {
                Bytes = raw,
                Due = due,
                CompleteClock = due[^1],
                FinalStatus = 0,
                CountDmaBytes = true
            };
        }

        private static void StartStxWriteTrack(long spinUp)
        {
            if (ActiveDrive.WriteProtected)
            {
                statusRegister |= STATUS_WRITE_PROTECT;
                statusRegister &= 0xFE;
                return;
            }

            long now = CPU._moira.Clock + spinUp;
            var track = ActiveDrive.Stx.GetTrack(headTrack, currentSide);
            long revCycles = (long)(track?.TrackSizeBytes ?? STXImage.TRACK_BYTES_STANDARD) * CYCLES_PER_MFM_BYTE;
            long start = now + (revCycles - now % revCycles);

            // The format stream is drained with realistic timing, but an STX image keeps
            // its original low-level layout: reformatting a protected disk is not supported.
            stxOp = new StxOp
            {
                CompleteClock = start + revCycles,
                FinalStatus = 0,
                DrainWriteTrack = true
            };
        }

        /// <summary>
        /// Rotational ID-field search: starting at <paramref name="fromClock"/>, walks the
        /// ID fields passing under the head — up to 5 revolutions, the chip's Record Not
        /// Found timeout — and returns the first one matching the track/sector registers,
        /// with the delay in cycles until its first sync byte. -1 matches any value.
        /// </summary>
        private static STXImage.Sector FindNextId(STXImage.Track track, long fromClock,
            int matchTrack, int matchSector, bool requireGoodIdCrc,
            out long delay, out bool badCrcMatchSeen)
        {
            long revCycles = (long)track.TrackSizeBytes * CYCLES_PER_MFM_BYTE;
            long pos = fromClock % revCycles;         // position of the head within the revolution
            badCrcMatchSeen = false;

            for (int rev = 0; rev < RNF_REVOLUTIONS; rev++)
            {
                STXImage.Sector best = null;
                long bestT = long.MaxValue;

                foreach (var sec in track.Sectors)
                {
                    // BitPosition points just after the IDAM: back up 4 bytes to the first $A1
                    long idPos = ((long)sec.BitPosition * CYCLES_PER_MFM_BIT - 4 * CYCLES_PER_MFM_BYTE) % revCycles;
                    if (idPos < 0) idPos += revCycles;

                    long t = idPos - pos + rev * revCycles;
                    if (t <= 0) continue;                          // already passed on this revolution

                    if (matchTrack >= 0 && sec.IdTrack != matchTrack) continue;
                    if (matchSector >= 0 && sec.IdSector != matchSector) continue;
                    if (requireGoodIdCrc && !sec.IdCrcOk)
                    {
                        badCrcMatchSeen = true;                    // matched, but its ID CRC is bad
                        continue;
                    }

                    if (t < bestT) { bestT = t; best = sec; }
                }

                if (best != null)
                {
                    delay = bestT;
                    return best;
                }
            }

            delay = RNF_REVOLUTIONS * revCycles;
            return null;
        }

        /// <summary>
        /// Cycle at which each byte of the sector comes off the disk. Standard sectors
        /// pace at 32 µs/byte (or the sector's total ReadTime); variable-rate sectors use
        /// the Pasti timing table: one big-endian word per 16-byte block, where one unit
        /// is 32 FDC cycles plus 28 cycles to complete each block (Hatari's formula).
        /// </summary>
        private static long[] BuildDueTimes(STXImage.Sector sec, long dataStart)
        {
            int n = sec.SectorSize;
            var due = new long[n];
            long acc = 0;

            if (sec.TimingData != null)
            {
                double prev = 0;
                for (int i = 0; i < n; i++)
                {
                    int blk = (i >> 4) * 2;
                    int units = blk + 1 < sec.TimingData.Length
                        ? (sec.TimingData[blk] << 8) | sec.TimingData[blk + 1]
                        : 0x7F;                                  // short table: standard rate
                    int blockCycles = units * 32 + 28;

                    if ((i & 15) == 0) prev = 0;                 // new 16-byte block
                    double cur = (double)blockCycles * ((i & 15) + 1) / 16;
                    int step = (int)Math.Round(cur - prev);
                    prev += step;
                    acc += step;
                    due[i] = dataStart + acc;
                }
            }
            else
            {
                long totalCycles = (sec.ReadTime == 0 ? 32L * n : sec.ReadTime) * 8;
                double prev = 0;
                for (int i = 0; i < n; i++)
                {
                    double cur = (double)totalCycles * (i + 1) / n;
                    int step = (int)Math.Round(cur - prev);
                    prev += step;
                    acc += step;
                    due[i] = dataStart + acc;
                }
            }

            return due;
        }

        /// <summary>Raw track stream for the STX Read Track command: the stored track image
        /// when the file has one, otherwise a standard layout rebuilt from the sectors
        /// (Hatari does the same); an absent track reads as unformatted noise.</summary>
        private static byte[] BuildStxRawTrack(STXImage.Track track)
        {
            if (track == null)
            {
                byte[] noise = new byte[STXImage.TRACK_BYTES_STANDARD];
                Random.Shared.NextBytes(noise);
                return noise;
            }

            if (track.TrackImage != null && track.TrackImage.Length > 0)
                return track.TrackImage;

            int size = track.TrackSizeBytes;
            var t = new List<byte>(size + 16);
            void Fill(byte v, int n) { for (int k = 0; k < n; k++) t.Add(v); }

            Fill(0x4E, STXImage.GAP1);

            foreach (var sec in track.Sectors)
            {
                int need = STXImage.GAP2 + 10 + STXImage.GAP3a + STXImage.GAP3b
                    + 4 + sec.SectorSize + 2 + STXImage.GAP4;
                if (t.Count + need >= size)
                    break;

                Fill(0x00, STXImage.GAP2);
                t.Add(0xA1); t.Add(0xA1); t.Add(0xA1); t.Add(0xFE);
                t.Add(sec.IdTrack); t.Add(sec.IdHead); t.Add(sec.IdSector); t.Add(sec.IdSize);
                t.Add((byte)(sec.IdCrc >> 8)); t.Add((byte)(sec.IdCrc & 0xFF));
                Fill(0x4E, STXImage.GAP3a);

                if (sec.Data != null)
                {
                    Fill(0x00, STXImage.GAP3b);
                    int daStart = t.Count;
                    t.Add(0xA1); t.Add(0xA1); t.Add(0xA1); t.Add(0xFB);
                    for (int i = 0; i < sec.SectorSize; i++) t.Add(sec.Data[i]);
                    ushort crc = Crc16(t, daStart, t.Count - daStart);
                    t.Add((byte)(crc >> 8)); t.Add((byte)(crc & 0xFF));
                    Fill(0x4E, STXImage.GAP4);
                }
            }

            while (t.Count < size)
                t.Add(0x4E);

            return t.ToArray();
        }

        /// <summary>Delivers every byte that has come off the disk up to <paramref name="now"/>
        /// and completes the command when its last cycle has passed.</summary>
        private static void ProcessStxOp(long now)
        {
            var op = stxOp;
            if (op == null) return;

            if (op.Bytes != null)
            {
                while (op.Next < op.Bytes.Length && op.Due[op.Next] <= now)
                {
                    if (!op.CountDmaBytes)
                    {
                        // Read Address: the six ID bytes are handed over directly, they would
                        // never fill a FIFO block on their own
                        ASEMain._mem.Write8(dmaAddress++, op.Bytes[op.Next]);
                    }
                    else if (dmaSectorCount != 0)
                    {
                        // The DMA only stores while its sector counter is not exhausted;
                        // bytes arriving afterwards are lost (see DmaFifoPush)
                        DmaFifoPush(op.Bytes[op.Next]);
                    }
                    op.Next++;
                }
            }

            if (now >= op.CompleteClock)
                FinishStxOp(op);
        }

        private static void FinishStxOp(StxOp op)
        {
            stxOp = null;

            if (op.IsWrite && op.Target != null)
            {
                // The data field is taken from RAM in one go at the end of the command
                var sec = op.Target;
                byte[] data = new byte[sec.SectorSize];
                for (int i = 0; i < data.Length; i++)
                    data[i] = ASEMain._mem.Read8(dmaAddress++);
                sec.Data = data;
                // freshly written data reads back clean, at the standard rate
                sec.FuzzyMask = null;
                sec.TimingData = null;
                sec.FdcStatus &= unchecked((byte)~(STXImage.SECTOR_FLAG_FUZZY |
                    STXImage.SECTOR_FLAG_VARIABLE_TIME | STXImage.SECTOR_FLAG_CRC |
                    STXImage.SECTOR_FLAG_RECORD_TYPE));

                if (dmaSectorCount > 0)
                    dmaSectorCount--;
            }

            if (op.DrainWriteTrack)
            {
                dmaAddress += (uint)(dmaSectorCount * 512);
                dmaSectorCount = 0;
            }

            if (op.SetSectorReg)
                sectorRegister = op.SectorRegAtEnd;

            // Multi-sector: keep going while the DMA still wants data
            if (op.Multi && op.FinalStatus == 0 && dmaSectorCount > 0)
            {
                sectorRegister++;
                if (op.IsWrite) StartStxWriteSector(true, 0);
                else StartStxReadSector(true, 0);

                if (stxOp != null)
                    return;               // next sector under way, the command stays busy

                EndCommandOK();           // instant failure path already set the status bits
                return;
            }

            // DMA satisfied on a read multiple: the chip has not finished either, it goes
            // looking for the next sector (see KeepSearchingAfterMultiple)
            if (op.Multi && !op.IsWrite && op.FinalStatus == 0)
            {
                KeepSearchingAfterMultiple();
                return;
            }

            statusRegister = op.FinalStatus;  // busy cleared; motor/index bits are composed live
            PulseInterrupt();
        }

        public static void SetWriteProtect(int drive, bool protect)
        {
            if (drive == 0) ASEMain.driveA.WriteProtected = protect;
            else if (drive == 1) ASEMain.driveB.WriteProtected = protect;
        }
    }
}
