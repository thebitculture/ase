using System.IO.Compression;
using static ASE.Config;


namespace ASE
{
    public class FloppyImage
    {
        public class Configuration
        {
            public int SizeInBytes => Sides * Tracks * SectorsPerTrack * SectorSize;
            public int Sides = 2;
            public int Tracks = 80;
            public int SectorsPerTrack = 9;
            public int SectorSize = 512; // in bytes
        }

        public byte[] Data;
        public Configuration DiskConfig;

        // STX (Pasti) images keep their own per-track/per-sector structures instead of the
        // linear Data buffer; the WD1772 branches on Stx != null.
        public STXImage Stx;

        // Writes only modify the in-memory image, they are never written back to the file,
        // so disks can be left unprotected by default (games can save, TOS can write, etc.)
        public bool WriteProtected = false;

        // Path of the currently inserted image ("volume.zip|entry" for zipped images).
        // Stored in snapshots so the same disk can be re-inserted on restore.
        public string ImagePath = "";

        // Just the file name of what is in the drive, for the status bar: no path, and for a
        // zipped image the entry inside the volume, which is the disk that was actually inserted.
        public string DisplayName =>
            string.IsNullOrEmpty(ImagePath)
                ? ""
                : Path.GetFileName(ImagePath.Split('|').Last());

        public bool HasDisk => Data != null || Stx != null;

        // ---------------- Disk change detection ----------------
        //
        // The ST's floppy connector leaves pin 34 (DSKCHG) unconnected, so there is no signal
        // that says "the disk was swapped". TOS detects it by watching the write protect line
        // instead: every 8 VBLs it checks one drive's WPRT bit (bit 6 of the FDC status register
        // in Type I form) and treats any change as a media change — which is what makes GEMDOS
        // throw away its cached boot sector, FAT and directory, and what "insert disk 2" prompts
        // rely on. A drive with no disk reads exactly like a write-protected one (the sensor
        // cannot tell them apart), so removing and inserting a disk produces the transition.
        //
        // Swapping images in an emulator is instantaneous, so that transition has to be
        // reproduced explicitly: WPRT is forced high for a while after every insert or eject
        // (Hatari does the same in floppy.c, Floppy_DriveTransitionUpdateState). The window is
        // measured in VBLs because that is what TOS counts: 18 VBLs covers one full poll period
        // even with two drives connected, in which case each is only checked every 16 VBLs.
        //
        // Consequences that match the real machine: swapping two unprotected disks reads
        // 0 -> 1 -> 0 and is detected, while swapping two write-protected disks reads 1 -> 1 -> 1
        // and cannot be detected at all.
        const long WpTransitionVbls = 18;

        // VBL at which the forced write protect ends; 0 (or any past VBL) means no transition.
        // Written from the UI thread (a disk is inserted from a menu, a drop or a dialog) and read
        // from the emulation thread, hence Volatile: nothing else has to be synchronized, the
        // value is a plain deadline the reader only compares against.
        long _wpForcedUntilVbl;

        /// <summary>
        /// Registers a disk insert/eject on this drive so the FDC reports "write protected" for
        /// the next few VBLs, which is how TOS notices the disk changed. Called from
        /// <see cref="Insert"/> and <see cref="Eject"/>, so every path that swaps a disk (menu,
        /// drag &amp; drop, zip entry, game library, command line) signals it.
        /// </summary>
        public void SignalDiskTransition() =>
            Volatile.Write(ref _wpForcedUntilVbl, ASEMain.VblCount + WpTransitionVbls);

        /// <summary>True while the insert/eject transition is holding the write protect line
        /// high. Read by the WD1772 when it composes the Type I status.</summary>
        public bool WpTransitionActive => ASEMain.VblCount < Volatile.Read(ref _wpForcedUntilVbl);

        /// <summary>Cancels a pending transition. Used on a machine reset: TOS re-reads
        /// everything from scratch, so there is nothing left to notify.</summary>
        public void ClearDiskTransition() => Volatile.Write(ref _wpForcedUntilVbl, 0);

        /// <summary>
        /// Loads a disk image into this drive. On success the disk change is signalled to the FDC
        /// (see <see cref="SignalDiskTransition"/>) so TOS and the running program notice it: this
        /// is the single entry point for every way of inserting a disk (menu, drag &amp; drop, zip
        /// entry, game library, command line, snapshot restore).
        /// Returns false when nothing was inserted, with the reason — or, for a zip holding
        /// several images, its file list — in <paramref name="message"/>.
        /// </summary>
        public bool Insert(string path, out string message)
        {
            bool inserted = LoadImage(path, out message);

            // Only a successful insert is signalled here: the failure paths eject, and Eject
            // signals the transition itself. The "several images in the zip" path does neither,
            // it leaves the current disk in place until the user picks one.
            if (inserted)
                SignalDiskTransition();

            return inserted;
        }

