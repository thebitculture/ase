using Avalonia.Animation.Easings;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Text;
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

        public byte[]? Data;
        public Configuration? DiskConfig;
        public List<Configuration> Configurations = new List<Configuration>();

        public bool WriteProtected = true;

        public bool HasDisk => Data != null;

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
            message = "";

            if (string.IsNullOrEmpty(path))
            {
                Eject();
                return false;
            }

            if (File.Exists(path))
            {
                // ST image format
                if (path.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                {
                    bool FilesizeMatched = false;

                    // Deduce the disk configuration from the file size.
                    Configurations.Find(x =>
                    {
                        if (x.SizeInBytes == new FileInfo(path).Length)
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

                    Data = File.ReadAllBytes(path);
                    message = $"Floppy image loaded: [[green]]{path}[[/green]]";
                }
                // MSA image format
                else if (path.EndsWith(".msa", StringComparison.OrdinalIgnoreCase))
                {
                    using (FileStream fileStream = File.OpenRead(path))
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

                        DiskConfig = new Configuration();
                        DiskConfig.SectorSize = 512;
                        DiskConfig.SectorsPerTrack = signatureBytes[3];
                        DiskConfig.Sides = signatureBytes[5] + 1;
                        DiskConfig.Tracks = signatureBytes[9] - signatureBytes[7];

                        int totalSectors = DiskConfig.Sides * DiskConfig.Tracks * DiskConfig.SectorsPerTrack;
                        int trackDataSize = DiskConfig.SectorsPerTrack * DiskConfig.SectorSize;
                        Data = new byte[totalSectors * DiskConfig.SectorSize];
                        
                        int index = 0;

                        for(int track = 0; track < DiskConfig.Tracks * DiskConfig.Sides; track++)
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
                        message = $"Floppy image loaded: [[green]]{path}[[/green]]";
                    }
                }
                else
                {
                    // Unsupported format
                    Eject();
                    message = $"Floppy image format not supported: [[red]]{path}[[/red]]";
                    return false;
                }
            }
            else 
            {
                message = $"Floppy image file not found: [[red]]{path}[[/red]]";
                Eject();
                return false;
            }

            return true;
        }

        public void Eject() 
        {
            ConfigOptions.RunninConfig.FloppyImagePath = "";
            Data = null; 
        }

    }
}
