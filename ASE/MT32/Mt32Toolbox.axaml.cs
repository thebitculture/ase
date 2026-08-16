using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ASE.MT32;

/// <summary>
/// Front panel of the built-in Roland MT-32: the module's volume knob and a live copy of
/// its LCD. Opened from the Emulation menu (<see cref="MainWindow.OnMt32ToolboxClick"/>),
/// which keeps a single instance and greys the entry out while this window is up.
///
/// Unlike the emulator's dialogs this one deliberately does NOT hold a UI pause: both the
/// knob and the display are only worth anything while the machine is running.
/// </summary>
public partial class Mt32Toolbox : Window
{
    public class PresetsProfile
    {
        // MT32 mapping for title
        public bool IsM32Mapped { get; set; } = false;
        public string Voice1Preset { get; set; } = "";
        public string Voice2Preset { get; set; } = "";
        public string Voice3Preset { get; set; } = "";
        public bool NoiseMapToDrums { get; set; } = false;
    }

    // Knob travel, as on the real module: 270° between the two end stops.
    const double MinAngle = -135;
    const double MaxAngle = 135;

    /// <summary>Volume the knob sits on when it points straight up (0°) — the configured
    /// default. Munt's line level is noticeably below the PSG's, hence a boost at neutral
    /// rather than 100%.</summary>
    const int NeutralVolume = 200;

    /// <summary>Degrees the knob turns per pixel of horizontal drag: a full 270° sweep
    /// takes a 270-pixel drag.</summary>
    const double DegreesPerPixel = 1.0;

    /// <summary>Shown on the LCD when there is no module behind it (MIDI emulation set to
    /// something else, missing ROMs, Munt not loadable…).</summary>
    const string LcdOffline = "MT-32 not running";

    /// <summary>How long the LCD keeps showing a message of ASE's own (see
    /// <see cref="FlashLcd"/>) before the module's display takes it back: long enough to
    /// read, short enough not to hide the module.</summary>
    static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(1.5);

    /// <summary>Opacity the LCD drops to when there is no module behind it — an unlit
    /// backlight, which reads at a glance as "this panel is inert".</summary>
    const double LcdOffOpacity = 0.35;

    /// <summary>Opacity of the volume knob while it is disabled. The combos and the
    /// checkbox grey themselves out through the theme's disabled styles; a Border has no
    /// such style, so the knob has to be dimmed by hand.</summary>
    const double KnobOffOpacity = 0.4;

    // Key this window's position is stored under in windows.json (see WindowLayouts). Only
    // the position: the window is fixed-size.
    const string LayoutKey = "Mt32Toolbox";

    /// <summary>First entry of the three YM-channel lists, and their default: the channel
    /// is left alone.</summary>
    const string NotMapped = "Not mapped";

    /// <summary>
    /// What the YM-channel combos offer: "Not mapped" and then the MT-32's 128 preset
    /// timbres, each prefixed with the program-change number that selects it on the module.
    /// Built once and shared by the three lists — an ItemsControl never writes to its
    /// source, so one array serves all of them. Index 0 is "not mapped"; from there on the
    /// selected index minus one is the program change.
    /// </summary>
    static readonly string[] MapOptions = BuildMapOptions();

    static string[] BuildMapOptions()
    {
        var options = new string[Mt32Timbres.Presets.Length + 1];
        options[0] = NotMapped;

        for (int i = 0; i < Mt32Timbres.Presets.Length; i++)
            options[i + 1] = $"{i + 1,3}  {Mt32Timbres.Presets[i]}";

        return options;
    }

    readonly RotateTransform _dialRotation = new();
    readonly DispatcherTimer _lcdTimer;

    // Drag state: the knob turns by how far the pointer has travelled since it was
    // pressed, not by where it is now, so grabbing the knob never makes it jump.
    bool _dragging;
    double _dragStartX;
    double _dragStartAngle;

    // Last volume this window wrote to the running config, and the one it found on open.
    // The first lets the poll notice a change made elsewhere (the Configuration window's
    // slider) and re-align the knob; the second decides whether closing has to persist.
    int _appliedVolume;
    readonly int _volumeAtOpen;