        bool LoadImage(string path, out string message)
        {
            string ZipVolume = "";
            ZipArchive zip = null;

            message = "";

            if (string.IsNullOrEmpty(path))
            {
                Eject();
                return false;
            }

            if (!path.Contains(".zip|", StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
            {
                message = $"Floppy image or zip file not found: [[red]]{path}[[/red]]";
                Eject();
                return false;
            }

            // The file is loaded from a zip volume
            if (path.Contains(".zip|", StringComparison.OrdinalIgnoreCase))
            {
                string[] FilenameParts = path.Split('|');
                ZipVolume = FilenameParts[0];
                path = FilenameParts[1];

                zip = ZipFile.OpenRead(ZipVolume);
            }

            // ZIP file
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zip = ZipFile.OpenRead(path);

                HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase) { ".st", ".msa", ".stx" };
                HashSet<string> ImagesInZip = new HashSet<string>();

                foreach (var entry in zip.Entries)
                {
                    var ext = Path.GetExtension(entry.Name);
                    if (_extensions.Contains(ext))
                        ImagesInZip.Add(entry.FullName);
                }

                // Single image in Zip
                if (ImagesInZip.Count == 1)
                {
                    zip.Dispose();
                    return Insert($"{path}|{ImagesInZip.First()}", out message);
                }
                else if (ImagesInZip.Count == 0)
                {
                    zip.Dispose();
                    message = $"No floppy images in zip file: [[red]]{path}[[/red]]";
                    Eject();
                    return false;
                }

                // Returns the list in the zip
                string fileList = "";
                foreach (string str in ImagesInZip)
                    fileList += str + "|";

                zip.Dispose();
                message = fileList.TrimEnd('|');
                return false;
            }
            // ST image format
            else if (path.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
            {
                byte[] image;

                // Read first: the geometry is worked out from the disk's own boot sector, not
                // from the size of the file holding it (see DetectStGeometry).
                if (zip == null)
                {
                    image = File.ReadAllBytes(path);
                }
                else
                {
                    var entry = zip.GetEntry(path);

                    using Stream entryStream = entry.Open();
                    using MemoryStream ms = new MemoryStream((int)entry.Length);

                    entryStream.CopyTo(ms);
                    image = ms.ToArray();
                }

                var geometry = DetectStGeometry(image, out string how);

                if (geometry == null)
                {
                    message = $"Floppy image geometry not recognized ({image.Length} bytes), ejected: [[red]]{path}[[/red]]";
                    Eject();
                    return false;
                }

                DiskConfig = geometry;
                Data = image;

                // The drive answers with the geometry, so the buffer has to match it exactly:
                // trailing junk past the last sector is dropped and a short last track is filled
                // with zeros rather than throwing when it is read.
                if (Data.Length != DiskConfig.SizeInBytes)
                    Array.Resize(ref Data, DiskConfig.SizeInBytes);

                Stx = null;

                ColoredConsole.WriteLine(
                    $"[[cyan]]FDC[[/cyan]] {Path.GetFileName(path)}: {DiskConfig.Tracks} tracks, " +
                    $"{DiskConfig.SectorsPerTrack} sectors, {DiskConfig.Sides} side(s) ({how})",
                    ConfigOptions.DebugModes.Quiet);

                message = $"Floppy image loaded: [[green]]{path}[[/green]]";
            }
            // MSA image format
            else if (path.EndsWith(".msa", StringComparison.OrdinalIgnoreCase))
            {
                using (Stream fileStream = (zip == null ? File.OpenRead(path) : zip.GetEntry(path).Open()))
                {
                    // Header 5 words:
                    //
                    // Word: Signature (&h0E0F)
                    // Word: Number of sectors
                    // Word: Number of sides
                    // Word: Start track
                    // Word: End track
                    byte[] signatureBytes = new byte[10];
                    fileStream.ReadExactly(signatureBytes, 0, 10);

                    // Check signature 0x0E0F big-endian
                    if (signatureBytes[0] != 0x0E || signatureBytes[1] != 0x0F)
                    {
                        message = $"Invalid MSA file: [[red]]{path}[[/red]]";
                        Eject();
                        return false;
                    }

                    DiskConfig = new Configuration
                    {
                        SectorSize = 512,
                        SectorsPerTrack = signatureBytes[3],
                        Sides = signatureBytes[5] + 1,
                        // Start/end track are inclusive (0..79 -> 80 tracks)
                        Tracks = signatureBytes[9] - signatureBytes[7] + 1
                    };

                    int totalSectors = DiskConfig.Sides * DiskConfig.Tracks * DiskConfig.SectorsPerTrack;
                    int trackDataSize = DiskConfig.SectorsPerTrack * DiskConfig.SectorSize;
                    Data = new byte[totalSectors * DiskConfig.SectorSize];

                    int index = 0;

                    for (int track = 0; track < DiskConfig.Tracks * DiskConfig.Sides; track++)
                    {
                        // Reads track size
                        byte[] trackSizeBytes = new byte[2];
                        fileStream.ReadExactly(trackSizeBytes, 0, 2);
                        int readedtrackSize = (trackSizeBytes[0] << 8) | trackSizeBytes[1];

                        // If track size == track data size, read directly
                        if (readedtrackSize == trackDataSize)
                        {
                            fileStream.ReadExactly(Data, index, trackDataSize);
                            index += trackDataSize;
                        }
                        // If track size < track data size, read RLE compressed data
                        else
                        {
                            int startindex = index;

                            do
                            {
                                byte bytestream = (byte)fileStream.ReadByte();

                                // If 0xE5, RLE compression
                                if (bytestream == 0xE5)
                                {
                                    // RLE compression -> 1 byte count, 1 byte repeated value
                                    byte value = (byte)fileStream.ReadByte();
                                    byte[] repeatBE = new byte[2];
                                    fileStream.ReadExactly(repeatBE, 0, 2);
                                    int count = (repeatBE[0] << 8) | repeatBE[1];
                                    for (int i = 0; i < count; i++)
                                    {
                                        Data[index] = value;
                                        index++;
                                    }
                                }
                                else
                                {
                                    Data[index] = bytestream;
                                    index++;
                                }
                            } while (index - startindex != trackDataSize);
                        }
                    }
                    Stx = null;
                    message = $"Floppy image loaded: [[green]]{path}[[/green]]";
                }
            }
            // STX (Pasti) image format
            else if (path.EndsWith(".stx", StringComparison.OrdinalIgnoreCase))
            {
                byte[] file;
                if (zip == null)
                {
                    file = File.ReadAllBytes(path);
                }
                else
                {
                    using Stream entryStream = zip.GetEntry(path).Open();
                    using MemoryStream ms = new MemoryStream();
                    entryStream.CopyTo(ms);
                    file = ms.ToArray();
                }

                Stx = STXImage.TryLoad(file, out string error);
                if (Stx == null)
                {
                    message = $"Invalid STX file ({error}): [[red]]{path}[[/red]]";
                    Eject();
                    return false;
                }
                Data = null;

                // Approximate geometry for the code paths shared with .ST/.MSA (head
                // clamping in seek/step); the actual sector layout comes from the
                // per-track STX structures.
                DiskConfig = new Configuration
                {
                    Sides = Stx.Sides,
                    Tracks = Math.Max(80, Stx.MaxTrack + 1),
                    SectorsPerTrack = 9,
                    SectorSize = 512
                };

                message = $"Floppy image loaded: [[green]]{path}[[/green]]";
            }
            else
            {
                // Unsupported format
                zip?.Dispose();
                Eject();
                message = $"Floppy image format not supported: [[red]]{path}[[/red]]";
                return false;
            }

            // The image is fully in memory by now; keeping the archive open would hold a file
            // handle on the zip (on Windows the user could not move or delete it afterwards).
            zip?.Dispose();

            ImagePath = string.IsNullOrEmpty(ZipVolume) ? path : $"{ZipVolume}|{path}";

            return true;
        }

        // ---------------- .ST geometry ----------------
        //
        // A raw .ST image is just the sectors of the disk one after another, with no header
        // saying how they are arranged, and the file size alone cannot say either: 737280 bytes
        // is the standard 80 tracks / 9 sectors / 2 sides disk, but it is equally 60/12/2 or
        // 48/15/2 - every factorisation of 1440 sectors fits. Answering with whichever came
        // first in a table of sizes is a coin toss, and it was losing it: the table was built
        // tracks-ascending, so a standard disk was read back as 60 tracks of 12 sectors.
        //
        // The disk carries the answer itself, in the boot sector's BIOS Parameter Block - the
        // same fields TOS reads to mount it. They are little-endian words (the BPB is
        // Intel-ordered even on a 68000): sectors per track at 24, sides at 26, and the total
        // sector count at 19, which is what says the block describes THIS file rather than
        // being whatever a protected loader left in its boot sector. The track count is taken
        // from the file rather than from the BPB whenever the file divides exactly, because
        // images with an extra track or two past what the BPB declares are common and those
        // tracks are real data.
        //
        // Only when the boot sector is not a usable BPB is the size guessed, and then in the
        // order of what an ST disk actually is (below) instead of table order. Semantics follow
        // Hatari's Floppy_FindDiskDetails.

        // BPB field offsets inside the boot sector.
        const int BpbBytesPerSector = 11;
        const int BpbTotalSectors = 19;
        const int BpbSectorsPerTrack = 24;
        const int BpbSides = 26;

        // Bounds a geometry has to stay inside to be believed at all: 90 tracks covers the 80 of
        // a standard disk plus every over-formatted variant (81-86), and 21 sectors the densest
        // HD track.
        const int MaxTracks = 90;
        const int MaxSectorsPerTrack = 21;

        // The sector counts ST disks are actually formatted with, most common first: 9 is the
        // standard, 10 and 11 the usual extended formats, 12 exists, 8 and 7 are rare, and the
        // last two are HD media. This order is the tie-breaker when several factorisations of
        // the same file size are possible.
        static readonly int[] SectorsPerTrackByPreference = { 9, 10, 11, 12, 8, 7, 18, 21 };

        static int ReadLE16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);

