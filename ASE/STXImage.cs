/*
 *
 * STX (Pasti) floppy image support for the WD1772 FDC emulation.
 *
 * The STX format stores, per track, the real ID field of every sector together
 * with the FDC status bits the original disk produced (CRC errors, missing data
 * fields, deleted data), a fuzzy-bit mask for sectors whose bits read
 * differently on every pass, and optional per-16-byte-block timing data for
 * sectors written with a variable bit rate. This is what copy protections
 * (Rob Northen Copylock, Macrodos/Speedlock, ...) actually check, and none of
 * it can be represented in a plain .ST/.MSA sector dump.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE
{
    public class STXImage
    {
        // FDC_Status flags of a sector descriptor. Bits 3, 4 and 5 have the same
        // meaning as in the WD1772 status register.
        public const byte SECTOR_FLAG_VARIABLE_TIME = 0x01; // variable bit width, timing data present
        public const byte SECTOR_FLAG_CRC = 0x08;           // CRC error (in data, or in ID when RNF is also set)
        public const byte SECTOR_FLAG_RNF = 0x10;           // ID field exists but there's no sector data
        public const byte SECTOR_FLAG_RECORD_TYPE = 0x20;   // deleted data address mark
        public const byte SECTOR_FLAG_FUZZY = 0x80;         // sector has fuzzy (unstable) bits

        // Track flags
        const ushort TRACK_FLAG_SECTOR_BLOCK = 0x01;        // track contains sector descriptors
        const ushort TRACK_FLAG_TRACK_IMAGE = 0x40;         // track contains a raw track image
        const ushort TRACK_FLAG_TRACK_IMAGE_SYNC = 0x80;    // the track image starts with a sync position word

        // Standard IBM/ISO double-density track layout (bytes), used to place the
        // sectors of "simple" tracks and to rebuild a raw track when no image is stored.
        public const int GAP1 = 60;      // track pre-gap, 0x4E
        public const int GAP2 = 12;      // sector ID pre-gap, 0x00
        public const int GAP3a = 22;     // sector ID post-gap, 0x4E
        public const int GAP3b = 12;     // sector data pre-gap, 0x00
        public const int GAP4 = 40;      // sector data post-gap, 0x4E
        public const int TRACK_BYTES_STANDARD = 6250;
        const int RAW_SECTOR_512 = GAP2 + 3 + 1 + 6 + GAP3a + GAP3b + 3 + 1 + 512 + 2 + GAP4;

        // Fixed timing table used for variable-time sectors of revision 0 files
        // (old Pasti tool, used e.g. by Macrodos). One big-endian word per block of
        // 16 bytes; a standard block is ~0x7F-0x80 units (1 unit = 32 FDC cycles).
        static readonly byte[] TimingDataDefault =
        {
            0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,
            0x00,0x85,0x00,0x85,0x00,0x85,0x00,0x85,0x00,0x85,0x00,0x85,0x00,0x85,0x00,0x85,
            0x00,0x79,0x00,0x79,0x00,0x79,0x00,0x79,0x00,0x79,0x00,0x79,0x00,0x79,0x00,0x79,
            0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F,0x00,0x7F
        };

        public class Sector
        {
            public int BitPosition;      // position in bits from the start of the track (points just after the IDAM $FE)
            public int ReadTime;         // read time of the whole sector in µs, 0 = standard (32 µs/byte)
            public byte IdTrack;         // content of the ID (address) field as recorded on the disk
            public byte IdHead;
            public byte IdSector;
            public byte IdSize;
            public ushort IdCrc;
            public byte FdcStatus;       // SECTOR_FLAG_* bits
            public int SectorSize;       // 128 << (IdSize & 3)
            public byte[] Data;          // sector payload, or null when RNF (no data field)
            public byte[] FuzzyMask;     // bit=1 stable, bit=0 fuzzy (random on each read); null if none
            public byte[] TimingData;    // 2 bytes big-endian per block of 16 bytes; null = constant rate

            /// <summary>ID field CRC is intentionally bad when both RNF and CRC flags are set.</summary>
            public bool IdCrcOk => (FdcStatus & (SECTOR_FLAG_RNF | SECTOR_FLAG_CRC)) != (SECTOR_FLAG_RNF | SECTOR_FLAG_CRC);
        }

        public class Track
        {
            public int TrackNumber;
            public int Side;
            public ushort Flags;
            public int MFMSize;                        // raw track length in bytes
            public List<Sector> Sectors = new();       // sorted by BitPosition (rotational order)
            public byte[] TrackImage;                  // raw track dump (read track data) or null

            /// <summary>Length in bytes of the raw track, used to derive the rotation time.</summary>
            public int TrackSizeBytes
            {
                get
                {
                    if (TrackImage != null && TrackImage.Length > 0)
                        return TrackImage.Length;
                    return MFMSize > 0 ? MFMSize : TRACK_BYTES_STANDARD;
                }
            }
        }

        public byte Revision;
        public int MaxTrack;    // highest track number present
        public int Sides;       // 1 or 2

        // Key: track | (side << 7), same encoding the STX TrackNumber byte uses
        readonly Dictionary<int, Track> _tracks = new();

        public Track GetTrack(int track, int side)
        {
            _tracks.TryGetValue((track & 0x7F) | (side << 7), out Track t);
            return t;
        }

        public static STXImage TryLoad(byte[] f, out string error)
        {
            error = "";
            try
            {
                return Parse(f);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        static ushort LE16(byte[] f, int o) => (ushort)(f[o] | (f[o + 1] << 8));
        static uint LE32(byte[] f, int o) => f[o] | ((uint)f[o + 1] << 8) | ((uint)f[o + 2] << 16) | ((uint)f[o + 3] << 24);

        static STXImage Parse(byte[] f)
        {
            if (f.Length < 16 || f[0] != 'R' || f[1] != 'S' || f[2] != 'Y' || f[3] != 0)
                throw new InvalidDataException("Not an STX file (missing RSY header)");

            ushort version = LE16(f, 4);
            if (version != 3)
                throw new InvalidDataException($"Unsupported STX version {version} (only 3 is supported)");

            var img = new STXImage
            {
                Revision = f[11],
                Sides = 1
            };
            int tracksCount = f[10];

            int p = 16;
            for (int t = 0; t < tracksCount; t++)
            {
                if (p + 16 > f.Length)
                    throw new InvalidDataException("Truncated STX file (track header)");

                int blockStart = p;
                int blockSize = (int)LE32(f, p);
                int fuzzySize = (int)LE32(f, p + 4);
                int sectorsCount = LE16(f, p + 8);
                ushort flags = LE16(f, p + 10);
                int mfmSize = LE16(f, p + 12);
                byte trackNumber = f[p + 14];
                p += 16;

                if (blockSize < 16 || blockStart + blockSize > f.Length)
                    throw new InvalidDataException("Truncated STX file (track block)");

                var track = new Track
                {
                    TrackNumber = trackNumber & 0x7F,
                    Side = (trackNumber >> 7) & 1,
                    Flags = flags,
                    // when the track only contains sector data (no descriptors), MFMSize is in bits
                    MFMSize = (flags & TRACK_FLAG_SECTOR_BLOCK) == 0 ? mfmSize / 8 : mfmSize
                };

                if (sectorsCount > 0 && (flags & TRACK_FLAG_SECTOR_BLOCK) == 0)
                {
                    // "Simple" track: just sectorsCount standard 512-byte sectors right after
                    // the header. Synthesize the descriptors with standard positions/CRCs.
                    int bytePosition = GAP1 + GAP2 + 4; // Pasti points just after the 3x$A1 + IDAM $FE
                    for (int s = 0; s < sectorsCount; s++)
                    {
                        var sec = new Sector
                        {
                            BitPosition = bytePosition * 8,
                            ReadTime = 0,
                            IdTrack = (byte)track.TrackNumber,
                            IdHead = (byte)track.Side,
                            IdSector = (byte)(s + 1),
                            IdSize = 2,
                            FdcStatus = 0,
                            SectorSize = 512,
                            Data = new byte[512]
                        };
                        sec.IdCrc = ComputeIdCrc(sec);
                        Array.Copy(f, p + s * 512, sec.Data, 0, 512);
                        track.Sectors.Add(sec);
                        bytePosition += RAW_SECTOR_512;
                    }
                }
                else
                {
                    // Descriptor layout: [sector descriptors][fuzzy masks][track data]
                    // where track data holds the optional raw track image, the sector
                    // payloads (at DataOffset) and the optional timing data.
                    int descBase = p;
                    int fuzzyBase = descBase + sectorsCount * 16;
                    int trackDataBase = fuzzyBase + fuzzySize;

                    int sectorsImageBase = trackDataBase;
                    if ((flags & TRACK_FLAG_TRACK_IMAGE) != 0)
                    {
                        int imgHdr = (flags & TRACK_FLAG_TRACK_IMAGE_SYNC) != 0 ? 4 : 2;
                        int imgSizeOff = trackDataBase + imgHdr - 2;
                        int imageSize = LE16(f, imgSizeOff);
                        track.TrackImage = new byte[imageSize];
                        Array.Copy(f, trackDataBase + imgHdr, track.TrackImage, 0, imageSize);
                        sectorsImageBase = trackDataBase + imgHdr + imageSize;
                    }

                    int fuzzyPos = fuzzyBase;
                    int maxSectorEnd = 0;
                    bool variableTimings = false;

                    for (int s = 0; s < sectorsCount; s++)
                    {
                        int d = descBase + s * 16;
                        var sec = new Sector
                        {
                            BitPosition = LE16(f, d + 4),
                            ReadTime = LE16(f, d + 6),
                            IdTrack = f[d + 8],
                            IdHead = f[d + 9],
                            IdSector = f[d + 10],
                            IdSize = f[d + 11],
                            IdCrc = (ushort)((f[d + 12] << 8) | f[d + 13]), // big-endian, as on disk
                            FdcStatus = f[d + 14]
                        };
                        int dataOffset = (int)LE32(f, d);

                        if ((sec.FdcStatus & SECTOR_FLAG_RNF) == 0)
                        {
                            sec.SectorSize = 128 << (sec.IdSize & 3);
                            sec.Data = new byte[sec.SectorSize];
                            Array.Copy(f, trackDataBase + dataOffset, sec.Data, 0, sec.SectorSize);

                            if ((sec.FdcStatus & SECTOR_FLAG_FUZZY) != 0)
                            {
                                sec.FuzzyMask = new byte[sec.SectorSize];
                                Array.Copy(f, fuzzyPos, sec.FuzzyMask, 0, sec.SectorSize);
                                fuzzyPos += sec.SectorSize;
                            }

                            if (dataOffset + sec.SectorSize > maxSectorEnd)
                                maxSectorEnd = dataOffset + sec.SectorSize;

                            if ((sec.FdcStatus & SECTOR_FLAG_VARIABLE_TIME) != 0)
                                variableTimings = true;
                        }
                        else
                        {
                            sec.SectorSize = 128 << (sec.IdSize & 3);
                        }

                        track.Sectors.Add(sec);
                    }

                    if (variableTimings)
                    {
                        if (img.Revision == 2)
                        {
                            // Timing block: word flags + word size + 2 bytes (big-endian) per
                            // block of 16 sector bytes, for each variable-time sector in order.
                            int timingBase = Math.Max(trackDataBase + maxSectorEnd, sectorsImageBase);
                            int timingPos = timingBase + 4;
                            foreach (var sec in track.Sectors)
                            {
                                if ((sec.FdcStatus & SECTOR_FLAG_RNF) != 0 ||
                                    (sec.FdcStatus & SECTOR_FLAG_VARIABLE_TIME) == 0)
                                    continue;
                                int len = (sec.SectorSize / 16) * 2;
                                if (timingPos + len <= f.Length)
                                {
                                    sec.TimingData = new byte[len];
                                    Array.Copy(f, timingPos, sec.TimingData, 0, len);
                                }
                                else
                                    sec.TimingData = TimingDataDefault;
                                timingPos += len;
                            }
                        }
                        else
                        {
                            // Revision 0 has no stored timings; use the fixed default table
                            foreach (var sec in track.Sectors)
                                if ((sec.FdcStatus & SECTOR_FLAG_RNF) == 0 &&
                                    (sec.FdcStatus & SECTOR_FLAG_VARIABLE_TIME) != 0)
                                    sec.TimingData = TimingDataDefault;
                        }
                    }
                }

                // Rotational order: the ID search walks the sectors by their position in the track
                track.Sectors.Sort((a, b) => a.BitPosition.CompareTo(b.BitPosition));

                img._tracks[(track.TrackNumber & 0x7F) | (track.Side << 7)] = track;
                if (track.TrackNumber > img.MaxTrack) img.MaxTrack = track.TrackNumber;
                if (track.Side == 1) img.Sides = 2;

                p = blockStart + blockSize;
            }

            return img;
        }

        /// <summary>CRC of the ID (address) field: computed over the 3 sync marks, the IDAM and the 4 ID bytes.</summary>
        static ushort ComputeIdCrc(Sector s)
        {
            byte[] field = { 0xA1, 0xA1, 0xA1, 0xFE, s.IdTrack, s.IdHead, s.IdSector, s.IdSize };
            ushort crc = 0xFFFF;
            foreach (byte b in field)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }
    }
}
