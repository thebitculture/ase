using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static SDL2.SDL;

namespace ASE
{
    /// <summary>
    /// Provides methods and properties for managing the configuration of the Atari System Emulator, including loading
    /// and saving configuration settings.
    /// </summary>
    /// <remarks>The Config class handles the initialization and management of emulator settings, supporting
    /// both default and user-specified configurations. It processes command-line arguments to customize emulator
    /// behavior and manages configuration persistence through JSON files. If a configuration file does not exist, a
    /// default configuration is created automatically. This class is intended to be used as the central point for
    /// accessing and modifying emulator configuration options.</remarks>
    public class Config
    {
        /// <summary>
        /// Represents the configuration options for the application, including paths, hardware flags, and debug
        /// settings.
        /// </summary>
        /// <remarks>This class holds the active configuration instance and provides properties to
        /// customize various settings such as mouse sensitivity and sample rate. The 'RunninConfig' static member
        /// serves as the default configuration accessible throughout the application.</remarks>
        public class ConfigOptions
        {
            public enum STModels
            {
                ST = 0,
                Mega = 1,
                STE = 2
            }

            public enum RAMConfigurations
            {
                RAM_512KB = 0,
                RAM_1MB = 1,
                RAM_2MB = 2,
                RAM_4MB = 3,
            }

            /// <summary>What a gamepad button does: a joystick direction or the fire button, an ST
            /// key, or the time warp — going back 5 or 10 seconds, or opening its window. Stored as
            /// its number, so new actions go at the end.</summary>
            public enum GamepadButtonsMapping
            {
                None, Fire, Space, Up, Y, N, T, Back5Seconds, Back10Seconds, OpenTimeWarp
            }

            /// <summary>
            /// What is plugged into one of the IKBD's two ports: nothing, the host mouse, or a
            /// joystick driven by the keyboard joystick, the gamepad or both. The order is the
            /// one of the Input tab's combos. Only port 0 can take the mouse: the IKBD decodes
            /// a mouse there and nowhere else, so on port 1 it reads as an empty port.
            /// </summary>
            public enum PortDevices
            {
                Disconnected, Mouse, KeysAndGamepad, KeysOnly, GamepadOnly
            }

            public enum MIDIEmulationOptions
            {
                None, System, BuiltInMT32
            }

            /// <summary>
            /// Image-space anti-aliasing applied as the last pass of the video chain. Only the
            /// post-process kinds make sense here: the ST draws its 3D games into the framebuffer
            /// itself, so there is no geometry to multisample (MSAA), no sub-pixel jitter to
            /// accumulate (TAA) and no higher-resolution scene to downsample (SSAA).
            /// </summary>
            public enum AntiAliasingModes
            {
                None = 0,   // off
                FXAA = 1,   // NVIDIA FXAA 3.11: one pass, soft, cheapest
                SMAA = 2    // SMAA 1x (Jimenez et al.): three passes, sharper edge reconstruction
            }

            /// <summary>What the machine's video output is plugged into: the RGB monitor (the
            /// picture as the ST drew it), or a television through a composite lead or the
            /// aerial socket, whose single-wire signal smears the colour and adds the artefacts
            /// of the day. Emulated as the first pass of the video chain.</summary>
            public enum VideoSignals
            {
                RGB = 0,
                Composite = 1,
                RF = 2
            }

            /// <summary>The 2x edge-smoothing filter (formerly the "smooth borders" switch):
            /// SuperEagle (the 2xSaI family) or xBR (Hyllian's, better on curves).</summary>
            public enum EdgeSmoothings
            {
                None = 0,
                SuperEagle = 1,
                XBR = 2
            }

            /// <summary>How the picture is scaled to the window: bilinear (soft), sharp-bilinear
            /// (crisp pixels at any scale), or sharp at a whole number of pixels per ST line.</summary>
            public enum ScalingModes
            {
                Smooth = 0,
                Sharp = 1,
                Integer = 2
            }

            /// <summary>Geometry of the CRT shader's phosphor mask.</summary>
            public enum MaskTypes
            {
                Aperture = 0,
                Slot = 1,
                Shadow = 2
            }
            
            /// <summary>
            /// Console debug verbosity. Each level is a superset of the previous one.
            /// </summary>
            public enum DebugModes
            {
                None = 0,           // No debug output (equivalent to the old 'false')
                Quiet = 1,          // Only initialization messages and important warnings (TOS load, ROM write attempts, ...)
                Information = 2,    // Adds operational detail (e.g. commands reaching the ACIA)
                Full = 3            // Everything, including low-level data traffic (every ACIA byte, joystick packets, ...)
            }

            /// <summary>
            /// Holds the active configuration
            /// </summary>
            public static ConfigOptions RunninConfig = new ConfigOptions();

            public string TOSPath { get; set; } = "emutos.rom";

            // Hardware flags
            public STModels STModel { get; set; } = STModels.STE;
            public RAMConfigurations RAMConfiguration { get; set; } = RAMConfigurations.RAM_2MB;
            public  bool MaxSpeed { get; set; } = false;
            public string FloppyImagePath { get; set; } = "";

            /// <summary>
            /// Whether the external drive B: is plugged into the floppy port. Off by default, as
            /// most STs shipped: with it off the select line reaches nothing and the FDC answers
            /// every B: access as "no drive" (see WD1772.DriveSelected), which is what TOS' boot
            /// probe reads to decide the machine has a single floppy. Toggled live from
            /// File > Connect drive B:, and saved here so the machine comes back as it was left.
            /// </summary>
            public bool DriveBEnabled { get; set; } = false;

            /// <summary>
            /// Whether an ST or STE carries the Mega's RP5C15 clock (the Mega always does; see
            /// Rp5c15). Off by default, as those machines shipped: their TOS 1.0x and 1.6x never
            /// read the IKBD's clock at boot and start on the ROM's date, and EmuTOS names an ST
            /// with the clock a Mega ST. Applies at power-on (--rtc).
            /// </summary>
            public bool RealTimeClock { get; set; } = false;

            /// <summary>Disk image to put in drive B: at startup (--floppy-b); implies the drive
            /// is connected. Like <see cref="FloppyImagePath"/> it is a starting condition, not a
            /// record of what the user later inserted by hand.</summary>
            public string FloppyBImagePath { get; set; } = "";
            /// <summary>
            /// Divisor applied to the host mouse movement before it reaches the ST
            /// (<c>dx = accumulated / MouseSensitivity</c>, see ACIA.cs), so a bigger number
            /// means a *slower* pointer. Stored and taken from --mouse-sensitivity in this
            /// form; the Configuration window shows <see cref="MousePointerSpeed"/> instead.
            /// </summary>
            public float MouseSensitivity { get; set; } = 2;

            /// <summary>
            /// The same setting the other way round: how fast the ST pointer moves, relative
            /// to the default (x1.0 == the default divisor of 2). This is what the slider in
            /// the Configuration window edits — a divisor is an implementation detail, and a
            /// control where a higher number means slower reads backwards to everyone.
            /// Not serialized: <see cref="MouseSensitivity"/> is the stored form.
            /// </summary>
            [JsonIgnore]
            public float MousePointerSpeed
            {
                get => MouseSensitivity >= 0.1f ? DefaultMouseSensitivity / MouseSensitivity : 1f;
                set => MouseSensitivity = value >= 0.1f ? DefaultMouseSensitivity / value : 1f;
            }

            /// <summary>Divisor that <see cref="MousePointerSpeed"/> calls x1.0.</summary>
            const float DefaultMouseSensitivity = 2f;

