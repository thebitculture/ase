using ASE.Models;
using System.Text.Json;

namespace ASE.MT32;

/// <summary>
/// Per-game YM->MT-32 voice mappings, stored in the library's own <c>Library.json</c>
/// (<see cref="LibraryItem.Mt32PresetsProfile"/>) so a title's instruments only have to be
/// chosen once.
///
/// Loading a game from the library applies its profile to <see cref="YmMidiMapper"/>, but
/// only when the ST's MIDI is wired to the built-in module — the mapping means nothing
/// otherwise, and the Configuration window's mode change takes effect on the next reset
/// anyway. A disk that carries no profile — another library game, or one opened by hand —
/// <em>clears</em> the mapping instead of inheriting it: the previous title's instruments
/// would otherwise keep playing over whatever is loaded next.
///
/// The other direction is the MT-32 toolbox's save button (<see cref="SaveCurrentProfile"/>),
/// which writes whatever the mapper currently holds back into the entry.
///
/// UI thread only: every entry point is reached from a window (the library dialog, the disk
/// menu, the toolbox), and <see cref="YmMidiMapper.SetProgram"/>/
/// <see cref="YmMidiMapper.DrumsEnabled"/> are plain atomic stores the emulation thread
/// picks up on its next frame.
/// </summary>
public static class Mt32Profiles
{
    const string LibraryFile = "Library.json";

    // Same shape the scraper writes the catalogue with, so a saved profile does not
    // reformat the whole file underneath it.
    static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Library entry of the disk currently in drive A, or null when what is in the
    /// drive did not come from the library (File > Open, drag and drop, <c>--floppy</c>,
    /// a restored snapshot...).</summary>
    public static LibraryItem CurrentGame { get; private set; }

    /// <summary>Raised (UI thread) after <see cref="CurrentGame"/> changes and its profile
    /// has been applied, so an open toolbox can re-read the mapper and show or hide its save
    /// button.</summary>
    public static event Action CurrentGameChanged;

    /// <summary>
    /// Records which library entry is now in the drive and applies its stored mapping.
    /// Called from <c>MainWindow.InsertDisk</c> for every disk, with null for anything that
    /// did not come from the library.
    /// </summary>
    public static void SetCurrentGame(LibraryItem game)
    {
        CurrentGame = game;
        Apply(game?.Mt32PresetsProfile);
        CurrentGameChanged?.Invoke();
    }

    /// <summary>
    /// Points the YM->MT-32 mapper at the profile's instruments, or unmaps everything when
    /// there is no profile. Applied before the optional reboot that follows a disk change:
    /// <see cref="MidiManager.Initialize"/> power-cycles the module but keeps the mapped
    /// programs, re-sending them on the first frame after the reset.
    /// </summary>
    static void Apply(Mt32Toolbox.PresetsProfile profile)
    {
        // What the machine is actually wired to, not what the config currently says — the
        // Configuration window edits the latter live while it is open (see MidiManager.Mode).
        if (MidiManager.Mode != Config.ConfigOptions.MIDIEmulationOptions.BuiltInMT32)
            return;

        bool mapped = profile is { IsM32Mapped: true };

        YmMidiMapper.SetProgram(0, mapped ? ProgramOf(profile.Voice1Preset) : -1);
        YmMidiMapper.SetProgram(1, mapped ? ProgramOf(profile.Voice2Preset) : -1);
        YmMidiMapper.SetProgram(2, mapped ? ProgramOf(profile.Voice3Preset) : -1);
        YmMidiMapper.DrumsEnabled = mapped && profile.NoiseMapToDrums;
    }

    /// <summary>
    /// Writes the mapper's current state into the loaded game's entry in <c>Library.json</c>.
    /// The catalogue is re-read first rather than held in memory: the library dialog that
    /// produced <see cref="CurrentGame"/> is long closed by then, and the scraper may have
    /// rewritten the file in between. Returns false with a user-readable
    /// <paramref name="error"/>; the caller decides how loudly to say so.
    /// </summary>
    public static bool SaveCurrentProfile(out string error)
    {
        error = "";

        LibraryItem game = CurrentGame;
        if (game == null)
        {
            error = "no library game is loaded";
            return false;
        }

        string libraryPath = Config.ConfigOptions.RunninConfig.LibraryPath ?? "";
        string libraryJson = Path.Combine(libraryPath, LibraryFile);

        if (libraryPath.Length == 0 || !File.Exists(libraryJson))
        {
            error = $"the catalogue {libraryJson} could not be found";
            return false;
        }

        Mt32Toolbox.PresetsProfile profile = Capture();

        try
        {
            var collection = JsonSerializer.Deserialize<LibraryCollection>(File.ReadAllText(libraryJson));
            LibraryItem entry = collection?.Collection?.Find(item => SameEntry(item, game));

            if (entry == null)
            {
                error = "this game is no longer in the catalogue — rescan the library";
                return false;
            }

            entry.Mt32PresetsProfile = profile;

            // Through a temporary file: a half-written Library.json costs the user a full
            // rescrape, and one instrument mapping is worth far less than the catalogue.
            string temporary = libraryJson + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(collection, SerializerOptions));
            File.Move(temporary, libraryJson, overwrite: true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        // Keep the in-memory entry in step, so the mapping survives closing and reopening
        // the toolbox without going through the library again.
        game.Mt32PresetsProfile = profile;
        return true;
    }

    /// <summary>The mapper's current state as a profile, or null when nothing is mapped at
    /// all — which is how saving an empty mapping removes the game's profile.</summary>
    static Mt32Toolbox.PresetsProfile Capture()
    {
        var profile = new Mt32Toolbox.PresetsProfile
        {
            Voice1Preset = PresetName(YmMidiMapper.GetProgram(0)),
            Voice2Preset = PresetName(YmMidiMapper.GetProgram(1)),
            Voice3Preset = PresetName(YmMidiMapper.GetProgram(2)),
            NoiseMapToDrums = YmMidiMapper.DrumsEnabled,
        };

        profile.IsM32Mapped = profile.Voice1Preset.Length > 0
                           || profile.Voice2Preset.Length > 0
                           || profile.Voice3Preset.Length > 0
                           || profile.NoiseMapToDrums;

        return profile.IsM32Mapped ? profile : null;
    }

    /// <summary>Whether two entries are the same library game. The filename alone is not
    /// enough: a cracker menu disk yields one entry per game it contains, all sharing it
    /// (see <c>GameMenuIdentifier</c>).</summary>
    static bool SameEntry(LibraryItem a, LibraryItem b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.GameMenuId, b.GameMenuId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Program change a stored preset name stands for, or -1 for "not mapped" —
    /// which is also what an unknown name gives, so a hand-edited (or newer)
    /// <c>Library.json</c> never leaves a voice pointing at the wrong instrument.</summary>
    public static int ProgramOf(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return -1;

        return Array.FindIndex(Mt32Timbres.Presets,
            preset => string.Equals(preset, presetName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The preset name a program change selects, or "" for "not mapped". Names —
    /// not indices — are what travels in <c>Library.json</c>: they survive being read, and
    /// edited, by a human.</summary>
    public static string PresetName(int program)
        => program >= 0 && program < Mt32Timbres.Presets.Length ? Mt32Timbres.Presets[program] : "";
}
