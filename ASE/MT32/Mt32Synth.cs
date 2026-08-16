using System.Runtime.InteropServices;
using System.Text;

namespace Mt32;

/// <summary>
/// Safe managed wrapper around a libmt32emu context.
///
/// Threading model this class assumes (the one the library supports):
///   - Render() is called from ONE thread only (typically the audio callback).
///   - MIDI is sent with mt32emu_play_msg, which queues the event; that needs no
///     synchronisation with the rendering thread, but the senders do need it between
///     themselves, hence the lock.
/// </summary>
public sealed unsafe class Mt32Synth : IDisposable
{
    private IntPtr _context;
    private readonly object _midiLock = new();

    /// <summary>Actual sample rate of the audio Render() produces, in Hz.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Always 2: the MT-32's stereo output.</summary>
    public const int Channels = 2;

    public static string LibraryVersion =>
        Marshal.PtrToStringAnsi(Mt32EmuNative.mt32emu_get_library_version_string()) ?? "unknown";

    private Mt32Synth(IntPtr context)
    {
        _context = context;
    }

    /// <summary>
    /// Creates the context, loads the ROMs from the given directory and opens the synth.
    /// </summary>
    /// <param name="romDirectory">Folder holding the ROMs (control and PCM).</param>
    /// <param name="analogOutputMode">
    /// Emulation mode for the analog circuit. DigitalOnly gives 32000 Hz with no filtering;
    /// Coarse (the library's default) gives a higher sample rate and sounds closer to the hardware.
    /// </param>
    /// <param name="partialCount">Maximum number of simultaneous partials. The real hardware uses 32.</param>
    /// <param name="targetSampleRate">
    /// Sample rate Render() should produce, in Hz; the library resamples internally.
    /// 0 keeps the analog mode's native rate. Asking for the mixer's own rate saves the
    /// caller from resampling (ASE mixes the MT-32 straight into the PSG's SDL stream).
    /// </param>
    public static Mt32Synth Open(
        string romDirectory,
        Mt32AnalogOutputMode analogOutputMode = Mt32AnalogOutputMode.Coarse,
        uint partialCount = 32,
        int targetSampleRate = 0)
    {
        if (!Directory.Exists(romDirectory))
            throw new DirectoryNotFoundException($"ROM directory does not exist: {romDirectory}");

        IntPtr ctx = Mt32EmuNative.mt32emu_create_context(IntPtr.Zero, IntPtr.Zero);
        if (ctx == IntPtr.Zero)
            throw new InvalidOperationException("mt32emu_create_context returned NULL.");

        var synth = new Mt32Synth(ctx);
        try
        {
            synth.LoadRoms(romDirectory);

            Mt32EmuNative.mt32emu_set_partial_count(ctx, partialCount);
            Mt32EmuNative.mt32emu_set_analog_output_mode(ctx, analogOutputMode);
            Mt32EmuNative.mt32emu_select_renderer_type(ctx, Mt32RendererType.Bit16S);
            // 0 = no sample rate conversion: use the analog mode's native one.
            Mt32EmuNative.mt32emu_set_stereo_output_samplerate(ctx, targetSampleRate);

            int rc = Mt32EmuNative.mt32emu_open_synth(ctx);
            if (rc != Mt32Rc.Ok)
                throw new InvalidOperationException($"mt32emu_open_synth failed: {Mt32Rc.Describe(rc)}");

            synth.SampleRate = (int)Mt32EmuNative.mt32emu_get_actual_stereo_output_samplerate(ctx);
            return synth;
        }
        catch
        {
            synth.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Walks the directory handing every file to mt32emu_add_rom_file. The library identifies
    /// each ROM by its SHA1, so the file names do not matter. This is the same approach the
    /// official mt32emu-smf2wav tool takes.
    /// </summary>
    private void LoadRoms(string romDirectory)
    {
        bool controlFound = false;
        bool pcmFound = false;
        var partialControl = new List<string>();
        var partialPcm = new List<string>();

        foreach (string file in Directory.EnumerateFiles(romDirectory))
        {
            int rc = Mt32EmuNative.mt32emu_add_rom_file(_context, file);
            switch (rc)
            {
                case Mt32Rc.AddedControlRom: controlFound = true; break;
                case Mt32Rc.AddedPcmRom: pcmFound = true; break;
                case Mt32Rc.AddedPartialControlRom: partialControl.Add(file); break;
                case Mt32Rc.AddedPartialPcmRom: partialPcm.Add(file); break;
                // RomNotIdentified simply means "this file is not a known ROM".
            }

            if (controlFound && pcmFound) break;
        }

        // The original MT-32 ROMs usually come split into two halves (_l and _h).
        if (!pcmFound && partialPcm.Count >= 2)
        {
            int rc = Mt32EmuNative.mt32emu_merge_and_add_rom_files(_context, partialPcm[0], partialPcm[1]);
            if (!Mt32Rc.IsError(rc)) pcmFound = true;
        }

        if (!controlFound && partialControl.Count >= 2)
        {
            int rc = Mt32EmuNative.mt32emu_merge_and_add_rom_files(_context, partialControl[0], partialControl[1]);
            if (!Mt32Rc.IsError(rc)) controlFound = true;
        }

        if (!controlFound || !pcmFound)
        {
            throw new FileNotFoundException(
                $"No complete ROM set found in '{romDirectory}'. " +
                $"Control ROM: {(controlFound ? "OK" : "MISSING")}, PCM ROM: {(pcmFound ? "OK" : "MISSING")}.");
        }
    }

    public Mt32RomInfo GetRomInfo()
    {
        ThrowIfDisposed();
        Mt32EmuNative.mt32emu_get_rom_info(_context, out Mt32RomInfo info);
        return info;
    }

    /// <summary>Current contents of the emulated LCD (20 characters).</summary>
    public string GetDisplayText()
    {
        ThrowIfDisposed();
        var buffer = new byte[21];
        Mt32EmuNative.mt32emu_get_display_state(_context, buffer, 0);
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return Encoding.ASCII.GetString(buffer, 0, len);
    }

    public string? GetPatchName(byte part)
    {
        ThrowIfDisposed();
        return Marshal.PtrToStringAnsi(Mt32EmuNative.mt32emu_get_patch_name(_context, part));
    }

    /// <summary>true while something is still sounding (the reverb tail included).</summary>
    public bool IsActive
    {
        get
        {
            ThrowIfDisposed();
            return Mt32EmuNative.mt32emu_is_active(_context) != 0;
        }
    }

    public bool HasActivePartials
    {
        get
        {
            ThrowIfDisposed();
            return Mt32EmuNative.mt32emu_has_active_partials(_context) != 0;
        }
    }

    /// <summary>Maximum number of simultaneous partials configured for this session.</summary>
    public int PartialCount
    {
        get
        {
            ThrowIfDisposed();
            return (int)Mt32EmuNative.mt32emu_get_partial_count(_context);
        }
    }

    // ==================== MIDI ====================

    /// <summary>
    /// Queues an already packed short MIDI message: status | (data1 &lt;&lt; 8) | (data2 &lt;&lt; 16).
    /// </summary>
    public void PlayMsg(uint packedMsg)
    {
        ThrowIfDisposed();
        lock (_midiLock)
        {
            int rc = Mt32EmuNative.mt32emu_play_msg(_context, packedMsg);
            if (rc == Mt32Rc.QueueFull)
                throw new InvalidOperationException("The MIDI queue is full.");
        }
    }

    public void PlayMsg(byte status, byte data1, byte data2 = 0)
        => PlayMsg((uint)status | ((uint)data1 << 8) | ((uint)data2 << 16));

    /// <summary>
    /// Note On. CAREFUL with the channels: by default the MT-32 assigns parts 1..8 to MIDI
    /// channels 2..9 and rhythm to channel 10. MIDI channel 1 (channel = 0) is NOT assigned
    /// to any part, so notes sent there produce no sound.
    /// </summary>
    /// <param name="channel">Zero-based MIDI channel. Use 1..8 for parts 1..8, and 9 for rhythm.</param>
    public void NoteOn(byte channel, byte note, byte velocity)
        => PlayMsg((byte)(0x90 | (channel & 0x0F)), note, velocity);

    public void NoteOff(byte channel, byte note)
        => PlayMsg((byte)(0x80 | (channel & 0x0F)), note, 0);

    /// <summary>Program Change: selects the timbre of the part bound to the channel.</summary>
    public void ProgramChange(byte channel, byte program)
        => PlayMsg((byte)(0xC0 | (channel & 0x0F)), program);

    public void ControlChange(byte channel, byte controller, byte value)
        => PlayMsg((byte)(0xB0 | (channel & 0x0F)), controller, value);

    /// <summary>Releases the sustain pedal and sends All Notes Off on all 16 channels.</summary>
    public void AllNotesOff()
    {
        for (byte ch = 0; ch < 16; ch++)
        {
            ControlChange(ch, 0x40, 0); // sustain off
            ControlChange(ch, 0x7B, 0); // all notes off
        }
    }

    public void PlaySysex(byte[] sysex)
    {
        ThrowIfDisposed();
        lock (_midiLock)
        {
            Mt32EmuNative.mt32emu_play_sysex(_context, sysex, (uint)sysex.Length);
        }
    }

    /// <summary>Shows a message on the emulated LCD (the MT-32's display SysEx).</summary>
    public void ShowLcdMessage(string text)
    {
        // Display address: 0x20 0x00 0x00, 20 ASCII characters.
        byte[] payload = new byte[3 + 20];
        payload[0] = 0x20; payload[1] = 0x00; payload[2] = 0x00;
        string padded = (text.Length > 20 ? text[..20] : text).PadRight(20);
        for (int i = 0; i < 20; i++) payload[3 + i] = (byte)padded[i];

        // Checksum: two's complement of the sum of address + data, in 7 bits.
        int sum = 0;
        foreach (byte b in payload) sum += b;
        byte checksum = (byte)((128 - (sum & 0x7F)) & 0x7F);

        var sysex = new List<byte> { 0xF0, 0x41, 0x10, 0x16, 0x12 };
        sysex.AddRange(payload);
        sysex.Add(checksum);
        sysex.Add(0xF7);

        PlaySysex(sysex.ToArray());
    }

    // ==================== Render ====================

    /// <summary>
    /// Renders <paramref name="frameCount"/> interleaved stereo frames into <paramref name="buffer"/>.
    /// The buffer must hold at least frameCount * 2 shorts.
    /// </summary>
    public void Render(short[] buffer, int frameCount)
    {
        ThrowIfDisposed();
        if (buffer.Length < frameCount * Channels)
            throw new ArgumentException("Buffer too small for the requested frame count.", nameof(buffer));

        Mt32EmuNative.mt32emu_render_bit16s(_context, buffer, (uint)frameCount);
    }

    /// <summary>Copy-free render into already pinned memory (used by the NAudio provider).</summary>
    public void Render(short* buffer, int frameCount)
    {
        ThrowIfDisposed();
        Mt32EmuNative.mt32emu_render_bit16s(_context, buffer, (uint)frameCount);
    }

    // ==================== Cleanup ====================

    private void ThrowIfDisposed()
    {
        if (_context == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(Mt32Synth));
    }

    public void Dispose()
    {
        if (_context == IntPtr.Zero) return;

        IntPtr ctx = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (ctx == IntPtr.Zero) return;

        if (Mt32EmuNative.mt32emu_is_open(ctx) != 0)
            Mt32EmuNative.mt32emu_close_synth(ctx);

        Mt32EmuNative.mt32emu_free_context(ctx);
    }
}
