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
        public List<Configuration> Configurations = new List<Configuration>();

        // STX (Pasti) images keep their own per-track/per-sector structures instead of the
        // linear Data buffer; the WD1772 branches on Stx != null.
        public STXImage Stx;

        // Writes only modify the in-memory image, they are never written back to the file,
        // so disks can be left unprotected by default (games can save, TOS can write, etc.)
        public bool WriteProtected = false;

        // Path of the currently inserted image ("volume.zip|entry" for zipped images).
        // Stored in snapshots so the same disk can be re-inserted on restore.
        public string ImagePath = "";

        public bool HasDisk => Data != null || Stx != null;

        public FloppyImage()
        {
            // Make array of disk configurations from file sizes.
            for (int caras = 1; caras < 3; caras++)
                for (int pistas = 79; pistas < 83; pistas++)
                    for (int sectores = 8; sectores < 13; sectores++)
                        Configurations.Add(new Configuration()
                        {
                            Sides = caras,
                            Tracks = pistas,
                            SectorsPerTrack = sectores,
                            SectorSize = 512
                        });
        }

        public bool Insert(string path, out string message)
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

            // El fichero se carga desde un volumen zip
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

                message = fileList.TrimEnd('|');
                return false;
            }
            // ST image format
            else if (path.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
            {
                bool FilesizeMatched = false;
                long ImageSize = (zip == null ? new FileInfo(path).Length : zip.GetEntry(path).Length);

                // Deduce the disk configuration from the file size.
                Configurations.Find(x =>
                {
                    if (x.SizeInBytes == ImageSize)
                    {
                        DiskConfig = x;
                        FilesizeMatched = true;
                        return true;
                    }
                    return false;
                });

                if (!FilesizeMatched)
                {
                    message = $"Floppy image unknown size, ejected: [[red]]{path}[[/red]]";
                    Eject();
                    return false;
                }

                if (zip == null)
                {
                    Data = File.ReadAllBytes(path);
                }
                else
                {
                    using Stream entryStream = zip.GetEntry(path).Open();
                    using MemoryStream ms = new MemoryStream((int)ImageSize);

                    entryStream.CopyTo(ms);
                    Data = ms.ToArray();
                }

                Stx = null;
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
                Eject();
                message = $"Floppy image format not supported: [[red]]{path}[[/red]]";
                return false;
            }

            ImagePath = string.IsNullOrEmpty(ZipVolume) ? path : $"{ZipVolume}|{path}";

            return true;
        }

        public void Eject()
        {
            ConfigOptions.RunninConfig.FloppyImagePath = "";
            ImagePath = "";
            Data = null;
            Stx = null;
        }

    }
}