            public int SampleRate { get; set; } = 44100;

            // Hard disk emulation. Both can be enabled at the same time: the ACSI image is a
            // real bus device (bootable, driver lives inside the image), while the GEMDOS drive
            // mounts a host folder at file-system level and takes the next free drive letter
            // after the image's partitions. Changes apply on the next power-on (reset).
            public bool GemdosDriveEnabled { get; set; } = false;
            public string GemdosDrivePath { get; set; } = "";
            public bool AcsiImageEnabled { get; set; } = false;
            public string AcsiImagePath { get; set; } = "";

            /// <summary>
            /// Whether the machine may boot from the ACSI hard disk image. When off, the boot
            /// sector's $1234 checksum is invalidated in the data handed to the DMA (the image
            /// itself is untouched), so TOS skips the DMA boot but the disk stays fully usable
            /// by a driver loaded from floppy. Only the ACSI image is bootable; the GEMDOS
            /// drive is not.
            /// </summary>
            public bool BootFromHardDisk { get; set; } = true;

            // Granularity (in CPU cycles) at which the CPU is interleaved with the MFP timers and
            // the interrupt controller. Lower values deliver timer interrupts (e.g. the 200 Hz
            // Timer C) and update the timer data registers more promptly, at a slight throughput
            // cost; higher values are faster but coarser. Clamped to [1, 512] where it is used.
            // 4 = Maximun compatibility; 16 = Balanced; 64 = Faster but less precise.
            public int CpuSyncSliceCycles { get; set; } = 4;

            // Screen flags

            public bool ShowBorders { get; set; } = true;   // show the screen borders (overscan) around the 320x200 display

            // Monochrome (SM124) monitor instead of a colour one. Detected by TOS through MFP
            // GPIP bit 7 at boot, which then selects high resolution (640x400, 1 plane, ~71 Hz).
            // Changing it requires a machine reset (the whole video geometry differs).
            public bool MonochromeMonitor { get; set; } = false;

            // Full screen (Alt+Enter, or Emulation > Toggle full screen). Remembered here rather
            // than in windows.json: that file holds the geometry of a normal window, and a full
            // screen one has none — see MainWindow.SetFullScreen.
            public bool FullScreen { get; set; } = false;

            public bool CheckForUpdates { get; set; } = true;   // query GitHub for a newer release at startup

            // Default directories. Screenshots (Shift+F11) and snapshots (F11) default to
            // subfolders next to config.json; an empty value falls back to that default.
            // DiskImagesPath and TOSRomsPath preset the corresponding file dialogs (empty = none).
            public string ScreenshotsPath { get; set; } = Path.Combine(GetAppDefaultConfigsFilePath(), "Screenshots");
            public string SnapshotsPath { get; set; } = Path.Combine(GetAppDefaultConfigsFilePath(), "Snapshots");
            public string DiskImagesPath { get; set; } = "";
            public string LibraryPath { get; set; } = "";
            public string TOSRomsPath { get; set; } = "";

            // The ST MMU shares RAM between the CPU and the video shifter in a 2-cycle round-robin,
            // forcing every CPU bus access onto a 4-cycle grid: a misaligned access waits 2 cycles
            // for its slot. ROM is exempt (no wait states); the MFP/ACIA add fixed extra waits.
            // Moira (with MOIRA_PRECISE_TIMING) places each bus access at its exact in-instruction
            // cycle, so we can reproduce these waits. This is what keeps free-running cycle-counted
            // raster code (Spectrum 512, fullscreen demos) locked to the video instead of drifting.
            // I've tried different combinations by reproducing these waits in the emulator, leaving
            // only the most accurate timing in Moira, and I've gotten mixed results. I couldn't say
            // which combination works best.
            public bool CycleExactBus { get; set; } = true;

            // Phase of the 4-cycle MMU bus grid the CPU aligns to (0..3, effectively 0 or 2 since
            // the 68000 clock is even). Calibrated so cycle-counted rasters stay vertically stable.
            public int BusPhase { get; set; } = 0;

            // Fixed extra wait cycles for the MFP, added on top of bus alignment (~4 cycles is
            // the accepted approximation). The ACIAs are not configurable: they are 6800-type
            // (VPA) peripherals, so each access synchronizes with the E clock (CPU/10) — a
            // variable, self-stabilising wait modelled directly in Memory.ApplyBusWait.
            public int MfpWaitCycles { get; set; } = 4;

            // Bypasses the CRT shader entirely (a plain blit is used instead) rather than just
            // zeroing the sliders: with the effects at 0 the GPU still runs the whole fragment
            // program — the five bloom taps, the noise hash, the two gamma powers — for every
            // pixel. Meant for weak GPUs (Raspberry Pi and the like). The slider values are kept,
            // so switching it back off restores the previous look.
            public bool DisableCrtEffects { get; set; } = false;

            // Turns dithering patterns into the intermediate colour they stood for. The ST has
            // 512 colours (4096 on an STE) and 16 on screen, so gradients and polygon shading are
            // drawn as two-colour patterns that a CRT's limited bandwidth blurred into a single
            // tone; on a sharp LCD they stay as visible chequerboards. Two post-render passes
            // detect those patterns and replace them with a real RGB average, and then merge the
            // bands of a dithered gradient into a continuous ramp — see the dithering and
            // gradient shaders in GLControl. Independent of DisableCrtEffects: it is image
            // correction, not a CRT effect, so it also applies to the plain blit and to high
            // resolution. Off by default, and deliberately: it costs far more GPU than everything
            // else the emulator draws, which is more than a Raspberry Pi or a similar
            // single-board machine has to spare.
            public bool ColorizeDithering { get; set; } = false;

            // Edge smoothing: a 2x pass over the emulator's framebuffer that reads the shape of
            // every edge from its neighbourhood and rebuilds it with intermediate tones, so a
            // diagonal or a curve stops being a staircase — SuperEagle (Kreed's 2xSaI family, the
            // filter MAME and the libretro front-ends ship) or xBR (Hyllian's, better on curves).
            // Independent of DisableCrtEffects for the same reason as ColorizeDithering — it
            // corrects the picture rather than dressing it up — and it runs *after* the
            // colorization chain, which needs the raw pattern of texels to detect anything. Off
            // by default: the pass renders four times as many texels as everything before it.
            [JsonConverter(typeof(EnumNameJsonConverter<EdgeSmoothings>))]
            public EdgeSmoothings EdgeSmoothing { get; set; } = EdgeSmoothings.None;

            // The video signal (see VideoSignals): RGB by default, i.e. the picture as drawn.
            // A composite or RF signal is emulated as the first pass of the chain and bypasses
            // the two pattern-based corrections above, which it leaves nothing to detect.
            [JsonConverter(typeof(EnumNameJsonConverter<VideoSignals>))]
            public VideoSignals VideoSignal { get; set; } = VideoSignals.RGB;

            // Scaling to the window (see ScalingModes). Smooth is the bilinear filter the
            // picture always had; Sharp keeps every pixel crisp whatever the scale; Integer adds
            // a letterbox so each ST line is a whole number of screen pixels.
            [JsonConverter(typeof(EnumNameJsonConverter<ScalingModes>))]
            public ScalingModes Scaling { get; set; } = ScalingModes.Smooth;

            // Phosphor persistence, percent (0-50): how much of the previous frame is blended
            // into each frame. 50 is a straight average of the two, which is what shows the
            // ST's 50 Hz flicker tricks (two-palette pictures, alternate-frame sprites) as the
            // steady mix a CRT made of them; anything above 0 leaves a trail behind motion.
            public int Persistence { get; set; } = 0;

