using System.Runtime.InteropServices;

namespace Mt32;

/// <summary>
/// P/Invoke bindings for the C interface of libmt32emu (mt32emu/src/c_interface/c_interface.h).
///
/// Type mapping used here:
///   mt32emu_context / mt32emu_const_context -> IntPtr   (opaque pointer to struct mt32emu_data)
///   mt32emu_report_handler_i                -> IntPtr   (single-pointer union; IntPtr.Zero = default handler)
///   mt32emu_bit32u                          -> uint
///   mt32emu_bit16s                          -> short
///   mt32emu_bit8u                           -> byte
///   mt32emu_boolean                         -> int      (C enum: 0 = FALSE, 1 = TRUE)
///   mt32emu_return_code                     -> int      (C enum, see Mt32Rc)
///   size_t                                  -> nuint / UIntPtr
///
/// The calling convention is __cdecl on Windows (MT32EMU_C_CALL).
/// </summary>
internal static unsafe class Mt32EmuNative
{
    /// <summary>
    /// Name of the native library. Careful: the CMake build appends the major version
    /// suffix, so the file is called "mt32emu-2.dll", not "mt32emu.dll".
    /// .NET's resolver also tries the name without an extension, so this one is enough.
    /// </summary>
    public const string DllName = "mt32emu-2";

    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    // ==================== Context-independent functions ====================

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern uint mt32emu_get_library_version_int();

    /// <summary>Returns a const char*; read it with Marshal.PtrToStringAnsi.</summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern IntPtr mt32emu_get_library_version_string();

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern uint mt32emu_get_stereo_output_samplerate(Mt32AnalogOutputMode analogOutputMode);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern Mt32AnalogOutputMode mt32emu_get_best_analog_output_mode(double targetSamplerate);

    // ==================== Context lifetime ====================

    /// <summary>
    /// reportHandler is an mt32emu_report_handler_i union passed by value. Being pointer-sized,
    /// IntPtr is ABI-compatible with it. Pass IntPtr.Zero for the default handler.
    /// </summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern IntPtr mt32emu_create_context(IntPtr reportHandler, IntPtr instanceData);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_free_context(IntPtr context);

    // ==================== ROM management ====================

