namespace ASE.MT32;

/// <summary>
/// The Roland MT-32's 128 preset timbres, in program-change order: index <c>n</c> is what
/// the module plays on Program Change <c>n</c> under its default patch assignment, i.e.
/// groups A (1-64) and B (65-128) of the front-panel sound list.
///
/// The names are the ones printed in Roland's manual (and shown on the module's own LCD).
/// They are kept here rather than read from the ROMs because libmt32emu only exposes the
/// timbre currently assigned to a part (<c>mt32emu_get_patch_name</c>), never the whole
/// list — and because these are needed to fill a combo box whether or not a module (or a
/// ROM set) is present at all.
/// </summary>
public static class Mt32Timbres
{
    public static readonly string[] Presets =
    [
        // ---- Group A: 1-64 ----
        "AcouPiano1", "AcouPiano2", "AcouPiano3", "ElecPiano1",
        "ElecPiano2", "ElecPiano3", "ElecPiano4", "Honkytonk",
        "Elec Org 1", "Elec Org 2", "Elec Org 3", "Elec Org 4",
        "Pipe Org 1", "Pipe Org 2", "Pipe Org 3", "Accordion",
        "Harpsi 1",   "Harpsi 2",   "Harpsi 3",   "Clavi 1",
        "Clavi 2",    "Clavi 3",    "Celesta 1",  "Celesta 2",
        "Syn Brass1", "Syn Brass2", "Syn Brass3", "Syn Brass4",
        "Syn Bass 1", "Syn Bass 2", "Syn Bass 3", "Syn Bass 4",
        "Fantasy",    "Harmo Pan",  "Chorale",    "Glasses",
        "Soundtrack", "Atmosphere", "Warm Bell",  "Funny Vox",
        "Echo Bell",  "Ice Rain",   "Oboe 2001",  "Echo Pan",
        "DoctorSolo", "Schooldaze", "Bellsinger", "SquareWave",
        "Str Sect 1", "Str Sect 2", "Str Sect 3", "Pizzicato",
        "Violin 1",   "Violin 2",   "Cello 1",    "Cello 2",
        "Contrabass", "Harp 1",     "Harp 2",     "Guitar 1",
        "Guitar 2",   "Elec Gtr 1", "Elec Gtr 2", "Sitar",

        // ---- Group B: 65-128 ----
        "Acou Bass1", "Acou Bass2", "Elec Bass1", "Elec Bass2",
        "Slap Bass1", "Slap Bass2", "Fretless 1", "Fretless 2",
        "Flute 1",    "Flute 2",    "Piccolo 1",  "Piccolo 2",
        "Recorder",   "Pan Pipes",  "Sax 1",      "Sax 2",
        "Sax 3",      "Sax 4",      "Clarinet 1", "Clarinet 2",
        "Oboe",       "Engl Horn",  "Bassoon",    "Harmonica",
        "Trumpet 1",  "Trumpet 2",  "Trombone 1", "Trombone 2",
        "Fr Horn 1",  "Fr Horn 2",  "Tuba",       "Brs Sect 1",
        "Brs Sect 2", "Vibe 1",     "Vibe 2",     "Syn Mallet",
        "Wind Bell",  "Glock",      "Tube Bell",  "Xylophone",
        "Marimba",    "Koto",       "Sho",        "Shakuhachi",
        "Whistle 1",  "Whistle 2",  "Bottleblow", "Breathpipe",
        "Timpani",    "MelodicTom", "Deep Snare", "Elec Perc1",
        "Elec Perc2", "Taiko",      "Taiko Rim",  "Cymbal",
        "Castanets",  "Triangle",   "Orche Hit",  "Telephone",
        "Bird Tweet", "OneNoteJam", "WaterBells", "JungleTune",
    ];
}
