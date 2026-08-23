using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TinyDialogsNet;

namespace ASE;

/// <summary>
/// Creates an ACSI hard disk image: a raw file of whole 512-byte sectors, laid out ready to
/// use — one partition, a FAT16 file system, and the ASEHD boot driver (see AtariHdDriver)
/// installed in the sectors reserved for it. The disk boots on its own, with nothing to run
/// on the ST first.
///
/// Third-party drivers (ICD Pro, PPutnik, HDDriver…) re-partition and re-format to their own
/// taste, so an image can still be handed to one of those; it simply overwrites this.
///
/// Shown as a modal dialog from the Configuration window's Hard disk tab; closes returning the
/// full path of the created image, or <c>null</c> when nothing was created.
/// </summary>
public partial class CreateHardDiskWindow : Window
{
    // A class-0 ACSI read/write carries a 21-bit LBA (see Acsi.GetLba), which tops out at
    // 2^21 sectors = 1 GB. The ASEHD driver switches to class-1 commands above that, but the
    // cap stays: a bigger disk needs a cluster size that wastes more than it gains.
    const long MaxSizeMB = 1024;
    const long MinSizeMB = 1;

    const long DefaultSizeMB = 32;

    const string DefaultFileName = "harddisk.img";

    static readonly FileFilter ImageFilter =
        new("Hard disk image", ["*.img", "*.hd", "*.acsi", "*.raw", "*.ahd"]);

    // Avalonia's runtime XAML loader (and the designer) look for a parameterless constructor.
    public CreateHardDiskWindow() : this("") { }

    /// <param name="suggestedPath">
    /// Image path the Hard disk tab currently holds, used only to open the file dialog on a
    /// sensible folder. Empty falls back to the configured disk-images directory.
    /// </param>
    public CreateHardDiskWindow(string suggestedPath)
    {
        InitializeComponent();

        string folder = "";

        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            try
            {
                folder = Path.GetDirectoryName(suggestedPath) ?? "";
            }
            catch
            {
                // A malformed path in the text box is not worth reporting here
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
            folder = Config.ConfigOptions.RunninConfig.DiskImagesPath ?? "";

        TextImagePath.Text = Directory.Exists(folder)
            ? Path.Combine(folder, DefaultFileName)
            : DefaultFileName;

        TextSize.Text = DefaultSizeMB.ToString(CultureInfo.InvariantCulture);
        TextSize.TextChanged += (_, _) => RefreshGeometry();

        RefreshGeometry();
    }

    // The emulator parks while the window is open. The Configuration window that opens this one
    // already holds a pause; the count makes the nesting harmless.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ASEMain.EnterUiPause();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ASEMain.ExitUiPause();
    }

    /// <summary>Echoes the size the way the emulated controller sees it, and flags a bad value.</summary>
    void RefreshGeometry()
    {
        if (!TryParseSize(out long mb))
        {
            TextGeometry.Text = $"Enter a whole number of megabytes between {MinSizeMB} and {MaxSizeMB}.";
            return;
        }

        long sectors = mb * 1024 * 1024 / Acsi.SectorSize;
        TextGeometry.Text = $"{mb} MB - {sectors:N0} sectors of {Acsi.SectorSize} bytes.";
    }

    bool TryParseSize(out long mb)
    {
        bool ok = long.TryParse((TextSize.Text ?? "").Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out mb);

        return ok && mb >= MinSizeMB && mb <= MaxSizeMB;
    }

