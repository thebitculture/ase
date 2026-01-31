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