    [DllImport(DllName, CallingConvention = Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int mt32emu_add_rom_file(IntPtr context, string filename);

    [DllImport(DllName, CallingConvention = Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int mt32emu_merge_and_add_rom_files(IntPtr context, string part1Filename, string part2Filename);

    [DllImport(DllName, CallingConvention = Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int mt32emu_add_machine_rom_file(IntPtr context, string machineId, string filename);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_get_rom_info(IntPtr context, out Mt32RomInfo romInfo);

    // ==================== Configuration before openSynth ====================

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_set_partial_count(IntPtr context, uint partialCount);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_set_analog_output_mode(IntPtr context, Mt32AnalogOutputMode analogOutputMode);

    /// <summary>0 = use the native sample rate of the selected analog mode.</summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_set_stereo_output_samplerate(IntPtr context, double samplerate);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_select_renderer_type(IntPtr context, Mt32RendererType rendererType);

    // ==================== Opening / closing the synth ====================

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_open_synth(IntPtr context);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_close_synth(IntPtr context);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_is_open(IntPtr context);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern uint mt32emu_get_actual_stereo_output_samplerate(IntPtr context);

    // ==================== Sending MIDI ====================

    /// <summary>
    /// Queues a short MIDI message packed into 32 bits: status | (data1 &lt;&lt; 8) | (data2 &lt;&lt; 16).
    /// This is the recommended route from a thread other than the rendering one (the queue
    /// takes care of the synchronisation). Returns an mt32emu_return_code.
    /// </summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_play_msg(IntPtr context, uint msg);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_play_sysex(IntPtr context, byte[] sysex, uint len);

    /// <summary>
    /// Immediate processing, WITHOUT going through the queue. Requires explicit synchronisation
    /// with the rendering thread. In an app with real-time audio, prefer mt32emu_play_msg.
    /// </summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_play_msg_now(IntPtr context, uint msg);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_play_sysex_now(IntPtr context, byte[] sysex, uint len);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_flush_midi_queue(IntPtr context);

    // ==================== Audio rendering ====================

    /// <summary>
    /// Renders interleaved 16-bit stereo PCM audio.
    /// CAREFUL: 'len' is counted in FRAMES, not in bytes nor in samples.
    /// A 16-bit stereo frame is 4 bytes (2 shorts).
    /// </summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_render_bit16s(IntPtr context, short[] stream, uint len);

    /// <summary>Pointer overload, to render into someone else's buffer without copies.</summary>
    [DllImport(DllName, EntryPoint = "mt32emu_render_bit16s", CallingConvention = Cdecl)]
    public static extern void mt32emu_render_bit16s(IntPtr context, short* stream, uint len);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern void mt32emu_render_float(IntPtr context, float[] stream, uint len);

    // ==================== State ====================

    /// <summary>true if there is at least one active partial.</summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_has_active_partials(IntPtr context);

    /// <summary>true if there are active partials or the reverb is still sounding.</summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_is_active(IntPtr context);

    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern uint mt32emu_get_partial_count(IntPtr context);

    /// <summary>Returns a const char* with the patch name of the given part (0..7, 8 = rhythm).</summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern IntPtr mt32emu_get_patch_name(IntPtr context, byte partNumber);

    /// <summary>
    /// Reads the contents of the emulated LCD. targetBuffer must hold at least 21 characters.
    /// Returns whether the MIDI MESSAGE LED is lit.
    /// </summary>
    [DllImport(DllName, CallingConvention = Cdecl)]
    public static extern int mt32emu_get_display_state(IntPtr context, byte[] targetBuffer, int narrowLcd);
}

/// <summary>Return codes (mt32emu_return_code from c_types.h).</summary>
internal static class Mt32Rc
{
    // Success
    public const int Ok = 0;
    public const int AddedControlRom = 1;
    public const int AddedPcmRom = 2;
    public const int AddedPartialControlRom = 3;
    public const int AddedPartialPcmRom = 4;

    // Errors (always negative)
    public const int RomNotIdentified = -1;
    public const int FileNotFound = -2;
    public const int FileNotLoaded = -3;
    public const int MissingRoms = -4;
    public const int NotOpened = -5;
    public const int QueueFull = -6;
    public const int RomsNotPairable = -7;
    public const int MachineNotIdentified = -8;
    public const int Failed = -100;

    public static bool IsError(int rc) => rc < 0;

    public static string Describe(int rc) => rc switch
    {
        Ok => "OK",
        AddedControlRom => "control ROM added",
        AddedPcmRom => "PCM ROM added",
        AddedPartialControlRom => "partial control ROM added",
        AddedPartialPcmRom => "partial PCM ROM added",
        RomNotIdentified => "ROM not identified (unknown SHA1)",
        FileNotFound => "file not found",
        FileNotLoaded => "the file could not be read",
        MissingRoms => "missing ROMs (control + PCM are both needed)",
        NotOpened => "the synth is not open",
        QueueFull => "MIDI queue full",
        RomsNotPairable => "the partial ROMs cannot be paired",
        MachineNotIdentified => "machine identifier not recognised",
        Failed => "unspecified error",
        _ => $"unknown code ({rc})"
    };
}

/// <summary>
/// mt32emu_analog_output_mode. Values as per Enumerations.h:
/// 0 = DIGITAL_ONLY, 1 = COARSE, 2 = ACCURATE, 3 = OVERSAMPLED.
/// </summary>
public enum Mt32AnalogOutputMode
{
    DigitalOnly = 0,
    Coarse = 1,
    Accurate = 2,
    Oversampled = 3
}

/// <summary>mt32emu_renderer_type: 0 = 16-bit integer, 1 = 32-bit float.</summary>
public enum Mt32RendererType
{
    Bit16S = 0,
    Float = 1
}

/// <summary>
/// mt32emu_rom_info. Every field is a const char*, read with Marshal.PtrToStringAnsi.
/// Unused fields come back as NULL.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Mt32RomInfo
{
    public IntPtr ControlRomId;
    public IntPtr ControlRomDescription;
    public IntPtr ControlRomSha1Digest;
    public IntPtr PcmRomId;
    public IntPtr PcmRomDescription;
    public IntPtr PcmRomSha1Digest;

    public string? ControlId => Marshal.PtrToStringAnsi(ControlRomId);
    public string? ControlDescription => Marshal.PtrToStringAnsi(ControlRomDescription);
    public string? PcmId => Marshal.PtrToStringAnsi(PcmRomId);
    public string? PcmDescription => Marshal.PtrToStringAnsi(PcmRomDescription);
}
