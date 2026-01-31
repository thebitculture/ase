/*
 * 
 * Western Digital WD1772 FDC emulation for Atari ST
 * 
 * https://info-coach.fr/atari/documents/_mydoc/FD-HD_Programming.pdf
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

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
        private static bool multiSectorInProgress;

        // Estado
        static int currentDrive = -1;
        static int currentSide;
        static int headTrack;
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

        private static void RaiseInterrupt() => Program._mfp.SetGPIOBit(5, false);

        // ClearInterrupt pone la línea en alto (inactivo)
        private static void ClearInterrupt() => Program._mfp.SetGPIOBit(5, true);

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
            dmaSectorCount = 0;
            statusRegister = 0;
            if (Program._mfp != null) Program._mfp.SetGPIOBit(5, true);
        }

        /*
         * I should move this method to Memory.cs ...
         */
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

        /*
         * ... and this one ...
         */
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
                    WriteByte(0xFF8609, (byte)(value >> 8));
                    return;

                case 0xFF860A: // -> $FF860B Mid
                    WriteByte(0xFF860B, (byte)(value >> 8));
                    return;

                case 0xFF860C: // -> $FF860D Low
                    WriteByte(0xFF860D, (byte)(value >> 8));
                    return;
            }

            WriteByte(address, (byte)(value >> 8));
            WriteByte(address + 1, (byte)(value & 0xFF));
        }

        /*
         * ... and this one ...
         */
        public static byte ReadByte(uint address)
        {
            switch (address)
            {
                case 0xFF8604: 
                    return Program._mem.Ports[address - Memory.PortsBase];
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
                    return Program._mem.Ports[address - Memory.PortsBase];
            }
        }

        /*
         * ... and this one too
         */
        public static ushort ReadWord(uint address)
        {
            switch (address)
            {
                case 0xFF8604: 
                    return (ushort)(0xFF00 | ReadFromFDCOrSectorCount());
                case 0xFF8606: 
                    return GetDMAStatus();
                case 0xFF8608: 
                    return (ushort)(ReadByte(0xFF8609) << 8 | 0x00);
                case 0xFF860A: 
                    return (ushort)(ReadByte(0xFF860B) << 8 | 0x00);
                case 0xFF860C: 
                    return (ushort)(ReadByte(0xFF860D) << 8 | 0x00);
                default:
                    return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
            }
        }

        private static void HandleDMAModeChange()
        {
            bool prevDir = (prevMode & 0x0100) != 0;
            bool newDir = (dmaModeRegister & 0x0100) != 0;

            if (prevDir != newDir)
            {
                // Reset DMA
                dmaSectorCount = 0;
                dmaError = false;
            }

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

        private static void UpdateTypeIStatus()
        {
            statusRegister = 0;
            statusRegister |= 0x80; // Motor On (Type I)
            statusRegister |= 0x20; // Spin-up completed

            if (headTrack == 0) 
                statusRegister |= STATUS_TRACK0;
            if (Program.driveA.WriteProtected) 
                statusRegister |= STATUS_WRITE_PROTECT;
            if ((System.Environment.TickCount & 32) != 0) 
                statusRegister |= 0x02; // Index Pulse
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

            /*
            FloppyImage activeDisk = (currentDrive == 0) ? Program.driveA : null;
            if (activeDisk == null || !activeDisk.HasDisk) 
            { 
                statusRegister |= STATUS_NOT_READY; 
                RaiseInterrupt(); 
                return; 
            }
            */

            statusRegister |= STATUS_BUSY;
            ClearInterrupt();

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

            // Type IV: force interrupr (0xD0-0xDF)
            if ((command & 0xF0) == CMD_FORCE_INTERRUPT)
            {
                // Termina cualquier operación multi-sector en curso
                statusRegister &= unchecked((byte)~STATUS_BUSY);
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
            Program._mfp.SetGPIOBit(5, false);
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

            if (!Program.driveA.HasDisk)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            if (headTrack < 0) 
                headTrack = 0;

            if (headTrack > Program.driveA.DiskConfig.Tracks - 1) 
                headTrack = Program.driveA.DiskConfig.Tracks - 1;

            UpdateTypeIStatus();
            statusRegister &= 0xFE;
        }

        private static void ExecuteReadSector()
        {
            bool multi = (commandRegister & 0x10) != 0; // bit 4 = multiple

            if (sectorRegister < 1 || sectorRegister > Program.driveA.DiskConfig.SectorsPerTrack)
            {
                statusRegister |= STATUS_RECORD_NOT_FOUND;
                return;
            }

            // En Atari ST el conteo DMA es por bloques de 512 y se decrementa por bloque
            // count=0 es error
            int sectorsToRead = 1;
            if (multi)
            {
                if (dmaSectorCount == 0)
                {
                    dmaError = true;
                    return;
                }
                sectorsToRead = dmaSectorCount;
            }

            // LBA lineal para wrap correcto
            int spt = Program.driveA.DiskConfig.SectorsPerTrack;
            int sides = Program.driveA.DiskConfig.Sides;
            int bps = Program.driveA.DiskConfig.SectorSize;

            int lba = ((headTrack * sides) + currentSide) * spt + (sectorRegister - 1);

            string dump = string.Empty;
            for (int n = 0; n < sectorsToRead; n++, lba++)
            {
                int offset = lba * bps;
                if (Program.driveA.Data == null || offset + bps > Program.driveA.Data.Length)
                {
                    statusRegister |= STATUS_RECORD_NOT_FOUND;
                    dmaError = true;
                    break;
                }

                for (int j = 0; j < bps; j++)
                {
                    Program.SetAtariWindowsTittle($"💾");
                    Program._mem.Write8(dmaAddress++, Program.driveA.Data[offset + j]);
                    dump += $"{Program.driveA.Data[offset + j]:X2} ";
                }

                if (dmaSectorCount > 0) dmaSectorCount--;
            }

            if (ConfigOptions.RunninConfig.DiskDump)
            {
                Console.WriteLine($"READ SECTOR: DMA={dmaAddress:X6} T={headTrack} S={currentSide} R={sectorRegister} count={sectorsToRead}");
                Console.Write(" Data loaded: " + dump);
                Console.Write(Environment.NewLine);
            }

            if (!multi)
            {
                statusRegister = 0x00;
            }
            else
            {
                multiSectorInProgress = true;
            }
        }

        private static void ExecuteWriteSector()
        {
            if (Program.driveA.WriteProtected) { statusRegister |= STATUS_WRITE_PROTECT; statusRegister &= 0xFE; return; }
            int sectorsToWrite = ((commandRegister & 0x10) != 0) ? Math.Max((byte)1, dmaSectorCount) : 1;
            for (int i = 0; i < sectorsToWrite; i++)
            {
                int offset = CalculateDiskOffset(headTrack, currentSide, sectorRegister + i);
                if (Program.driveA.Data != null && offset + Program.driveA.DiskConfig.SectorSize <= Program.driveA.Data.Length)
                {
                    for (int j = 0; j < Program.driveA.DiskConfig.SectorSize; j++)
                    {
                        Program.driveA.Data[offset + j] = Program._mem.Read8(dmaAddress++);
                        Program.SetAtariWindowsTittle($"💾");
                    }
                }
                if (dmaSectorCount > 0) dmaSectorCount--;
            }
            statusRegister = 0x00;
        }

        private static void ExecuteReadAddress()
        {
            Program._mem.Write8(dmaAddress++, (byte)headTrack);
            Program._mem.Write8(dmaAddress++, (byte)currentSide);
            Program._mem.Write8(dmaAddress++, sectorRegister);
            Program._mem.Write8(dmaAddress++, 2);
            Program._mem.Write8(dmaAddress++, 0);
            Program._mem.Write8(dmaAddress++, 0);
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
            return (track * Program.driveA.DiskConfig.Sides * Program.driveA.DiskConfig.SectorsPerTrack + side * Program.driveA.DiskConfig.SectorsPerTrack + (sector - 1)) * Program.driveA.DiskConfig.SectorSize;
        }

        public static void SetWriteProtect(int drive, bool protect)
        {
            if (drive >= 0 && drive < 2) Program.driveA.WriteProtected = protect;
        }
    }
}
