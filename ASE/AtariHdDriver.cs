/*
 *
 * ASEHD boot driver installation.
 *
 * The driver is two flat binaries built from tools/asehd with vasm (build.cmd):
 *
 *   ASEHD.BOT - the boot sector (512 bytes): bootstrap plus room for the
 *               partition table. Its only job is loading the resident driver.
 *   ASEHD.SYS - the resident driver, stored in the reserved sectors between
 *               the root sector and the partition (sectors 1..31).
 *
 * Both travel inside the executable as embedded resources, so a hard disk image
 * created from the Hard disk tab comes out bootable with no extra step on the ST:
 * no install floppy, no utility to run.
 *
 * Installing is: write the driver to the reserved sectors, build the root sector
 * (bootstrap + the partition table already on the disk), patch the driver's length
 * into the bootstrap, and fix up the checksum word that makes TOS execute it — that
 * last step deliberately last, so a half-written install leaves a disk that does
 * not boot rather than one that boots into rubbish.
 *
 * Since driver 2.0 no geometry is patched into the binaries: the resident driver
 * parses the partition table itself at boot and mounts every GEM/BGM partition it
 * finds (primaries, XGM chains, ICD extras).
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Reflection;

namespace ASE
{
    /// <summary>
    /// Installs the ASEHD boot driver into an ACSI image. See <see cref="Install"/>.
    /// </summary>
    public static class AtariHdDriver
    {
        public const int SectorSize = 512;

        /// <summary>First sector holding the resident driver (sector 0 is the root sector).</summary>
        public const long DriverFirstSector = 1;

        /// <summary>First sector of the partition; everything below it is reserved.</summary>
        public const long PartitionFirstSector = 32;

        const long MaxDriverSectors = PartitionFirstSector - DriverFirstSector;

        // Offsets inside the boot sector, matching the layout at the top of BOOT.S.
        const int OffSignature = 2;     // 'ASEHD2'
        const int OffDrvSectors = 8;    // word
        const int OffDrvBytes = 10;     // long
        const int OffPartitionTable = 0x1C2;

        // Offset of the magic inside the resident driver (after the entry BRA.W).
        const int OffDriverMagic = 4;

        const string BootResource = "ASE.Resources.ASEHD.BOT";
        const string DriverResource = "ASE.Resources.ASEHD.SYS";
        const string SignatureText = "ASEHD2";

        static byte[] _boot, _driver;
        static bool _lookedUp;

        /// <summary>
        /// The boot sector binary, or null when the build carries none (the resources are
        /// optional so the emulator still builds and runs without them; images just come
        /// out unbootable).
        /// </summary>
        public static byte[] BootSector { get { LookUp(); return _boot; } }

        /// <summary>The resident driver binary, or null when the build carries none.</summary>
        public static byte[] DriverImage { get { LookUp(); return _driver; } }

        static void LookUp()
        {
            if (_lookedUp) return;
            _lookedUp = true;

            _boot = ReadResource(BootResource);
            _driver = ReadResource(DriverResource);
        }

        static byte[] ReadResource(string name)
        {
            try
            {
                using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) return null;

                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"ASEHD: could not read the embedded driver ({name}): {ex.Message}",
                                         Config.ConfigOptions.DebugModes.Quiet);
                return null;
            }
        }

        /// <summary>True when this build can make an image bootable.</summary>
        public static bool Available => Unavailable == null;

        /// <summary>
        /// Why the driver cannot be installed, or null when it can. Worth reporting rather
        /// than just saying "no driver": the usual reason is a stale binary left in
        /// <c>Resources</c> that no longer matches the sources in <c>tools/asehd</c>, and
        /// the message says which check failed.
        /// </summary>
        public static string Unavailable => Validate(out string problem) ? null : problem;

        /// <summary>
        /// Structural checks on both binaries, so a bad build is reported here instead of
        /// as a machine that will not boot.
        /// </summary>
        public static bool Validate(out string problem)
        {
            problem = null;

            byte[] boot = BootSector;
            byte[] driver = DriverImage;

            if (boot == null || driver == null)
            { problem = "this build carries no ASE HD driver"; return false; }

            // The boot sector is written as-is to sector 0; anything but exactly one
            // sector means BOOT.S no longer pads itself correctly.
            if (boot.Length != SectorSize)
            { problem = $"the boot sector is {boot.Length} bytes, not {SectorSize}"; return false; }

            for (int i = 0; i < SignatureText.Length; i++)
                if (boot[OffSignature + i] != (byte)SignatureText[i])
                { problem = "the ASEHD2 signature is missing from the boot sector"; return false; }

            // The resident driver must open with a 4-byte BRA.W to its init entry —
            // the bootstrap jumps to +0 — and carry the magic the bootstrap verifies
            // after loading it. $60 $00 is BRA.W; a BRA.S here means the sources were
            // assembled with branch optimisation on.
            if (driver.Length < 64)
            { problem = "the resident driver is implausibly small"; return false; }

            if (driver[0] != 0x60 || driver[1] != 0x00)
            { problem = "the driver does not start with a BRA.W (assembled with optimisation?)"; return false; }

            for (int i = 0; i < SignatureText.Length; i++)
                if (driver[OffDriverMagic + i] != (byte)SignatureText[i])
                { problem = "the ASEHD2 magic is missing from the resident driver"; return false; }

            long sectors = (driver.Length + SectorSize - 1) / SectorSize;
            if (sectors > MaxDriverSectors)
            { problem = $"the driver needs {sectors} sectors, only {MaxDriverSectors} are reserved"; return false; }

            return true;
        }

        /// <summary>
        /// Writes the driver into an image that already has its partition table and file
        /// system, and makes the root sector bootable.
        /// <paramref name="rootSector"/> is the image's sector 0 as it stands: its partition
        /// table is kept and everything below it is replaced by the bootstrap. Passing it in
        /// rather than reading it back means the stream only ever has to be writable, and the
        /// same routine serves both a fresh image and a driver update on a disk with files on
        /// it — the partition itself is never touched either way. The driver reads that same
        /// partition table at boot, so there is no geometry to patch anywhere.
        /// </summary>
        public static bool Install(FileStream fs, byte[] rootSector)
        {
            if (!Validate(out string problem))
            {
                ColoredConsole.WriteLine($"ASEHD: image left unbootable — {problem}.",
                                         Config.ConfigOptions.DebugModes.Quiet);
                return false;
            }

            byte[] driver = DriverImage;
            int driverSectors = (int)((driver.Length + SectorSize - 1) / SectorSize);

            // ---- the resident driver, padded to whole sectors ----
            var resident = new byte[driverSectors * SectorSize];
            Array.Copy(driver, resident, driver.Length);

            fs.Position = DriverFirstSector * SectorSize;
            fs.Write(resident, 0, resident.Length);

            // ---- the root sector: bootstrap + the partition table already on the disk ----
            var root = new byte[SectorSize];
            Array.Copy(BootSector, 0, root, 0, OffPartitionTable);                   // bootstrap
            Array.Copy(rootSector, OffPartitionTable, root, OffPartitionTable,       // partitions
                       SectorSize - OffPartitionTable);

            WriteBE16(root, OffDrvSectors, (ushort)driverSectors);
            WriteBE32(root, OffDrvBytes, (uint)driver.Length);
            FixChecksum(root);

            fs.Position = 0;
            fs.Write(root, 0, SectorSize);
            fs.Flush();

            ColoredConsole.WriteLine(
                $"ASEHD: boot driver installed ({driver.Length:N0} bytes, {driverSectors} sectors); " +
                $"the disk boots on its own.",
                Config.ConfigOptions.DebugModes.Quiet);

            return true;
        }

        /// <summary>What a disk's root sector says about it, for the reinstall confirmation.</summary>
        public readonly struct DiskInfo
        {
            public bool Valid { get; init; }
            public string Problem { get; init; }
            public long PartitionStart { get; init; }
            public long PartitionSectors { get; init; }
            public int PartitionCount { get; init; }
            public string PartitionId { get; init; }
            public bool AlreadyOurs { get; init; }
            public bool Bootable { get; init; }
        }

        /// <summary>
        /// Reads an image's root sector and works out whether the driver can be installed on it.
        /// Used to tell the user what they are about to overwrite before anything is written.
        /// Only the four primary entries are examined here: enough to describe the disk and to
        /// verify the reserved area is free — the driver itself digs deeper at boot.
        /// </summary>
        public static DiskInfo Examine(string path)
        {
            try
            {
                var root = new byte[SectorSize];

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < PartitionFirstSector * SectorSize)
                        return new DiskInfo { Problem = "the image is too small to hold a partition" };

                    int read = 0;
                    while (read < SectorSize)
                    {
                        int n = fs.Read(root, read, SectorSize - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < SectorSize)
                        return new DiskInfo { Problem = "could not read the root sector" };
                }

                // Count the partitions the primary table declares; the first one is the one
                // whose start decides whether the reserved area is really free.
                int count = 0;
                long start = 0, sectors = 0;
                string id = "";

                for (int i = 0; i < 4; i++)
                {
                    int p = OffPartitionTable + 4 + i * 12;
                    if ((root[p] & 0x01) == 0) continue;

                    count++;
                    if (count > 1) continue;

                    id = $"{(char)root[p + 1]}{(char)root[p + 2]}{(char)root[p + 3]}";
                    start = ReadBE32(root, p + 4);
                    sectors = ReadBE32(root, p + 8);
                }

                if (count == 0 || sectors == 0)
                    return new DiskInfo { Problem = "the disk has no partition table this driver understands" };

                // The reserved area has to be genuinely free. A disk laid out by another driver
                // may well start its partition earlier than ours does, and writing the driver
                // there would land inside the file system.
                byte[] driver = DriverImage;
                long needed = DriverFirstSector +
                              (driver == null ? 0 : (driver.Length + SectorSize - 1) / SectorSize);

                if (start < needed)
                    return new DiskInfo
                    {
                        Problem = $"the partition starts at sector {start}, but the driver needs " +
                                  $"sectors {DriverFirstSector} to {needed - 1}. Installing would " +
                                  "overwrite the file system."
                    };

                ushort sum = 0;
                for (int i = 0; i < SectorSize; i += 2)
                    sum += (ushort)((root[i] << 8) | root[i + 1]);

                // "ASEHD" without the version digit: an ASEHD1 disk being updated to 2.0 is
                // still "already ours".
                bool ours = true;
                for (int i = 0; i < SignatureText.Length - 1; i++)
                    if (root[OffSignature + i] != (byte)SignatureText[i]) { ours = false; break; }

                return new DiskInfo
                {
                    Valid = true,
                    PartitionStart = start,
                    PartitionSectors = sectors,
                    PartitionCount = count,
                    PartitionId = id,
                    AlreadyOurs = ours,
                    Bootable = sum == 0x1234,
                };
            }
            catch (Exception ex)
            {
                return new DiskInfo { Problem = ex.Message };
            }
        }

        /// <summary>
        /// Installs the driver on an image that already exists, keeping its partition table and
        /// everything inside the partition. Call <see cref="Examine"/> first and show the user
        /// what it reports: this replaces whatever boot sector and driver the disk had.
        /// </summary>
        public static bool Reinstall(string path, out string error)
        {
            error = null;

            DiskInfo info = Examine(path);
            if (!info.Valid) { error = info.Problem; return false; }

            try
            {
                var root = new byte[SectorSize];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                int read = 0;
                while (read < SectorSize)
                {
                    int n = fs.Read(root, read, SectorSize - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (!Install(fs, root))
                {
                    error = "the driver in this build is not usable";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// TOS executes the root sector only when its 256 big-endian words add up to $1234;
        /// the last word is chosen to make that true.
        /// </summary>
        public static void FixChecksum(byte[] sector)
        {
            sector[SectorSize - 2] = 0;
            sector[SectorSize - 1] = 0;

            ushort sum = 0;
            for (int i = 0; i < SectorSize; i += 2)
                sum += (ushort)((sector[i] << 8) | sector[i + 1]);

            WriteBE16(sector, SectorSize - 2, (ushort)(0x1234 - sum));
        }

        static uint ReadBE32(byte[] b, int off) =>
            (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

        static void WriteBE32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }

        static void WriteBE16(byte[] b, int off, ushort v)
        {
            b[off] = (byte)(v >> 8); b[off + 1] = (byte)v;
        }
    }
}
