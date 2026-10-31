/*
 *
 * Atari hard disk image layout.
 *
 * A raw image of zeros is not something TOS can use: it needs a root sector with a
 * partition table and, inside the partition, a FAT file system. Both are written here
 * rather than by a tool on the ST, so the arithmetic can be checked in one place:
 *
 *   sector 0        root sector: partition table (no boot checksum yet — the disk only
 *                   becomes bootable once AtariHdDriver installs the ASEHD driver)
 *   sectors 1-31    reserved for that driver
 *   sector 32...    partition C: — boot record with the BPB, two FATs, root directory
 *
 * Two users: the Hard disk tab's *Create image* (an empty disk of any size, see
 * CreateHardDiskWindow) and the library's hard-disk games (a small disk built around one
 * game, see HardDiskGames), which also fills the file system through FatImageWriter.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE
{
    /// <summary>
    /// Writes the partition table and an empty FAT file system into an ACSI image. See the
    /// file header for the layout.
    /// </summary>
    public static class AtariHdImage
    {
        const int RootSectorPartitionTable = 0x1C2;

        /// <summary>FAT16 starts at 4085 clusters, below that it is FAT12 — the rule GEMDOS
        /// and the ASEHD driver both apply to the cluster count, never a choice.</summary>
        public const long Fat16MinClusters = 4085;

        /// <summary>Where everything of one formatted partition lives, in absolute sectors.</summary>
        public sealed class Layout
        {
            public long PartitionStart { get; init; }
            public long PartitionSectors { get; init; }
            public int SectorsPerCluster { get; init; }
            public int ReservedSectors { get; init; }
            public int NumFats { get; init; }
            public int SectorsPerFat { get; init; }
            public int RootEntries { get; init; }
            public int RootSectors { get; init; }
            public long Clusters { get; init; }

            /// <summary>Sector 0 as written: the partition table, for
            /// <see cref="AtariHdDriver.Install"/> to keep when it makes the disk bootable.</summary>
            public byte[] RootSector { get; init; }

            public long FatStart => PartitionStart + ReservedSectors;
            public long RootDirStart => FatStart + (long)NumFats * SectorsPerFat;
            public long DataStart => RootDirStart + RootSectors;
            public bool IsFat16 => Clusters >= Fat16MinClusters;
        }

        /// <summary>
        /// Writes the root sector, the partition's boot record and both FATs; everything else
        /// must already read as zero (a fresh file after <c>SetLength</c>, or a new buffer).
        /// The disk is not bootable afterwards: that is <see cref="AtariHdDriver.Install"/>,
        /// deliberately a separate last step. Returns null for an image too small to hold a
        /// file system, which is then left blank.
        /// <paramref name="bootPartition"/> flags the partition bootable (bit 7 of its flag byte,
        /// AHDI's convention), which makes ASEHD switch the machine's boot drive to it — so the
        /// AUTO folder and the desktop's INF file come from C: instead of A:.
        /// </summary>
        public static Layout Format(Stream fs, long totalSectors, bool bootPartition = false)
        {
            int sectorSize = Acsi.SectorSize;
            long partSectors = totalSectors - AtariHdDriver.PartitionFirstSector;
            if (partSectors < 64)
                return null;

            const int reserved = 1;         // the boot record itself
            const int numFats = 2;
            const int rootEntries = 512;
            int rootSectors = rootEntries * 32 / sectorSize;

            // Sectors per cluster: the smallest power of two that keeps the cluster count inside
            // the 16-bit field of the BPB, with room to spare (32768 rather than 65535). A small
            // image goes the other way — with too few clusters the file system is FAT12, not
            // FAT16, so one sector per cluster is used to stay in 16-bit territory as long as
            // possible. Below ~2 MB it is FAT12 whatever we do, and the driver reads that from
            // the cluster count rather than being told.
            int spc = 2;
            while (partSectors / spc > 32768 && spc < 64)
                spc <<= 1;
            if (spc == 2 && partSectors / 2 < Fat16MinClusters)
                spc = 1;

            // Sectors per FAT and cluster count depend on each other; a few passes settle it.
            int spf = 1;
            long clusters = 0;
            for (int i = 0; i < 8; i++)
            {
                long dataSectors = partSectors - reserved - (long)numFats * spf - rootSectors;
                clusters = dataSectors / spc;
                int needed = (int)(((clusters + 2) * 2 + sectorSize - 1) / sectorSize);
                if (needed == spf) break;
                spf = needed;
            }

            // The Atari boot record stores the partition length in a 16-bit field, which is what
            // caps an unaided TOS partition at 32 MB; past that it is left at zero and the real
            // length goes in the 32-bit fields: the partition table's, where an AHDI-style driver
            // reads it, and the boot record's own at $20 (below), where EmuTOS reads it.

            // ---- root sector: one partition, the rest of the table left empty ----
            var root = new byte[sectorSize];
            WriteBE32(root, RootSectorPartitionTable, (uint)totalSectors);

            int p = RootSectorPartitionTable + 4;
            root[p] = (byte)(bootPartition ? 0x81 : 0x01);      // exists (+ boot)
            // "BGM" is the id for a partition described by the 32-bit fields, "GEM" the classic
            // one TOS can also read on its own below 32 MB.
            string id = partSectors > 65535 ? "BGM" : "GEM";
            root[p + 1] = (byte)id[0]; root[p + 2] = (byte)id[1]; root[p + 3] = (byte)id[2];
            WriteBE32(root, p + 4, (uint)AtariHdDriver.PartitionFirstSector);
            WriteBE32(root, p + 8, (uint)partSectors);
            // The checksum word at $1FE stays zero on purpose: TOS only executes a root sector
            // whose words add up to $1234, so a freshly formatted image never tries to boot.

            fs.Position = 0;
            fs.Write(root, 0, sectorSize);

            // ---- partition boot record (BPB fields are little-endian) ----
            var boot = new byte[sectorSize];
            boot[0] = 0x60; boot[1] = 0x1C;                     // BRA.S over the BPB
            WriteAscii(boot, 2, "ASE   ", 6);                   // OEM
            boot[8] = 0x41; boot[9] = 0x53; boot[10] = 0x45;    // serial
            WriteLE16(boot, 11, sectorSize);                    // bytes per sector
            boot[13] = (byte)spc;                               // sectors per cluster
            WriteLE16(boot, 14, reserved);
            boot[16] = numFats;
            WriteLE16(boot, 17, rootEntries);
            WriteLE16(boot, 19, partSectors <= 65535 ? (int)partSectors : 0);
            boot[21] = 0xF8;                                    // media descriptor: fixed disk
            WriteLE16(boot, 22, spf);
            WriteLE16(boot, 24, 63);                            // sectors per track (cosmetic)
            WriteLE16(boot, 26, 16);                            // heads (cosmetic)
            WriteLE16(boot, 28, 0);                             // hidden sectors

            // Past 65535 sectors the 16-bit count at 19 is zero and the length goes in the
            // 32-bit field at $20, as on DOS. ASEHD takes it from the partition table, but
            // EmuTOS' own ACSI driver reads it here: left at zero it counts no clusters, every
            // allocation finds the disk full, and creating a folder ends in a bus error inside
            // EmuTOS (HARDWARE-NOTES.md, "A large partition that EmuTOS sees as full").
            if (partSectors > 65535)
                WriteLE32(boot, 32, (uint)partSectors);

            fs.Position = AtariHdDriver.PartitionFirstSector * sectorSize;
            fs.Write(boot, 0, sectorSize);

            // ---- both FATs: media descriptor plus the end-of-chain entry ----
            // Everything else is already zero, which is what "free cluster" means.
            var fatHead = new byte[] { 0xF8, 0xFF, 0xFF, 0xFF };
            for (int i = 0; i < numFats; i++)
            {
                fs.Position = (AtariHdDriver.PartitionFirstSector + reserved + (long)i * spf) * sectorSize;
                fs.Write(fatHead, 0, fatHead.Length);
            }

            ColoredConsole.WriteLine(
                $"ACSI: partition C: at sector {AtariHdDriver.PartitionFirstSector}, {partSectors:N0} sectors, " +
                $"{ClusterSizeText(spc * sectorSize)} clusters, {clusters:N0} of them, FAT {spf} sectors.",
                Config.ConfigOptions.DebugModes.Quiet);

            return new Layout
            {
                PartitionStart = AtariHdDriver.PartitionFirstSector,
                PartitionSectors = partSectors,
                SectorsPerCluster = spc,
                ReservedSectors = reserved,
                NumFats = numFats,
                SectorsPerFat = spf,
                RootEntries = rootEntries,
                RootSectors = rootSectors,
                Clusters = clusters,
                RootSector = root,
            };
        }

        // A small image ends up with 512-byte clusters, and "0 KB" reads like a bug.
        static string ClusterSizeText(int bytes) =>
            bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} byte";

        static void WriteBE32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
        }

        static void WriteLE16(byte[] b, int off, int v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        }

        static void WriteLE32(byte[] b, int off, uint v)
        {
            WriteLE16(b, off, (int)(v & 0xFFFF));
            WriteLE16(b, off + 2, (int)(v >> 16));
        }

        static void WriteAscii(byte[] b, int off, string s, int len)
        {
            for (int i = 0; i < len; i++)
                b[off + i] = (byte)(i < s.Length ? s[i] : ' ');
        }
    }

    /// <summary>No free cluster left: the caller can try again on a bigger image.</summary>
    public sealed class HardDiskFullException : IOException
    {
        public HardDiskFullException() : base("the hard disk image is full") { }
    }

    /// <summary>
    /// Fills a FAT16 partition laid out by <see cref="AtariHdImage.Format"/> with folders and
    /// files, over an image held in memory. Clusters are handed out in order, so every file
    /// comes out contiguous — the layout a freshly copied disk has, and the fastest for the
    /// driver to read. Names are taken as given and must already be TOS 8.3 names.
    /// </summary>
    public sealed class FatImageWriter
    {
        /// <summary>A directory being filled: where its entries go, and what is already in it
        /// (the names, so a collision is caught here rather than as two files sharing one).</summary>
        public sealed class Folder
        {
            internal readonly List<long> Sectors = new();       // absolute sectors of the entry table
            internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
            internal int Used;                                  // entries written
            internal ushort Cluster;                            // 0 = the root directory
            internal readonly bool IsRoot;

            internal Folder(bool isRoot) { IsRoot = isRoot; }

            /// <summary>Whether the folder already holds an entry of this 8.3 name.</summary>
            public bool Contains(string atariName) => Names.Contains(atariName);
        }

        const int SectorSize = 512;
        const int EntrySize = 32;
        const byte AttrDirectory = 0x10;
        const byte AttrArchive = 0x20;

        readonly byte[] _image;
        readonly AtariHdImage.Layout _layout;
        readonly int _clusterBytes;
        long _nextCluster = 2;                                  // 0 and 1 are reserved entries

        public Folder Root { get; }

        public FatImageWriter(byte[] image, AtariHdImage.Layout layout)
        {
            if (!layout.IsFat16)
                throw new InvalidOperationException("the partition is FAT12; only FAT16 is written");

            _image = image;
            _layout = layout;
            _clusterBytes = layout.SectorsPerCluster * SectorSize;

            Root = new Folder(isRoot: true);
            for (int i = 0; i < layout.RootSectors; i++)
                Root.Sectors.Add(layout.RootDirStart + i);
        }

        /// <summary>Creates a subfolder, with its "." and ".." entries.</summary>
        public Folder AddFolder(Folder parent, string atariName, DateTime modified)
        {
            ushort cluster = AllocateChain(1);
            ClearCluster(cluster);

            WriteEntry(parent, atariName, AttrDirectory, cluster, 0, modified);

            var folder = new Folder(isRoot: false) { Cluster = cluster };
            AddClusterSectors(folder, cluster);

            // ".." points at the parent's first cluster, which for the root is 0 — there is no
            // cluster behind the root directory, and that is what GEMDOS expects to find.
            WriteRawEntry(folder, ".", AttrDirectory, cluster, 0, modified);
            WriteRawEntry(folder, "..", AttrDirectory, parent.IsRoot ? (ushort)0 : parent.Cluster, 0, modified);

            return folder;
        }

        /// <summary>Stores a file's contents and its directory entry.</summary>
        public void AddFile(Folder parent, string atariName, byte[] data, DateTime modified)
        {
            ushort first = 0;

            if (data.Length > 0)
            {
                int count = (int)((data.Length + _clusterBytes - 1) / _clusterBytes);
                first = AllocateChain(count);

                // The chain was allocated in one run, so the data is one block too
                Array.Copy(data, 0, _image, ClusterOffset(first), data.Length);
            }

            WriteEntry(parent, atariName, AttrArchive, first, (uint)data.Length, modified);
        }

        // ==================== Clusters ====================

        /// <summary>Takes <paramref name="count"/> consecutive clusters and links them in both
        /// FATs. Returns the first one.</summary>
        ushort AllocateChain(int count)
        {
            if (_nextCluster + count > _layout.Clusters + 2)
                throw new HardDiskFullException();

            long first = _nextCluster;
            for (long c = first; c < first + count; c++)
                SetFat(c, (ushort)(c == first + count - 1 ? 0xFFFF : c + 1));

            _nextCluster += count;
            return (ushort)first;
        }

        void SetFat(long cluster, ushort value)
        {
            for (int f = 0; f < _layout.NumFats; f++)
            {
                long offset = (_layout.FatStart + (long)f * _layout.SectorsPerFat) * SectorSize + cluster * 2;
                _image[offset] = (byte)value;               // FAT entries are little-endian
                _image[offset + 1] = (byte)(value >> 8);
            }
        }

        long ClusterOffset(long cluster) =>
            (_layout.DataStart + (cluster - 2) * _layout.SectorsPerCluster) * SectorSize;

        void ClearCluster(long cluster) => Array.Clear(_image, (int)ClusterOffset(cluster), _clusterBytes);

        void AddClusterSectors(Folder folder, long cluster)
        {
            long first = ClusterOffset(cluster) / SectorSize;
            for (int i = 0; i < _layout.SectorsPerCluster; i++)
                folder.Sectors.Add(first + i);
        }

        // ==================== Directory entries ====================

        void WriteEntry(Folder folder, string atariName, byte attr, ushort cluster, uint size, DateTime modified)
        {
            if (!folder.Names.Add(atariName))
                throw new IOException($"two entries named {atariName} in one folder");

            WriteRawEntry(folder, atariName, attr, cluster, size, modified);
        }

        void WriteRawEntry(Folder folder, string name, byte attr, ushort cluster, uint size, DateTime modified)
        {
            int perSector = SectorSize / EntrySize;

            if (folder.Used >= folder.Sectors.Count * perSector)
            {
                // The root directory has a fixed size; a subfolder grows by a cluster
                if (folder.IsRoot)
                    throw new IOException("the root directory is full");

                ushort extra = AllocateChain(1);
                ClearCluster(extra);
                SetFat(LastCluster(folder), extra);
                AddClusterSectors(folder, extra);
            }

            long sector = folder.Sectors[folder.Used / perSector];
            int offset = (int)(sector * SectorSize + folder.Used % perSector * EntrySize);
            folder.Used++;

            // Name and extension, space padded; "." and ".." are names, not extensions
            string stem = name, ext = "";
            int dot = name.StartsWith('.') ? -1 : name.LastIndexOf('.');
            if (dot > 0)
            {
                stem = name.Substring(0, dot);
                ext = name.Substring(dot + 1);
            }

            WritePadded(offset, stem, 8);
            WritePadded(offset + 8, ext, 3);
            _image[offset + 11] = attr;

            // DOS time and date, little-endian like everything else in the entry
            ushort time = (ushort)((modified.Hour << 11) | (modified.Minute << 5) | (modified.Second / 2));
            int year = Math.Clamp(modified.Year - 1980, 0, 127);
            ushort date = (ushort)((year << 9) | (modified.Month << 5) | modified.Day);

            WriteLE16(offset + 22, time);
            WriteLE16(offset + 24, date);
            WriteLE16(offset + 26, cluster);
            WriteLE16(offset + 28, (ushort)size);
            WriteLE16(offset + 30, (ushort)(size >> 16));
        }

        /// <summary>Last cluster of a subfolder's chain: its entry table is allocated in order,
        /// so that is the cluster of its last sector.</summary>
        long LastCluster(Folder folder) =>
            (folder.Sectors[^1] - _layout.DataStart) / _layout.SectorsPerCluster + 2;

        void WritePadded(int offset, string s, int length)
        {
            for (int i = 0; i < length; i++)
                _image[offset + i] = (byte)(i < s.Length ? s[i] : ' ');
        }

        void WriteLE16(int offset, ushort v)
        {
            _image[offset] = (byte)v;
            _image[offset + 1] = (byte)(v >> 8);
        }
    }
}