    // Message currently owning the LCD instead of the module (null = none) and when it
    // expires. The poll tick is what takes it away, so nothing else has to be scheduled.
    string _flash;
    DateTime _flashUntil;

    // Last module-availability state pushed into the controls. Nullable so the first poll
    // always applies one, whichever it is.
    bool? _moduleAvailable;

    public Mt32Toolbox()
    {
        InitializeComponent();

        // Before the window is shown, so it comes up where it was left last session.
        WindowLayouts.Restore(this, LayoutKey);

        SvgVolumeDial.RenderTransformOrigin = RelativePoint.Center;
        SvgVolumeDial.RenderTransform = _dialRotation;

        FillInstrumentLists();
        RefreshProfileButton();

        // The loaded game can change while this window is up (the library dialog does not
        // close it), and with it the mapping the combos have to show.
        Mt32Profiles.CurrentGameChanged += OnCurrentGameChanged;

        _volumeAtOpen = Config.ConfigOptions.RunninConfig.Mt32Volume;
        SetVolume(_volumeAtOpen);

        // Settle both before the first layout, so the window does not visibly change text
        // or light up and go dark again the moment it appears — the poll only runs 150 ms
        // after it is shown.
        SetModuleAvailable(MidiManager.Mt32Active);
        RefreshLcd();

        // Normal priority on purpose: the main window's GL control renders continuously
        // and starves anything below Input for frames at a time, which would leave the
        // LCD lagging seconds behind the module.
        _lcdTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
        _lcdTimer.Tick += OnPollTick;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _lcdTimer.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Read here and not in OnClosed: by then the window is gone and its position no
        // longer means anything. Reached from the close button and from the main window
        // closing this one with it.
        WindowLayouts.Remember(this, LayoutKey);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lcdTimer.Stop();
        Mt32Profiles.CurrentGameChanged -= OnCurrentGameChanged;

        // The knob writes straight into the running config (that is what makes it apply
        // live), so closing is this window's "OK": persist it, but only when it actually
        // moved — no need to rewrite config.json every time the toolbox is dismissed.
        if (Config.ConfigOptions.RunninConfig.Mt32Volume != _volumeAtOpen)
            Program.Config.DumpJsonConfig();

        base.OnClosed(e);
    }

    private void OnTitleBarPressed(object sender, PointerPressedEventArgs e)
    {
        // Este método sólo existe para solucionar que linux no mueve la ventana al arrastrar del título
        // Esto hay que revisarlo con nuevas versiones de Avalonia.

        if (!OperatingSystem.IsLinux())
            return;

        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        // Ningún botón de la barra (cerrar, guardar perfil) se atrapa: la pulsación es suya.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is not null)
            return;

        BeginMoveDrag(e);
    }

    void OnPollTick(object sender, EventArgs e)
    {
        // The ST is no longer wired to the built-in module: this window is its front
        // panel, so it has nothing left to be in front of. Polling rather than being told
        // is deliberate — the mode can change from the Configuration window, a restored
        // snapshot or a reset, and MidiManager.Mode only moves when one of those actually
        // commits (see its remarks).
        if (MidiManager.Mode != Config.ConfigOptions.MIDIEmulationOptions.BuiltInMT32)
        {
            Close();
            return;
        }

        // Wired to the module but the module is not there (missing/invalid ROMs, Munt not
        // loadable, machine off): the panel stays up, dark and inert, instead of offering
        // knobs that reach nothing.
        SetModuleAvailable(MidiManager.Mt32Active);

        RefreshLcd();

        // The Configuration window's slider drives the same setting; pick up whatever it
        // left behind so the knob never shows a stale position.
        int volume = Config.ConfigOptions.RunninConfig.Mt32Volume;
        if (volume != _appliedVolume && !_dragging)
            SetVolume(volume);
    }