    void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
            TextSize.Text = tag;
    }

    async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var (canceled, path) = await Dialogs.SaveFile("New hard disk image",
            TextImagePath.Text ?? DefaultFileName, ImageFilter);

        if (canceled || string.IsNullOrWhiteSpace(path))
            return;

        TextImagePath.Text = path;
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => Close(null);

    async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseSize(out long mb))
        {
            await Error($"The size must be a whole number of megabytes between {MinSizeMB} and {MaxSizeMB}.");
            return;
        }

        string path = (TextImagePath.Text ?? "").Trim();

        if (string.IsNullOrEmpty(path))
        {
            await Error("Choose where the image file should be created.");
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            await Error($"'{path}' is not a valid file name: {ex.Message}");
            return;
        }

        // Typing a bare name is natural; give it the extension the file dialogs filter on.
        if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".img";

        string dir = Path.GetDirectoryName(path) ?? "";

        if (!Directory.Exists(dir))
        {
            await Error($"The folder '{dir}' does not exist.");
            return;
        }

        // The running machine holds the attached image open, and truncating a disk under a
        // mounted file system corrupts it anyway. The OS error for this is cryptic, so it is
        // caught here instead.
        if (Acsi.EmulationOn && SamePath(path, Config.ConfigOptions.RunninConfig.AcsiImagePath))
        {
            await Error("That image is attached to the running machine. Reset with the ACSI disk switched off, or pick another file.");
            return;
        }

        if (File.Exists(path))
        {
            var answer = await Dialogs.MessageBox("Create hard disk image",
                $"'{Path.GetFileName(path)}' already exists.\n\nReplace it with a new empty image? Everything stored in it will be lost.",
                MessageBoxDialogType.YesNo, MessageBoxIconType.Warning, MessageBoxButton.No);

            if (answer != MessageBoxButton.Yes)
                return;
        }

        long bytes = mb * 1024 * 1024;

        try
        {
            // SetLength on a fresh file is instant and reads back as zeros on every platform —
            // no point writing a gigabyte of them, and the blocks are claimed as the ST uses them.
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.SetLength(bytes);
            WriteAtariLayout(fs, bytes / Acsi.SectorSize);
        }
        catch (Exception ex)
        {
            await Error($"Could not create '{path}': {ex.Message}");
            return;
        }

        ColoredConsole.WriteLine($"ACSI: created empty hard disk image [[green]]{path}[[/green]] " +
            $"({mb} MB, {bytes / Acsi.SectorSize:N0} sectors).",
            Config.ConfigOptions.DebugModes.Quiet);

        Close(path);
    }

    // ==================== Atari disk layout ====================
    // A raw image of zeros is not something TOS can use: it needs a root sector with a
    // partition table and, inside the partition, a FAT16 file system. Both are written here
    // rather than by a tool on the ST, so the arithmetic can be checked in one place.
    //
    //   sector 0        root sector: partition table (no boot checksum yet — the disk only
    //                   becomes bootable once tools/asehd installs its driver)
    //   sectors 1-31    reserved for that driver
    //   sector 32...    partition C: — boot record with the BPB, two FATs, root directory
    //
    // The Atari boot record stores the partition length in a 16-bit field, which is what caps
    // an unaided TOS partition at 32 MB; past that it is left at zero and the real length comes
    // from the 32-bit field in the partition table, which is where a driver reads it anyway.
    const int RootSectorPartitionTable = 0x1C2;

    static void WriteAtariLayout(FileStream fs, long totalSectors)
    {
        int sectorSize = Acsi.SectorSize;
        long partSectors = totalSectors - AtariHdDriver.PartitionFirstSector;
        if (partSectors < 64)
            return;                     // too small to hold a file system; leave it blank

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
        if (spc == 2 && partSectors / 2 < 4085)
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

        // ---- root sector: one partition, the rest of the table left empty ----
        var root = new byte[sectorSize];
        WriteBE32(root, RootSectorPartitionTable, (uint)totalSectors);

        int p = RootSectorPartitionTable + 4;
        root[p] = 0x01;                                     // exists
        // "BGM" is the id for a partition described by the 32-bit fields, "GEM" the classic
        // one TOS can also read on its own below 32 MB.
        string id = partSectors > 65535 ? "BGM" : "GEM";
        root[p + 1] = (byte)id[0]; root[p + 2] = (byte)id[1]; root[p + 3] = (byte)id[2];
        WriteBE32(root, p + 4, (uint)AtariHdDriver.PartitionFirstSector);
        WriteBE32(root, p + 8, (uint)partSectors);
        // The checksum word at $1FE stays zero on purpose: TOS only executes a root sector
        // whose words add up to $1234, so a freshly created image never tries to boot.

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

        // Last: make it boot on its own. Nothing to install on the ST, and a build carrying no
        // driver still produces a perfectly good — just unbootable — disk. The driver reads
        // the partition table itself at boot, so the geometry above is not passed anywhere.
        AtariHdDriver.Install(fs, root);
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

    static void WriteAscii(byte[] b, int off, string s, int len)
    {
        for (int i = 0; i < len; i++)
            b[off + i] = (byte)(i < s.Length ? s[i] : ' ');
    }

    static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static Task<MessageBoxButton> Error(string message)
        => Dialogs.MessageBox("Create hard disk image", message,
            MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
}