        /// <summary>
        /// Works out the geometry of a raw .ST image: from the boot sector's BPB when it holds
        /// one that describes this file, otherwise from the file size. Returns null when neither
        /// can explain it. <paramref name="source"/> says which of the two answered.
        /// </summary>
        static Configuration DetectStGeometry(byte[] image, out string source)
        {
            source = "";

            int totalSectors = image.Length / 512;

            if (totalSectors < 2)
                return null;

            // ---- the disk's own boot sector ----
            int bps = ReadLE16(image, BpbBytesPerSector);
            int spt = ReadLE16(image, BpbSectorsPerTrack);
            int sides = ReadLE16(image, BpbSides);
            int nsects = ReadLE16(image, BpbTotalSectors);

            int unit = spt * sides;

            // The block has to describe THIS file, because a loader that put its own code in the
            // boot sector can leave anything in those bytes: Sega's Super Hang-On declares 9
            // sectors per track, 1 side and a total of 9 sectors for an 819200-byte image. What
            // catches that one is the file not dividing into whole tracks of the shape it claims;
            // the declared total is otherwise allowed to fall a few tracks short of the file,
            // since an image with extra tracks past what the BPB says is common (Out Run: 82
            // tracks of real data, BPB says 80).
            bool usableBpb =
                bps == 512 &&
                (sides == 1 || sides == 2) &&
                spt >= 1 && spt <= MaxSectorsPerTrack &&
                nsects >= totalSectors - 8 * unit &&
                nsects <= totalSectors + 8 * unit;

            if (usableBpb)
            {
                // Tracks come from the file whenever it divides exactly: an image with tracks
                // past what the BPB declares still has them, and they are real data. Only when
                // the file is not a whole number of tracks does the declared total decide, and
                // then only if it is within a track of the file's own size.
                int tracks = totalSectors % unit == 0 ? totalSectors / unit
                           : Math.Abs(nsects - totalSectors) <= unit && nsects % unit == 0 ? nsects / unit
                           : 0;

                if (tracks >= 1 && tracks <= MaxTracks)
                {
                    source = "from boot sector";

                    return new Configuration
                    {
                        Sides = sides,
                        Tracks = tracks,
                        SectorsPerTrack = spt,
                        SectorSize = 512
                    };
                }
            }

            // ---- no usable BPB: guess, but in the order a real disk is likely to be ----
            //
            // Two passes, so a track count in the range an ST disk actually uses beats any other
            // factorisation that also divides the file exactly. Double-sided is tried first
            // within each pass, which is what makes a 368640-byte file come out as the
            // single-sided 80/9/1 it is on an ST rather than a PC-shaped 40/9/2.
            for (int pass = 0; pass < 2; pass++)
            {
                int minTracks = pass == 0 ? 78 : 20;
                int maxTracks = pass == 0 ? 86 : MaxTracks;

                for (int sides2 = 2; sides2 >= 1; sides2--)
                    foreach (int spt2 in SectorsPerTrackByPreference)
                    {
                        int unit2 = spt2 * sides2;

                        if (totalSectors % unit2 != 0)
                            continue;

                        int tracks = totalSectors / unit2;

                        if (tracks < minTracks || tracks > maxTracks)
                            continue;

                        source = "guessed from file size, no usable boot sector";

                        return new Configuration
                        {
                            Sides = sides2,
                            Tracks = tracks,
                            SectorsPerTrack = spt2,
                            SectorSize = 512
                        };
                    }
            }

            return null;
        }

        public void Eject()
        {
            ConfigOptions.RunninConfig.FloppyImagePath = "";
            ImagePath = "";
            Data = null;
            Stx = null;

            // An empty drive reads as write protected, but the change from the disk that was in
            // it has to be signalled all the same: without it TOS keeps using its cached
            // directory as if the disk were still there.
            SignalDiskTransition();
        }

    }
}