    /// <summary>Lights the panel up or shuts it down: an unlit LCD and dead controls when
    /// there is no module behind them. Idempotent — the poll calls it every tick.</summary>
    void SetModuleAvailable(bool available)
    {
        if (_moduleAvailable == available)
            return;

        _moduleAvailable = available;

        LcdPanel.Opacity = available ? 1.0 : LcdOffOpacity;

        VolumePanel.IsEnabled = available;
        VolumePanel.Opacity = available ? 1.0 : KnobOffOpacity;

        MappingPanel.IsEnabled = available;

        // Nothing to save while the mapping cannot be seen or changed.
        btnSavePresetsProfile.IsEnabled = available;

        // A drag in flight would otherwise keep turning a knob that no longer applies.
        if (!available && _dragging)
        {
            _dragging = false;
            _flash = null;
        }
    }

    /// <summary>Copies the module's LCD into the toolbox display — except while the knob
    /// is being turned (the display belongs to the volume then) or while one of ASE's own
    /// messages still has it.</summary>
    void RefreshLcd()
    {
        if (_dragging)
        {
            TxtMt32Lcd.Text = VolumeLcdText;
            return;
        }

        if (_flash != null)
        {
            if (DateTime.UtcNow < _flashUntil)
            {
                TxtMt32Lcd.Text = _flash;
                return;
            }

            _flash = null;
        }

        string text = MidiManager.Mt32DisplayText();
        TxtMt32Lcd.Text = string.IsNullOrWhiteSpace(text) ? LcdOffline : text.TrimEnd();
    }