            // Geometry of the CRT shader's phosphor mask (see MaskTypes). Shadow is the look the
            // Mask slider always had.
            [JsonConverter(typeof(EnumNameJsonConverter<MaskTypes>))]
            public MaskTypes MaskType { get; set; } = MaskTypes.Shadow;

            // Anti-aliasing for the staircase edges of lines and polygons — what the 3D games
            // draw — as a post-process over the picture, FXAA or SMAA (see AntiAliasingModes).
            // The third correction next to ColorizeDithering and EdgeSmoothing, independent of
            // both and of DisableCrtEffects for the same reason, and applied after them: the two
            // detect their patterns by comparing texels for equality, which a filter that blends
            // colours along every edge would defeat. Off by default like its neighbours: it
            // softens what it touches, and a pixel-art title may be better without it.
            [JsonConverter(typeof(EnumNameJsonConverter<AntiAliasingModes>))]
            public AntiAliasingModes AntiAliasing { get; set; } = AntiAliasingModes.None;

            // The two knobs of those filters, for the config file only (no control in the
            // window: they are for the experienced user, and the defaults suit the ST's
            // pictures). FxaaSubpix is FXAA's sub-pixel low-pass, 0..1 — the term that also
            // softens text and dithering, which is why the default sits under the 0.75 of the
            // original; 0 leaves only the edge reconstruction. SmaaThreshold is the contrast
            // an edge needs to be one, 0.01..0.5 — 0.1 is the reference's HIGH preset, lower
            // catches the subtler edges colorize dithering produces, higher leaves more alone.
            // Both are clamped on the way to the GPU (GLControl), so a typo cannot break the
            // picture, and read per pass, so an edit to the running config applies at once.
            public float FxaaSubpix { get; set; } = 0.5f;
            public float SmaaThreshold { get; set; } = 0.1f;

            // The video signal's knobs, config file only like the two above. Each signal has
            // three, and they are the whole of what tells composite from RF — the two share the
            // model and differ only in how far each is turned up. Chroma radius: the half-width,
            // in texels, of the window the colour is decoded under, 2..8; wider smears the colour
            // further sideways. Luma softness, 0..1: the width of the gaussian the brightness is
            // filtered with, a quarter texel at 0 (as sharp as the wire carries: a low-res pixel
            // comes through whole) to a texel and a half at 1 (the luma of a poor tuner).
            // Artefacts, 0..1: how much of the crosstalk between the two the
            // decoder lets through — the rainbow shimmer on dithering, the crawling dots, the
            // coloured fringes on white text. At 0 the picture keeps a television's bandwidth and
            // none of its mistakes; it is the dial to turn down when the effect is too much, and
            // the only one that leaves the sharpness alone.
            public float CompositeChromaRadius { get; set; } = 4f;
            public float CompositeLumaSoftness { get; set; } = 0.15f;
            public float CompositeArtefacts { get; set; } = 0.25f;
            public float RfChromaRadius { get; set; } = 8f;
            public float RfLumaSoftness { get; set; } = 0.5f;
            public float RfArtefacts { get; set; } = 0.7f;

            public float Curvature { get; set; } = 0.01f;
            public float Vignette { get; set; } = 0.18f;
            public float Scanline { get; set; } = 1.0f;
            public float ChromAb { get; set; } = 0.25f;
            public float Bloom { get; set; } = 0.22f;
            public float Mask { get; set; } = 0.50f;
            public float Noise { get; set; } = 0.05f;

            // What is plugged into the IKBD's ports (see PortDevices). Port 0 is the mouse port,
            // and a joystick there is joystick 0; port 1 is joystick 1. Applied live, like
            // plugging a controller into a running ST.
            [JsonConverter(typeof(EnumNameJsonConverter<PortDevices>))]
            public PortDevices Port0Device { get; set; } = PortDevices.Mouse;
            [JsonConverter(typeof(EnumNameJsonConverter<PortDevices>))]
            public PortDevices Port1Device { get; set; } = PortDevices.KeysAndGamepad;

