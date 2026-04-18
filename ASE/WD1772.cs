/*
 * 
 * Western Digital WD1772 FDC emulation for Atari ST
 * This class should be my next focus, because it's the cause of many games games does not work.
 * 
 * https://info-coach.fr/atari/documents/_mydoc/FD-HD_Programming.pdf
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

        // Estado
        static int currentDrive = -1;
        static int currentSide;
        static int headTrack;
        static int stepDirection = 1;
        static bool dmaError;

        // Bits Status
        private const byte STATUS_BUSY = 0x01;
        private const byte STATUS_DRQ = 0x02;
        private const byte STATUS_LOST_DATA = 0x04;
        private const byte STATUS_CRC_ERROR = 0x08;
        private const byte STATUS_RECORD_NOT_FOUND = 0x10;
        private const byte STATUS_RECORD_TYPE = 0x20;
        private const byte STATUS_WRITE_PROTECT = 0x40;
        private const byte STATUS_NOT_READY = 0x80;
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
            headTrack = 0;
            stepDirection = 1;
            dmaSectorCount = 0;
            statusRegister = 0;

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
                    dmaAddress = (dmaAddress & 0xFF00FF) | ((uint)value << 8); 
                    break;
                case 0xFF860D: 
                    dmaAddress = (dmaAddress & 0xFFFF00) | ((uint)value & 0xFE); 
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
            switch (address)
            {
                case 0xFF8604: 
                    return (ushort)(0xFF00 | ReadFromFDCOrSectorCount());
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
            bool prevDir = (prevMode & 0x0100) != 0;
            bool newDir = (dmaModeRegister & 0x0100) != 0;

            if (prevDir != newDir)
                dmaError = false;

            prevMode = dmaModeRegister;
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

            return status;
        }

        private static void WriteToFDCOrSectorCount(byte value)
        {
            bool selectSectorCount = ((dmaModeRegister >> DMA_SECTOR_COUNT_REG) & 1) == 1;

            if (selectSectorCount)
            {
                dmaSectorCount = value;
            }
            else
            {
                if (((dmaModeRegister >> DMA_HDC_SELECT) & 1) == 1) return;

                int sel = (dmaModeRegister >> 1) & 3;
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
        }

        private static byte ReadFromFDCOrSectorCount()
        {
            bool selectSectorCount = ((dmaModeRegister >> DMA_SECTOR_COUNT_REG) & 1) == 1;

            if (selectSectorCount) 
                return dmaSectorCount;

            if (((dmaModeRegister >> DMA_HDC_SELECT) & 1) == 1) 
                return 0xFF;

            int fdcRegister = (dmaModeRegister >> DMA_A0) & 0x03;
            switch (fdcRegister)
            {
                case 0: // STATUS REGISTER
                    ClearInterrupt();
                    return statusRegister;
                case 1: 
                    return trackRegister;
                case 2: 
                    return sectorRegister;
                case 3: 
                    return dataRegister;
                default: 
                    return 0xFF;
            }
        }

        // 300 RPM → 1 revolution per 200 ms → index pulse every ~160000 CPU cycles at 8 MHz.
        // We approximate with a simple modulo on the emulated cycle counter.
        static long indexPulseCycleAccum = 0;
        private const long INDEX_PULSE_CYCLES = 160000; // 8 MHz / 300 RPM * 0.06 (6% duty)

        public static void TickCycles(int cycles)
        {
            indexPulseCycleAccum += cycles;
            if (indexPulseCycleAccum > INDEX_PULSE_CYCLES * 20)
                indexPulseCycleAccum = 0; // wrap safely
        }

        private static void UpdateTypeIStatus()
        {
            statusRegister = 0;
            statusRegister |= 0x80; // Motor On (Type I)
            statusRegister |= 0x20; // Spin-up completed

            if (headTrack == 0)
                statusRegister |= STATUS_TRACK0;
            if (ASEMain.driveA.WriteProtected)
                statusRegister |= STATUS_WRITE_PROTECT;

            // Index pulse: active for the first ~6% of each revolution
            long posInRevolution = indexPulseCycleAccum % (INDEX_PULSE_CYCLES * 20);
            if (posInRevolution < INDEX_PULSE_CYCLES)
                statusRegister |= 0x02;
        }

        private static void ExecuteCommand(byte command)
        {
            commandRegister = command;
            byte cmdType = (byte)(command & 0xF0);

            statusRegister = 0;

            if (currentDrive == -1) 
            { 
                statusRegister |= STATUS_NOT_READY; 
                return; 
            }

            statusRegister |= STATUS_BUSY;
            ClearInterrupt();

            Dispatcher.UIThread.InvokeAsync(() => { 
                ASEMain.MainWindow.DriveLed(true); 
            }, DispatcherPriority.Background);

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

            // Type II (0xE0)
            byte hi3 = (byte)(command & 0xE0);

            if (hi3 == 0x80) // 0x80 read sector
            {
                ExecuteReadSector();
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
            if ((command & 0xF0) == CMD_FORCE_INTERRUPT)
            {
                statusRegister &= unchecked((byte)~STATUS_BUSY);
                UpdateTypeIStatus();

                byte intFlags = (byte)(command & 0x0F);
                if (intFlags != 0)
                    PulseInterrupt();
                else
                    ClearInterrupt();

                return;
            }

            // No se reconoce el comando
            statusRegister &= unchecked((byte)~STATUS_BUSY);
            PulseInterrupt();
        }

        private static void EndCommandOK()
        {
            statusRegister &= unchecked((byte)~STATUS_BUSY);
            PulseInterrupt();
        }

        private static void PulseInterrupt()
        {
            ASEMain._mfp.SetGPIOBit(5, false);
        }

        private static void ClearInterrupt() 
        { 
            ASEMain._mfp.SetGPIOBit(5, true); 
        }

        private static void ExecuteRestore()
        {
            headTrack = 0;
            trackRegister = 0;
            UpdateTypeIStatus();
            statusRegister &= 0xFE;
        }

        private static void ExecuteSeek()
        {
            // SEEK -> move head where Data Register indicates
            headTrack = dataRegister;
            trackRegister = dataRegister;

            if (!ASEMain.driveA.HasDisk)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            if (headTrack < 0) 
                headTrack = 0;

            if (headTrack > ASEMain.driveA.DiskConfig.Tracks - 1) 
                headTrack = ASEMain.driveA.DiskConfig.Tracks - 1;

            UpdateTypeIStatus();
            statusRegister &= 0xFE;
        }

        private static void ExecuteStep(byte command)
        {
            headTrack += stepDirection;

            if (headTrack < 0) 
                headTrack = 0;
            if (ASEMain.driveA.HasDisk && headTrack >= ASEMain.driveA.DiskConfig.Tracks)
                headTrack = ASEMain.driveA.DiskConfig.Tracks - 1;

            // T flag (bit 4): update track register
            if ((command & 0x10) != 0)
                trackRegister = (byte)headTrack;

            UpdateTypeIStatus();
        }

        private static void ExecuteReadSector()
        {
            bool multi = (commandRegister & 0x10) != 0; // bit 4 = multiple

            if (!ASEMain.driveA.HasDisk)
                return;

            int spt = ASEMain.driveA.DiskConfig.SectorsPerTrack;
            int sides = ASEMain.driveA.DiskConfig.Sides;
            int bps = ASEMain.driveA.DiskConfig.SectorSize;

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
                if (ASEMain.driveA.Data == null || offset + bps > ASEMain.driveA.Data.Length)
                {
                    statusRegister |= STATUS_RECORD_NOT_FOUND;
                    dmaError = true;
                    readError = true;
                    break;
                }

                for (int j = 0; j < bps; j++)
                {
                    ASEMain._mem.Write8(dmaAddress++, ASEMain.driveA.Data[offset + j]);
                    dump += $"{ASEMain.driveA.Data[offset + j]:X2} ";
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
                // In multi-sector mode the WD1772 keeps incrementing SR until the sector
                // is not found on the track (SR > SPT). This RECORD_NOT_FOUND is the
                // standard signal that loaders use to know the track is finished and they
                // must seek to the next one before issuing a new read command.
                if (multi && sectorRegister > spt)
                    statusRegister = STATUS_RECORD_NOT_FOUND;
                else
                    statusRegister = 0x00;
            }
        }

        private static void ExecuteWriteSector()
        {
            if (ASEMain.driveA.WriteProtected) { statusRegister |= STATUS_WRITE_PROTECT; statusRegister &= 0xFE; return; }

            bool multi = (commandRegister & 0x10) != 0;
            int spt = ASEMain.driveA.DiskConfig.SectorsPerTrack;
            int bps = ASEMain.driveA.DiskConfig.SectorSize;

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
                if (ASEMain.driveA.Data == null || offset + bps > ASEMain.driveA.Data.Length)
                {
                    statusRegister |= STATUS_RECORD_NOT_FOUND;
                    dmaError = true;
                    writeError = true;
                    break;
                }

                for (int j = 0; j < bps; j++)
                    ASEMain.driveA.Data[offset + j] = ASEMain._mem.Read8(dmaAddress++);

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
            ASEMain._mem.Write8(dmaAddress++, (byte)headTrack);
            ASEMain._mem.Write8(dmaAddress++, (byte)currentSide);
            ASEMain._mem.Write8(dmaAddress++, sectorRegister);
            ASEMain._mem.Write8(dmaAddress++, 2);
            ASEMain._mem.Write8(dmaAddress++, 0);
            ASEMain._mem.Write8(dmaAddress++, 0);
            statusRegister = 0x00;
        }

        private static void ExecuteReadTrack() 
        { 
            statusRegister = 0x00; 
        }

        private static void ExecuteWriteTrack() 
        { 
            statusRegister = 0x00; 
        }

        private static int CalculateDiskOffset(int track, int side, int sector)
        {
            return (track * ASEMain.driveA.DiskConfig.Sides * ASEMain.driveA.DiskConfig.SectorsPerTrack + side * ASEMain.driveA.DiskConfig.SectorsPerTrack + (sector - 1)) * ASEMain.driveA.DiskConfig.SectorSize;
        }

        public static void SetWriteProtect(int drive, bool protect)
        {
            if (drive >= 0 && drive < 2) ASEMain.driveA.WriteProtected = protect;
        }
    }
}