    /// <summary>Puts a message of ASE's own on the LCD for <see cref="FlashDuration"/> —
    /// this window has no other place to answer a button with.</summary>
    void FlashLcd(string text)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow + FlashDuration;
        TxtMt32Lcd.Text = text;
    }

    /// <summary>What the LCD reads while the knob is turning. The number is right-aligned
    /// in three columns so it does not shuffle sideways as the value crosses 10 and 100.</summary>
    string VolumeLcdText => $"Volume {_appliedVolume,3}%";

    /// <summary>Offers the module's instruments on the three YM-channel lists and shows
    /// the mapper's current state — the mapping is a live routing that outlives this
    /// window, so reopening it must not reset anything.</summary>
    void FillInstrumentLists()
    {
        ComboBox[] lists = { cmbMapYM1, cmbMapYM2, cmbMapYM3 };

        for (int voice = 0; voice < lists.Length; voice++)
        {
            lists[voice].ItemsSource = MapOptions;
            // Program -1 (not mapped) -> index 0; program n -> index n+1.
            lists[voice].SelectedIndex = YmMidiMapper.GetProgram(voice) + 1;
        }

        chkDrums.IsChecked = YmMidiMapper.DrumsEnabled;
    }

    /// <summary>A YM voice was (re)mapped. SetProgram is idempotent, so the events fired
    /// while <see cref="FillInstrumentLists"/> seeds the lists are harmless.</summary>
    void OnMapChanged(object sender, SelectionChangedEventArgs e)
    {
        int voice = sender == cmbMapYM1 ? 0 : sender == cmbMapYM2 ? 1 : 2;
        int index = ((ComboBox)sender).SelectedIndex;

        if (index >= 0)
            YmMidiMapper.SetProgram(voice, index - 1);
    }

    void OnDrumsChanged(object sender, RoutedEventArgs e)
        => YmMidiMapper.DrumsEnabled = chkDrums.IsChecked == true;

    // ==================== Per-game profile ====================

    /// <summary>A disk was inserted while this window was open: <see cref="Mt32Profiles"/>
    /// has already pushed the game's instruments into the mapper, so the lists only have to
    /// catch up with it.</summary>
    void OnCurrentGameChanged()
    {
        FillInstrumentLists();
        RefreshProfileButton();
    }

    /// <summary>The save button only exists for games loaded from the library — a disk
    /// opened by hand has no catalogue entry to write the mapping into.</summary>
    void RefreshProfileButton()
    {
        btnSavePresetsProfile.IsVisible = Mt32Profiles.CurrentGame != null;
    }

    /// <summary>Stores the mapping now showing in the lists as the loaded game's profile,
    /// so it comes back on its own next time the game is launched from the library.</summary>
    void OnSavePresetsProfile(object sender, RoutedEventArgs e)
    {
        if (Mt32Profiles.SaveCurrentProfile(out string error))
        {
            FlashLcd("Profile saved");
            return;
        }

        // The LCD has room for the verdict only; the reason goes to the console, which is
        // where this window's other failures already report.
        FlashLcd("Save failed");
        ColoredConsole.WriteLine($"MT-32: could not save the game's instrument profile — [[red]]{error}[[/red]]");
    }

    // ==================== Volume knob ====================

    void OnVolumeDialPressed(object sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartAngle = _dialRotation.Angle;

        // The knob takes the display over; a message still on it would come back when the
        // drag ends, out of context by then.
        _flash = null;

        e.Pointer.Capture(VolumeKnob);
        e.Handled = true;
    }

    void OnVolumeDialMoved(object sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        double travel = e.GetPosition(this).X - _dragStartX;
        SetAngle(_dragStartAngle + travel * DegreesPerPixel);
        e.Handled = true;
    }

    void OnVolumeDialReleased(object sender, PointerReleasedEventArgs e)
    {
        // Released unconditionally, before the _dragging guard: SetModuleAvailable can end
        // a drag from the outside (the module went away mid-turn) without the pointer ever
        // being let go, and a capture left behind would swallow the next click.
        e.Pointer.Capture(null);

        if (!_dragging)
            return;

        _dragging = false;
        e.Handled = true;

        // Hand the display back to the module.
        RefreshLcd();
    }

    // Losing the capture (another window stealing it, the pointer device going away) has
    // to end the drag too, or the knob would keep following the pointer afterwards.
    void OnVolumeDialCaptureLost(object sender, PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        RefreshLcd();
    }

    /// <summary>Turns the knob to <paramref name="angle"/> (clamped to the end stops) and
    /// applies the volume that position stands for.</summary>
    void SetAngle(double angle)
    {
        angle = Math.Clamp(angle, MinAngle, MaxAngle);
        _dialRotation.Angle = angle;
        ApplyVolume(VolumeFromAngle(angle));
    }

    /// <summary>Places the knob at the position <paramref name="volume"/> stands for and
    /// applies it — the reverse of <see cref="SetAngle"/>, used when the value comes from
    /// somewhere else (startup, the Configuration window).</summary>
    void SetVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, Mt32Backend.MaxVolume);
        _dialRotation.Angle = AngleFromVolume(volume);
        ApplyVolume(volume);
    }

    /// <summary>Writes the volume into the running config, where <c>Mt32Backend.MixInto</c>
    /// reads it once per audio buffer — which is what makes the knob apply live, with the
    /// gain ramped across the buffer instead of stepping at its boundary.</summary>
    void ApplyVolume(int volume)
    {
        _appliedVolume = volume;
        Config.ConfigOptions.RunninConfig.Mt32Volume = volume;
        ToolTip.SetTip(VolumeKnob, $"MT-32 volume: {volume}% — drag left/right");

        // Straight to the display rather than waiting for the poll: turning the knob has
        // to read as continuous, and the poll only runs every 150 ms.
        if (_dragging)
            TxtMt32Lcd.Text = VolumeLcdText;
    }

    // Both halves of the travel are mapped separately so neutral stays exactly at 12
    // o'clock: fully anticlockwise mutes, neutral is NeutralVolume, fully clockwise is
    // the backend's ceiling.
    static int VolumeFromAngle(double angle)
    {
        double volume = angle <= 0
            ? NeutralVolume * (angle - MinAngle) / -MinAngle
            : NeutralVolume + (Mt32Backend.MaxVolume - NeutralVolume) * (angle / MaxAngle);

        return (int)Math.Round(Math.Clamp(volume, 0, Mt32Backend.MaxVolume));
    }

    static double AngleFromVolume(int volume)
        => volume <= NeutralVolume
            ? MinAngle * (1 - (double)volume / NeutralVolume)
            : MaxAngle * (double)(volume - NeutralVolume) / (Mt32Backend.MaxVolume - NeutralVolume);

    private void OnClose(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