            // Joystick emulation
            public SDL_Scancode KeyJoy1Up { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_8;
            public SDL_Scancode KeyJoy1Down { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_5;
            public SDL_Scancode KeyJoy1Left { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_4;
            public SDL_Scancode KeyJoy1Right { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_6;
            public SDL_Scancode KeyJoy1Fire { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_0;

            // Gamepad button mapping
            public GamepadButtonsMapping GamepadButtonX { get; set; } = GamepadButtonsMapping.Up;
            public GamepadButtonsMapping GamepadButtonY { get; set; } = GamepadButtonsMapping.Fire;
            public GamepadButtonsMapping GamepadButtonA { get; set; } = GamepadButtonsMapping.Fire;
            public GamepadButtonsMapping GamepadButtonB { get; set; } = GamepadButtonsMapping.Space;
            public GamepadButtonsMapping GamepadButtonLS { get; set; } = GamepadButtonsMapping.Y;
            public GamepadButtonsMapping GamepadButtonRS { get; set; } = GamepadButtonsMapping.N;
            public GamepadButtonsMapping GamepadButtonLB { get; set; } = GamepadButtonsMapping.T;
            public GamepadButtonsMapping GamepadButtonRB { get; set; } = GamepadButtonsMapping.Space;

            // MIDI

            /// <summary>
            /// What the ST's MIDI ports are connected to: nothing, the host's own MIDI devices
            /// (<see cref="MIDIEmulationOptions.System"/>, mapped through
            /// <see cref="MidiInDevice"/>/<see cref="MidiOutDevice"/>) or the built-in Roland
            /// MT-32 emulation (Munt, ROMs in <see cref="MT32Rompath"/>). Changing it takes
            /// effect on a machine reset: MT-32 titles probe and initialise the module while
            /// loading, so attaching one to a running machine would go unnoticed.
            /// </summary>
            public MIDIEmulationOptions MidiEmulation { get; set; } = MIDIEmulationOptions.None;

            /// <summary>
            /// Host MIDI ports the emulated ST is wired to in <see cref="MIDIEmulationOptions.System"/>
            /// mode, or "" for none. Stored as the *name* the operating system gives the port and
            /// not as its index: an index is only a position in the driver's list and shifts as
            /// soon as a device is plugged, unplugged or reordered, so a saved index quietly ends
            /// up addressing a different instrument. <see cref="HostMidi"/> enumerates the ports
            /// and is where the name is resolved back to a platform handle.
            /// </summary>
            public string MidiInDevice { get; set; } = "";
            /// <inheritdoc cref="MidiInDevice"/>
            public string MidiOutDevice { get; set; } = "";

            /// <summary>
            /// Directory holding the Roland MT-32 control and PCM ROM images, used by the built-in
            /// emulation. The file names do not matter: libmt32emu identifies every image by its
            /// SHA-1 (see Mt32Synth.LoadRoms). The ROMs are copyrighted and not shipped with ASE.
            /// </summary>
            public string MT32Rompath { get; set; } = "";

            /// <summary>
            /// Output level of the built-in MT-32 in the mix, in percent — the real
            /// module's front-panel volume knob. 100 folds Munt's line level 1:1 over the
            /// PSG's, which turns out noticeably quieter than the ST's own sound, so the
            /// default boosts it; 0 mutes, 400 is the ceiling. Read live by the audio
            /// mixer (Mt32Backend.MixInto), so the slider works without a reset.
            /// </summary>
            public int Mt32Volume { get; set; } = 200;

            /// <summary>
            /// Whether the time warp keeps moments (the switch in Emulation ▸ Time warp…). Null
            /// until the user chooses: on, except on a Raspberry Pi (<see cref="Config.HostIsRaspberryPi"/>),
            /// where a copy of the memory and its compression every second are a real share of a
            /// small board's processor and RAM.
            /// </summary>
            public bool? TimeWarpEnabled { get; set; }

            /// <summary>
            /// How far back the time warp reaches, in seconds (Emulation ▸ Time warp…,
            /// Ctrl+Shift+F10): one moment of the machine is kept per second, compressed, for this
            /// long. 0 keeps none, like switching it off.
            /// </summary>
            public int TimeWarpSeconds { get; set; } = 60;

            // Online play (NETPLAY.md). Only what belongs to online play itself is kept here:
            // the machine a session runs is the host's and never touches the guest's settings.

            /// <summary>The name other players see when this one hosts or joins a game.</summary>
            public string NetplayPlayerName { get; set; } = "";

            /// <summary>
            /// UDP port a hosted game listens on, and the one a search on the local network asks
            /// (NETPLAY.md §7.2). Fixed rather than random so it can be forwarded in a router for
            /// players outside the local network; a guest joining over the Internet takes it too
            /// when it is free, and one on the local network an ephemeral port.
            /// </summary>
            public int NetplayPort { get; set; } = Netplay.NetTransport.DefaultPort;

            /// <summary>
            /// The website that passes invitation links between two ASEs (NETPLAY.md §8), contacted
            /// only when a link is made or used. Empty: no links, only the manual codes.
            /// </summary>
            public string NetplayWebUrl { get; set; } = "https://thebitculture.com";

            /// <summary>Whether invitation links open ASE (NETPLAY.md §8.4): not asked yet, yes (the
            /// "ase:" link type is on this system), or no. Never added without the user saying so
            /// in the Online play window.</summary>
            public enum LinkChoices { Ask = 0, Yes = 1, No = 2 }

            [JsonConverter(typeof(EnumNameJsonConverter<LinkChoices>))]
            public LinkChoices NetplayOpenLinks { get; set; } = LinkChoices.Ask;

            /// <summary>The command the ase: link type was last pointed at; when this ASE starts
            /// from somewhere else, the type is pointed here again.</summary>
            public string NetplayLinksLauncher { get; set; } = "";

            /// <summary>The address last typed into the Join box, offered again next time.</summary>
            public string NetplayLastAddress { get; set; } = "";

            // Screenscraper
            public string ScreenScraperUser { get; set; }
            /// <summary>Protected value; use ScreenScraperPasswordRaw to read it.</summary>
            public string ScreenScraperPassword { get; set; }
            [JsonIgnore]
            public string ScreenScraperPasswordRaw
            {
                get => StringOfuscator.Unprotect(ScreenScraperPassword) ?? string.Empty;
                set => ScreenScraperPassword = StringOfuscator.Protect(value);
            }
            public bool ScrapeMedia { get; set; } = true;

            /// <summary>Windows only: custom VLC installation directory (containing libvlc.dll)
            /// used to play game preview videos in the library without bundling libVLC with the
            /// emulator. Empty = auto-detect the default "Program Files\VideoLAN\VLC" install.</summary>
            public string VlcInstallPath { get; set; } = "";

            // Debug flags
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool DiskDump { get; set; } = false;  // Not exposed, only for my testing
            [JsonConverter(typeof(DebugModeJsonConverter))]
            public DebugModes DebugMode { get; set; } = DebugModes.None;
        }

        /// <summary>
        /// Serializes <see cref="ConfigOptions.DebugModes"/> as a readable string and, on read, also
        /// accepts the legacy boolean form (<c>true</c> -> <see cref="ConfigOptions.DebugModes.Full"/>,
        /// <c>false</c> -> <see cref="ConfigOptions.DebugModes.None"/>) so existing config files keep working.
        /// </summary>
        public class DebugModeJsonConverter : JsonConverter<ConfigOptions.DebugModes>
        {
            public override ConfigOptions.DebugModes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.True:
                        return ConfigOptions.DebugModes.Full;
                    case JsonTokenType.False:
                        return ConfigOptions.DebugModes.None;
                    case JsonTokenType.Number:
                        int n = reader.GetInt32();
                        return Enum.IsDefined(typeof(ConfigOptions.DebugModes), n) ? (ConfigOptions.DebugModes)n : ConfigOptions.DebugModes.None;
                    case JsonTokenType.String:
                        return (Enum.TryParse(reader.GetString(), true, out ConfigOptions.DebugModes v) && Enum.IsDefined(typeof(ConfigOptions.DebugModes), v)) ? v : ConfigOptions.DebugModes.None;
                    default:
                        return ConfigOptions.DebugModes.None;
                }
            }

            public override void Write(Utf8JsonWriter writer, ConfigOptions.DebugModes value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        /// <summary>
        /// Serializes an enum option as its name ("FXAA", "Composite"…), read back
        /// case-insensitively, and also accepts the number for a config edited by hand. Anything
        /// unrecognised reads as the enum's default (0) rather than failing the whole file.
        /// </summary>
        public class EnumNameJsonConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Number:
                        int n = reader.GetInt32();
                        return Enum.IsDefined(typeof(T), n) ? (T)Enum.ToObject(typeof(T), n) : default;
                    case JsonTokenType.String:
                        return TryParseOption(reader.GetString(), out T v) ? v : default;
                    default:
                        return default;
                }
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        /// <summary>
        /// Reads an enum option from the command line or the config file: the enum's names,
        /// case-insensitively, plus the friendlier spellings in <paramref name="aliases"/>.
        /// </summary>
        public static bool TryParseOption<T>(string value, out T mode, params (string alias, T value)[] aliases) where T : struct, Enum
        {
            string v = (value ?? "").Trim();
            foreach (var (alias, target) in aliases)
                if (string.Equals(alias, v, StringComparison.OrdinalIgnoreCase)) { mode = target; return true; }
            if (Enum.TryParse(v, true, out mode) && Enum.IsDefined(typeof(T), mode))
                return true;
            mode = default;
            return false;
        }

        public static string Version = "";

        // Snapshot to restore on the first power-on (--snapshot=<path>). Launch-only
        // argument: it is never persisted to the config file.
        public static string StartupSnapshot = "";

        // Input recording from the command line (--record, --record-frames=N, --replay=<path>,
        // --exit-when-done). Launch-only, never persisted: see Netplay.InputRecording.
        public static bool StartupRecord;
        public static long StartupRecordFrames;
        public static string StartupReplay = "";

        /// <summary>An online play invitation link to open when ASE starts (--join=, or the bare
        /// ase: link the system passes); null for none.</summary>
        public static string StartupJoinLink;
        public static bool ExitWhenSessionEnds;

        const string AppName = "ASE";
        const string DefaultConfigFileName = "config.json";

        string AppDataConfigPath;
        string PathToDefaultConfig;


        public void LoadConfig(string[] args)
        {
            AppDataConfigPath = GetAppDefaultConfigsFilePath();
            PathToDefaultConfig = Path.Combine(AppDataConfigPath, DefaultConfigFileName);

            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            Version = Regex.Replace(Version, @"^(\d+\.\d+).*", "$1");

            ColoredConsole.WriteLine($"{Environment.NewLine}[[white]]ATARI SYSTEM EMULATOR[[/white]] v{Version} - The Bit Culture {DateTime.Now.Year}");
            ColoredConsole.WriteLine("👉 [[magenta]]https://github.com/thebitculture/ase[[/magenta]]");
            ColoredConsole.WriteLine("👉 [[magenta]]https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn[[/magenta]]" + Environment.NewLine);

            if (File.Exists(PathToDefaultConfig))
                LoadJsonConfig(PathToDefaultConfig);
            else
                DumpJsonConfig(PathToDefaultConfig);  // Creates default configuration

            foreach (string arg in args)
            {
                string[] parts = arg.Split('=');

                switch (parts[0].ToLower())
                {
                    case "--tos":
                        if(parts.Length > 1)
                            ConfigOptions.RunninConfig.TOSPath = parts[1];
                        break;
                    case "--debug":
                        if (parts.Length > 1)
                        {
                            if (Enum.TryParse(parts[1], true, out ConfigOptions.DebugModes lvl)
                                && Enum.IsDefined(typeof(ConfigOptions.DebugModes), lvl))
                            {
                                ConfigOptions.RunninConfig.DebugMode = lvl;
                            }
                            else
                            {
                                ColoredConsole.WriteLine($"Invalid debug level [[red]]{parts[1]}[[/red]]. Use none|quiet|information|full.");
                                ColoredConsole.WriteLine("Defaulting to [[cyan]]Quiet[[/cyan]].");
                                ConfigOptions.RunninConfig.DebugMode = ConfigOptions.DebugModes.Full;
                            }
                        }
                        else
                        {
                            // Bare '--debug' enables full verbosity (most useful default for debugging).
                            ConfigOptions.RunninConfig.DebugMode = ConfigOptions.DebugModes.Full;
                        }
                        break;
                    case "--maxspeed":
                        if (parts.Length > 1 && bool.TryParse(parts[1], out bool _maxs))
                            ConfigOptions.RunninConfig.MaxSpeed = _maxs;
                        break;
                    case "--profile":
                        {
                            // Default: one line per second of emulated time (50 Hz PAL).
                            int every = 50;

                            if (parts.Length > 1 && (!int.TryParse(parts[1], out every) || every <= 0))
                            {
                                ColoredConsole.WriteLine("Invalid profile interval. Use [[cyan]]--profile=N[[/cyan]], with N = frames between report lines.");
                                every = 50;
                            }

                            FrameProfiler.Configure(every);
                        }
                        break;
                    case "--console":
                        // Consumed by ConsoleHost before this parser exists (the emulator is a
                        // windowed program on Windows and has to find somewhere to log *first*).
                        // It is listed here so it does not read as an unknown option, and in the
                        // help below so it can be found at all.
                        break;
                    case "--floppy":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.FloppyImagePath = parts[1];
                        break;
                    case "--floppy-b":
                        if (parts.Length > 1)
                        {
                            ConfigOptions.RunninConfig.FloppyBImagePath = parts[1];
                            ConfigOptions.RunninConfig.DriveBEnabled = true;
                        }
                        break;
                    case "--drive-b":
                        ConfigOptions.RunninConfig.DriveBEnabled =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _drvb) || _drvb;
                        break;
                    case "--rtc":
                        ConfigOptions.RunninConfig.RealTimeClock =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _rtc) || _rtc;
                        break;
                    case "--acsi":
                        if (parts.Length > 1)
                        {
                            ConfigOptions.RunninConfig.AcsiImagePath = parts[1];
                            ConfigOptions.RunninConfig.AcsiImageEnabled = true;
                        }
                        break;
                    case "--gemdos-dir":
                        if (parts.Length > 1)
                        {
                            ConfigOptions.RunninConfig.GemdosDrivePath = parts[1];
                            ConfigOptions.RunninConfig.GemdosDriveEnabled = true;
                        }
                        break;
                    case "--boot-hd":
                        ConfigOptions.RunninConfig.BootFromHardDisk =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _bhd) || _bhd;
                        break;
                    case "--mouse-sensitivity":
                        if (parts.Length > 1 && Regex.IsMatch(parts[1], @"^\d+"))
                        {
                            if (parts.Length > 1 && float.TryParse(parts[1], out float xSens))
                                ConfigOptions.RunninConfig.MouseSensitivity = xSens;
                        }
                        else
                        {
                            ColoredConsole.WriteLine("Invalid mouse sensitivity format. Use --mouse-sensitivity=N");
                            ColoredConsole.WriteLine("Example: --mouse-sensitivity=2.5");
                            ColoredConsole.WriteLine($"Using default sensitivity [[cyan]]{ConfigOptions.RunninConfig.MouseSensitivity}[[/cyan]].");
                        }
                        break;
                    case "--port0":
                    case "--port1":
                        {
                            bool port1 = parts[0].Equals("--port1", StringComparison.OrdinalIgnoreCase);
                            if (parts.Length > 1 && TryParseOption(parts[1], out ConfigOptions.PortDevices _pd,
                                    ("none", ConfigOptions.PortDevices.Disconnected), ("off", ConfigOptions.PortDevices.Disconnected),
                                    ("joystick", ConfigOptions.PortDevices.KeysAndGamepad),
                                    ("keys", ConfigOptions.PortDevices.KeysOnly), ("keyboard", ConfigOptions.PortDevices.KeysOnly),
                                    ("gamepad", ConfigOptions.PortDevices.GamepadOnly), ("pad", ConfigOptions.PortDevices.GamepadOnly))
                                && !(port1 && _pd == ConfigOptions.PortDevices.Mouse))
                            {
                                if (port1) ConfigOptions.RunninConfig.Port1Device = _pd;
                                else ConfigOptions.RunninConfig.Port0Device = _pd;
                            }
                            else
                            {
                                ColoredConsole.WriteLine($"Invalid device [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]] for {parts[0]}. " +
                                    (port1 ? "Use joystick|keys|gamepad|disconnected (the mouse only works on port 0)."
                                           : "Use mouse|joystick|keys|gamepad|disconnected."));
                            }
                        }
                        break;
                    case "--cycleexact":
                        ConfigOptions.RunninConfig.CycleExactBus =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _ce) || _ce;
                        break;
                    case "--busphase":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _bp))
                            ConfigOptions.RunninConfig.BusPhase = _bp;
                        break;
                    case "--mfpwait":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _mw))
                            ConfigOptions.RunninConfig.MfpWaitCycles = _mw;
                        break;
                    case "--altconfig":
                        if (parts.Length > 1)
                        {
                            ColoredConsole.WriteLine($"Config override [[cyan]]{parts[1]}[[/cyan]]!");
                            LoadJsonConfig(parts[1]);
                        }
                        break;
                    case "--snapshot":
                        if (parts.Length > 1)
                            StartupSnapshot = parts[1];
                        break;
                    case "--snapshots-dir":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.SnapshotsPath = parts[1];
                        break;
                    case "--record":
                        StartupRecord = true;
                        break;
                    case "--record-frames":
                        if (parts.Length > 1 && long.TryParse(parts[1], out long _rf) && _rf > 0)
                        {
                            StartupRecordFrames = _rf;
                            StartupRecord = true;
                        }
                        else
                        {
                            ColoredConsole.WriteLine("Invalid frame count. Use [[cyan]]--record-frames=N[[/cyan]] with N > 0 (50 frames are one second).");
                        }
                        break;
                    case "--replay":
                        if (parts.Length > 1)
                            StartupReplay = parts[1];
                        break;
                    case "--exit-when-done":
                        ExitWhenSessionEnds = true;
                        break;
                    case "--screenshots-dir":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.ScreenshotsPath = parts[1];
                        break;
                    case "--library-dir":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.LibraryPath = parts[1];
                        break;
                    case "--no-effects":
                        ConfigOptions.RunninConfig.DisableCrtEffects =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _nfx) || _nfx;
                        break;
                    case "--colorize-dither":
                        ConfigOptions.RunninConfig.ColorizeDithering =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _cdt) || _cdt;
                        break;
                    case "--edge-smoothing":
                        if (parts.Length > 1 && TryParseOption(parts[1], out ConfigOptions.EdgeSmoothings _es,
                                ("none", ConfigOptions.EdgeSmoothings.None), ("off", ConfigOptions.EdgeSmoothings.None),
                                ("eagle", ConfigOptions.EdgeSmoothings.SuperEagle), ("supereagle", ConfigOptions.EdgeSmoothings.SuperEagle),
                                ("xbr", ConfigOptions.EdgeSmoothings.XBR)))
                            ConfigOptions.RunninConfig.EdgeSmoothing = _es;
                        else
                            ColoredConsole.WriteLine($"Invalid edge smoothing [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use none|eagle|xbr.");
                        break;
                    case "--video-signal":
                        if (parts.Length > 1 && TryParseOption(parts[1], out ConfigOptions.VideoSignals _vs,
                                ("rgb", ConfigOptions.VideoSignals.RGB), ("monitor", ConfigOptions.VideoSignals.RGB),
                                ("composite", ConfigOptions.VideoSignals.Composite), ("composite", ConfigOptions.VideoSignals.Composite),
                                ("rf", ConfigOptions.VideoSignals.RF), ("aerial", ConfigOptions.VideoSignals.RF), ("antenna", ConfigOptions.VideoSignals.RF)))
                            ConfigOptions.RunninConfig.VideoSignal = _vs;
                        else
                            ColoredConsole.WriteLine($"Invalid video signal [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use rgb|composite|rf.");
                        break;
                    case "--scaling":
                        if (parts.Length > 1 && TryParseOption(parts[1], out ConfigOptions.ScalingModes _sc,
                                ("smooth", ConfigOptions.ScalingModes.Smooth), ("bilinear", ConfigOptions.ScalingModes.Smooth),
                                ("sharp", ConfigOptions.ScalingModes.Sharp), ("integer", ConfigOptions.ScalingModes.Integer), ("int", ConfigOptions.ScalingModes.Integer)))
                            ConfigOptions.RunninConfig.Scaling = _sc;
                        else
                            ColoredConsole.WriteLine($"Invalid scaling [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use smooth|sharp|integer.");
                        break;
                    case "--persistence":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _pp))
                            ConfigOptions.RunninConfig.Persistence = Math.Clamp(_pp, 0, 50);
                        else
                            ColoredConsole.WriteLine($"Invalid persistence [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use a percentage, 0-50.");
                        break;
                    case "--aa":
                    case "--anti-aliasing":
                        if (parts.Length > 1 && TryParseAntiAliasing(parts[1], out ConfigOptions.AntiAliasingModes _aa))
                        {
                            ConfigOptions.RunninConfig.AntiAliasing = _aa;
                        }
                        else
                        {
                            ColoredConsole.WriteLine($"Invalid anti-aliasing mode [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use none|fxaa|smaa.");
                            ColoredConsole.WriteLine("Keeping the configured mode [[cyan]]" + ConfigOptions.RunninConfig.AntiAliasing + "[[/cyan]].");
                        }
                        break;
                    case "--fullscreen":
                        ConfigOptions.RunninConfig.FullScreen =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _fs) || _fs;
                        break;
                    case "--mono":
                    case "--monochrome":
                        ConfigOptions.RunninConfig.MonochromeMonitor =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _mono) || _mono;
                        break;
                    case "--midi":
                        if (parts.Length > 1 && TryParseMidiMode(parts[1], out ConfigOptions.MIDIEmulationOptions _midi))
                        {
                            ConfigOptions.RunninConfig.MidiEmulation = _midi;
                        }
                        else
                        {
                            ColoredConsole.WriteLine($"Invalid MIDI mode [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use none|system|mt32.");
                            ColoredConsole.WriteLine("Keeping the configured mode [[cyan]]" + ConfigOptions.RunninConfig.MidiEmulation + "[[/cyan]].");
                        }
                        break;
                    // The port names are the ones the host OS publishes (see HostMidi); they carry
                    // spaces, so on the command line they need quoting: --midi-out="USB MIDI".
                    case "--midi-in":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.MidiInDevice = parts[1];
                        break;
                    case "--midi-out":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.MidiOutDevice = parts[1];
                        break;
                    case "--mt32-roms":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.MT32Rompath = parts[1];
                        break;
                    case "--mt32-volume":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _mtv))
                        {
                            ConfigOptions.RunninConfig.Mt32Volume = Math.Clamp(_mtv, 0, Mt32Backend.MaxVolume);
                        }
                        else
                        {
                            ColoredConsole.WriteLine($"Invalid MT-32 volume. Use [[cyan]]--mt32-volume=N[[/cyan]] with N in percent (0-{Mt32Backend.MaxVolume}).");
                            ColoredConsole.WriteLine($"Keeping the configured volume [[cyan]]{ConfigOptions.RunninConfig.Mt32Volume}%[[/cyan]].");
                        }
                        break;

                    case "--player-name":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.NetplayPlayerName = parts[1].Trim();
                        break;
                    case "--join":
                        if (parts.Length > 1)
                            StartupJoinLink = arg[(arg.IndexOf('=') + 1)..];
                        break;
                    case "--netplay-web":
                        ConfigOptions.RunninConfig.NetplayWebUrl = parts.Length > 1 ? parts[1].Trim() : "";
                        break;
                    case "--netplay-port":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _np) && _np is > 0 and < 65536)
                        {
                            ConfigOptions.RunninConfig.NetplayPort = _np;
                        }
                        else
                        {
                            ColoredConsole.WriteLine("Invalid port. Use [[cyan]]--netplay-port=N[[/cyan]] with N between 1 and 65535.");
                            ColoredConsole.WriteLine($"Keeping the configured port [[cyan]]{ConfigOptions.RunninConfig.NetplayPort}[[/cyan]].");
                        }
                        break;

                    default:
                        // A bare path instead of an option: that is what the shell appends when a
                        // file is opened with ASE (the installer registers the .st/.msa/.stx/.snap
                        // associations) and what a file dropped on the executable arrives as. Note
                        // it takes `arg`, not `parts[0]`: the split on '=' above would cut a path
                        // that contains one.
                        // An ase: invitation link, as the system passes it when one is clicked
                        if (arg.StartsWith(Netplay.LinkScheme.Scheme + ":", StringComparison.OrdinalIgnoreCase))
                        {
                            StartupJoinLink = arg;
                            break;
                        }

                        if (!parts[0].StartsWith("-"))
                        {
                            OpenPathArgument(arg);
                            break;
                        }

                        // Anything unrecognized lands here as well, so name it before the list —
                        // otherwise a typo just looks like the emulator refusing to start.
                        if (parts[0].ToLower() is not ("--help" or "-h"))
                            ColoredConsole.WriteLine($"Unknown option [[red]]{parts[0]}[[/red]].{Environment.NewLine}");

                        PrintUsage();
                        Environment.Exit(0);
                        break;
                }
            }
        }

        /// <summary>
        /// Takes an argument that is a plain file path — a file opened with ASE from Explorer, or
        /// dropped on the executable — and puts it where its extension says: a disk image into
        /// drive A, a snapshot into the machine. An extension we do not know is reported and
        /// otherwise ignored, deliberately: unlike a mistyped option, this arrives from a double
        /// click, and a windowed program that exits without a word (the usage text goes to a
        /// console that is not there) would look like a crash. Starting with an empty drive is
        /// something the user can see and act on.
        /// </summary>
        static void OpenPathArgument(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                // Same set the File menu opens, .zip included (the image is inside it).
                case ".st":
                case ".msa":
                case ".stx":
                case ".zip":
                    ConfigOptions.RunninConfig.FloppyImagePath = path;
                    break;

                case ".snap":
                    StartupSnapshot = path;
                    break;

                case ".asrec":
                    StartupReplay = path;
                    break;

                default:
                    ColoredConsole.WriteLine($"Don't know what to do with [[red]]{path}[[/red]]: expected a disk image (.st, .msa, .stx, .zip), a snapshot (.snap) or an input recording (.asrec).");
                    break;
            }
        }

        /// <summary>
        /// Reads the value of <c>--anti-aliasing</c> (and the config file's string form): the
        /// mode names, case-insensitively, plus <c>off</c> for none.
        /// </summary>
        public static bool TryParseAntiAliasing(string value, out ConfigOptions.AntiAliasingModes mode)
        {
            switch ((value ?? "").Trim().ToLower())
            {
                case "none":
                case "off":
                    mode = ConfigOptions.AntiAliasingModes.None;
                    return true;
                case "fxaa":
                    mode = ConfigOptions.AntiAliasingModes.FXAA;
                    return true;
                case "smaa":
                    mode = ConfigOptions.AntiAliasingModes.SMAA;
                    return true;
                default:
                    mode = ConfigOptions.AntiAliasingModes.None;
                    return false;
            }
        }

        /// <summary>
        /// Reads the value of <c>--midi</c>. Accepts the friendly spellings a user would try
        /// rather than the enum names, since "BuiltInMT32" is an implementation detail.
        /// </summary>
        static bool TryParseMidiMode(string value, out ConfigOptions.MIDIEmulationOptions mode)
        {
            switch (value.ToLower())
            {
                case "none":
                case "off":
                    mode = ConfigOptions.MIDIEmulationOptions.None;
                    return true;
                case "system":
                case "host":
                case "os":
                    mode = ConfigOptions.MIDIEmulationOptions.System;
                    return true;
                case "mt32":
                case "mt-32":
                case "builtinmt32":
                    mode = ConfigOptions.MIDIEmulationOptions.BuiltInMT32;
                    return true;
                default:
                    mode = ConfigOptions.MIDIEmulationOptions.None;
                    return false;
            }
        }

        /// <summary>
        /// Prints the command-line help. Every option the switch above understands is listed here;
        /// the ones in square brackets work bare as well as with a value.
        /// </summary>
        static void PrintUsage()
        {
            // Defaults are read from a fresh ConfigOptions so the help cannot drift from the code.
            var def = new ConfigOptions();

            ColoredConsole.WriteLine("Usage: [[white]]ASE[[/white]] [options] [<file>]");
            ColoredConsole.WriteLine("       A file given on its own is opened by its extension: a disk image");
            ColoredConsole.WriteLine("       (.st/.msa/.stx/.zip) goes into drive A, a .snap is restored, an .asrec");
            ColoredConsole.WriteLine("       input recording is played back.");

            HelpSection("Machine");
            HelpOption("--tos", "=<path>", "TOS ROM image (192 KB for ST/Mega, 256 KB for STE)");
            HelpOption("--rtc", "[=true|false]", "Fit the Mega's real-time clock in an ST or STE (the Mega has it)");
            HelpOption("--altconfig", "=<path>", "Load this configuration file instead of the default one");
            HelpOption("--maxspeed", "=true|false", "Run as fast as the host allows instead of at ST speed");

            HelpSection("Media and directories");
            HelpOption("--floppy", "=<path>", "Start with a disk image (.st/.msa/.stx/.zip) in drive A");
            HelpOption("--drive-b", "[=true|false]", "Connect the external drive B: to the floppy port");
            HelpOption("--floppy-b", "=<path>", "Start with a disk image in drive B (connects the drive)");
            HelpOption("--acsi", "=<path>", "Attach an ACSI hard disk image (raw, 512-byte sectors)");
            HelpOption("--gemdos-dir", "=<path>", "Mount a host folder as a GEMDOS hard drive");
            HelpOption("--boot-hd", "[=true|false]", "Allow booting from the ACSI hard disk image");
            HelpOption("--snapshot", "=<path>", "Restore a machine snapshot (.snap) on startup");
            HelpOption("--library-dir", "=<path>", "Folder of the game library (images + Library.json)");
            HelpOption("--snapshots-dir", "=<path>", "Where F11 saves machine snapshots");
            HelpOption("--screenshots-dir", "=<path>", "Where Shift+F11 saves PNG screenshots");

            HelpSection("Input recording");
            HelpOption("--record", "", "Record the input from power-on into the snapshots directory (.asrec)");
            HelpOption("--record-frames", "=N", "Stop that recording by itself after N frames (50 = 1 s); implies --record");
            HelpOption("--replay", "=<path>", "Play back an input recording and check that it stays in sync");
            HelpOption("--exit-when-done", "", "Close ASE when that recording or playback ends (exit code 1: failed or out of sync)");

            HelpSection("Display and input");
            HelpOption("--fullscreen", "[=true|false]", "Start in full screen (Alt+Enter toggles it)");
            HelpOption("--monochrome", "[=true|false]", "Monochrome (SM124) monitor: 640x400 high resolution");
            HelpOption("--no-effects", "[=true|false]", "Bypass the CRT shader: faster on weak GPUs");
            HelpOption("--colorize-dither", "[=true|false]", "Blend dithering patterns into real colours and rebuild gradients (heavy on the GPU)");
            HelpOption("--video-signal", "=<signal>", "What the ST is plugged into: rgb (monitor), composite or rf (aerial TV)");
            HelpOption("--edge-smoothing", "=<filter>", "2x edge smoothing of diagonals and curves: none, eagle or xbr (heavy on the GPU)");
            HelpOption("--scaling", "=<mode>", "Scaling to the window: smooth, sharp or integer");
            HelpOption("--persistence", "=N", "Phosphor persistence: share of the previous frame blended in, 0-50 percent");
            HelpOption("--anti-aliasing", "=<mode>", "Anti-aliasing of lines and polygon edges: none, fxaa or smaa. Also --aa");
            HelpOption("--mouse-sensitivity", "=N", $"Mouse movement divisor: higher is slower (default: {def.MouseSensitivity})");
            HelpOption("--port0", "=<device>", "Mouse port: mouse, joystick (keys and gamepad), keys, gamepad or none (default: mouse)");
            HelpOption("--port1", "=<device>", "Joystick port: joystick (keys and gamepad), keys, gamepad or none (default: joystick)");

            HelpSection("MIDI");
            HelpOption("--midi", "=<mode>", "MIDI emulation: none|system|mt32");
            HelpOption("--midi-in", "=<name>", "Host MIDI input port, by name (system mode)");
            HelpOption("--midi-out", "=<name>", "Host MIDI output port, by name (system mode)");
            HelpOption("--mt32-roms", "=<path>", "Folder with the Roland MT-32 ROM images (mt32 mode)");
            HelpOption("--mt32-volume", "=N", $"Built-in MT-32 volume in percent, 0-{Mt32Backend.MaxVolume} (default: {def.Mt32Volume})");

            HelpSection("Online play");
            HelpOption("--player-name", "=<name>", "The name other players see");
            HelpOption("--netplay-port", "=N", $"UDP port a hosted game listens on (default: {def.NetplayPort})");
            HelpOption("--join", "=LINK", "Opens the Online play window to join with an invitation link");
            HelpOption("--netplay-web", "=URL", $"Website for online play's invitation links; empty for none (default: {def.NetplayWebUrl})");

            HelpSection("Timing (advanced)");
            HelpOption("--cycleexact", "[=true|false]", $"Cycle-exact bus wait states (default: {(def.CycleExactBus ? "on" : "off")})");
            HelpOption("--busphase", "=N", $"Phase of the 4-cycle MMU bus grid, 0-3 (default: {def.BusPhase})");
            HelpOption("--mfpwait", "=N", $"Extra wait cycles per MFP access (default: {def.MfpWaitCycles})");

            HelpSection("Diagnostics");
            HelpOption("--debug", "[=level]", "Verbosity: none|quiet|information|full (bare = full)");
            HelpOption("--profile", "[=N]", "Timing breakdown every N frames (default: 50)");
            HelpOption("--console", "[=true|false]", "Windows: open a console window for the log (not needed when ASE is launched from one)");
            HelpOption("--help, -h", "", "Show this help message");
        }

        /// <summary>Section header of the help listing.</summary>
        static void HelpSection(string title) =>
            ColoredConsole.WriteLine($"{Environment.NewLine}[[white]]{title}:[[/white]]");

        /// <summary>
        /// One option of the help listing: flag in cyan, its argument in yellow, description
        /// aligned to a fixed column. The padding is computed from the visible text, since the
        /// colour markup is stripped before anything reaches the console.
        /// </summary>
        static void HelpOption(string flag, string argument, string description)
        {
            string padding = new string(' ', Math.Max(1, 30 - flag.Length - argument.Length));
            string coloredArg = argument.Length > 0 ? $"[[yellow]]{argument}[[/yellow]]" : "";

            ColoredConsole.WriteLine($"  [[cyan]]{flag}[[/cyan]]{coloredArg}{padding}{description}");
        }

        public static string GetAppDefaultConfigsFilePath()
        {
            string basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolderPath = Path.Combine(basePath, AppName);

            if (!Directory.Exists(appFolderPath))
            {
                Directory.CreateDirectory(appFolderPath);
            }

            return appFolderPath;
        }

        /// <summary>
        /// Whether ASE runs on a Raspberry Pi. The board's device tree names it ("Raspberry Pi 4
        /// Model B Rev 1.4"), on Raspberry Pi OS and the other Linux distributions for it alike,
        /// which is more than a guess from the processor or the memory could say. Read once;
        /// false anywhere else, or when neither file can be read.
        /// </summary>
        public static bool HostIsRaspberryPi => _hostIsRaspberryPi.Value;

        static readonly Lazy<bool> _hostIsRaspberryPi = new(() =>
        {
            if (!OperatingSystem.IsLinux())
                return false;

            foreach (string path in new[] { "/proc/device-tree/model", "/sys/firmware/devicetree/base/model" })
            {
                try
                {
                    if (File.Exists(path))
                        return File.ReadAllText(path).StartsWith("Raspberry Pi", StringComparison.Ordinal);
                }
                catch (Exception)
                {
                    // Unreadable: try the other one, or take it for something else
                }
            }

            return false;
        });

        /// <summary>Directory where Shift+F11 saves PNG screenshots (configured value, or the
        /// default "Screenshots" folder next to config.json when unset).</summary>
        public static string ScreenshotsDir => DirOrDefault(ConfigOptions.RunninConfig.ScreenshotsPath, "Screenshots");

        /// <summary>Directory where F11 saves machine snapshots (configured value, or the
        /// default "Snapshots" folder next to config.json when unset).</summary>
        public static string SnapshotsDir => DirOrDefault(ConfigOptions.RunninConfig.SnapshotsPath, "Snapshots");

        static string DirOrDefault(string configured, string defaultSubfolder)
            => string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(GetAppDefaultConfigsFilePath(), defaultSubfolder)
                : configured;

        /// <summary>Initial location for a tinyfiledialogs dialog: the given directory with a
        /// trailing separator (which is how tinyfd tells folders from files), or "" when the
        /// directory is unset or missing so the dialog keeps its own default.</summary>
        public static string DialogStartFolder(string dir)
            => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                ? dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
                : "";

        /// <summary>Where a file path from the configuration really points. A relative path is
        /// looked up in the working directory first, as it always was, and then next to the
        /// executable: the bundled EmuTOS is configured as a plain "emutos.rom", and a macOS app
        /// started from Finder runs with "/" as its working directory, so the file sitting beside
        /// the binary would never be found. Returns the path unchanged when neither exists.</summary>
        public static string ResolveFilePath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || File.Exists(path))
                return path;

            string besideExecutable = Path.Combine(AppContext.BaseDirectory, path);
            return File.Exists(besideExecutable) ? besideExecutable : path;
        }

        public void LoadJsonConfig(string ConfigFile = "")
        {
            if(string.IsNullOrEmpty(ConfigFile))
                ConfigFile = PathToDefaultConfig;

            try
            {
                if (File.Exists(ConfigFile))
                {
                    JsonSerializerOptions options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    string json = File.ReadAllText(ConfigFile);
                    ConfigOptions cfg = JsonSerializer.Deserialize<ConfigOptions>(json, options);

                    if (cfg == null)
                    {
                        ColoredConsole.WriteLine($"ERROR: Could not parse config file [[red]]{ConfigFile}[[/red]].");
                        Environment.Exit(1);
                    }

                    ConfigOptions.RunninConfig = cfg;

                    ColoredConsole.WriteLine($"I'm using [[green]]{ConfigFile}[[/green]] as config file.");
                    return;
                }

            }
            catch 
            {
                ColoredConsole.WriteLine($"ERROR: Could not configure using [[red]]{ConfigFile}[[/red]] config file.");
                Environment.Exit(1);
            }

            ColoredConsole.WriteLine($"ERROR: Config file [[red]]{ConfigFile}[[/red]] does not exists.");
            Environment.Exit(1);
        }

        /// <summary>
        /// Puts back, in a copy of the running configuration about to be written, the settings a
        /// shared game borrowed from the host (Netplay.Lockstep): the machine the guest runs is
        /// the host's until its next power-on, and config.json must never get it (NETPLAY.md
        /// §5.1). Null when nothing is borrowed.
        /// </summary>
        public static Action<ConfigOptions> PersistOverlay;

        public void DumpJsonConfig(string ConfigFile = "")
        {
            if (string.IsNullOrEmpty(ConfigFile))
                ConfigFile = PathToDefaultConfig;

            try
            {
                JsonSerializerOptions options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                };

                var config = ConfigOptions.RunninConfig;
                var overlay = PersistOverlay;
                if (overlay != null)
                {
                    config = JsonSerializer.Deserialize<ConfigOptions>(JsonSerializer.Serialize(config, options));
                    overlay(config);
                }

                string json = JsonSerializer.Serialize(config, options);

                File.WriteAllText(ConfigFile, json);
            }
            catch
            {
                ColoredConsole.WriteLine($"ERROR: Cannot create [[red]]{ConfigFile}[[//red]] config file.");
                Environment.Exit(1);
            }
        }
    }
}
