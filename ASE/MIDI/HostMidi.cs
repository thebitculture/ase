using System.Runtime.InteropServices;
using System.Text;

namespace ASE
{
    /// <summary>One MIDI port as the host operating system publishes it.</summary>
    /// <param name="Name">
    /// Name shown to the user, and the value the configuration stores
    /// (<see cref="Config.ConfigOptions.MidiInDevice"/>/<see cref="Config.ConfigOptions.MidiOutDevice"/>).
    /// </param>
    /// <param name="Id">
    /// Platform handle for the port: the winmm device index on Windows, "client:port" of the ALSA
    /// sequencer on Linux, the CoreMIDI endpoint reference on macOS. It is only meaningful until
    /// the device list changes, so it is never persisted — the name is.
    /// </param>
    public readonly record struct HostMidiPort(string Name, string Id);

    /// <summary>
    /// Enumerates the MIDI ports the host operating system exposes. This is what fills the MIDI
    /// IN/OUT dropdowns of the Configuration window when the MIDI emulation is set to
    /// <see cref="Config.ConfigOptions.MIDIEmulationOptions.System"/>, and what the emulated MIDI
    /// port resolves the configured names against.
    ///
    /// One backend per platform and no external dependency: winmm on Windows, the ALSA sequencer
    /// on Linux and CoreMIDI on macOS. None of them is needed for the emulator to run, so every
    /// failure — a machine with no MIDI stack at all, a Linux install without libasound, an entry
    /// point that moved — degrades to an empty list and a console note instead of throwing.
    /// </summary>
    public static class HostMidi
    {
        /// <summary>Ports the host can send MIDI *to us* from (ST MIDI IN).</summary>
        public static IReadOnlyList<HostMidiPort> Inputs() => Enumerate(inputs: true);

        /// <summary>Ports we can send the ST's MIDI output to (ST MIDI OUT).</summary>
        public static IReadOnlyList<HostMidiPort> Outputs() => Enumerate(inputs: false);

        static IReadOnlyList<HostMidiPort> Enumerate(bool inputs)
        {
            string direction = inputs ? "input" : "output";

            try
            {
                IReadOnlyList<HostMidiPort> ports =
                    OperatingSystem.IsWindows() ? WinMM.Enumerate(inputs) :
                    OperatingSystem.IsMacOS() ? CoreMidi.Enumerate(inputs) :
                    OperatingSystem.IsLinux() ? Alsa.Enumerate(inputs) :
                    [];

                ColoredConsole.WriteLine($"MIDI: {ports.Count} host {direction} port(s) found.", Config.ConfigOptions.DebugModes.Information);
                return ports;
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException / anything the platform MIDI
                // stack throws: the emulator does not depend on this, so report and carry on.
                ColoredConsole.WriteLine(
                    $"MIDI: cannot enumerate the host {direction} ports ([[red]]{ex.GetType().Name}[[/red]]: {ex.Message}).",
                    Config.ConfigOptions.DebugModes.Quiet);

                return [];
            }
        }

        /// <summary>
        /// Windows: the MME API (winmm). Ports are a flat, driver-ordered list, which is exactly
        /// why the configuration stores names and not the indices returned here.
        /// </summary>
        static class WinMM
        {
            // MIDIINCAPSW / MIDIOUTCAPSW (mmeapi.h). szPname is a fixed field of MAXPNAMELEN
            // characters, 32 in the Unicode structures used below.
            const int MaxPnameLen = 32;
            const uint MMSYSERR_NOERROR = 0;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct MidiInCaps
            {
                public ushort wMid;
                public ushort wPid;
                public uint vDriverVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPnameLen)] public string szPname;
                public uint dwSupport;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct MidiOutCaps
            {
                public ushort wMid;
                public ushort wPid;
                public uint vDriverVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPnameLen)] public string szPname;
                public ushort wTechnology;
                public ushort wVoices;
                public ushort wNotes;
                public ushort wChannelMask;
                public uint dwSupport;
            }

            [DllImport("winmm.dll", ExactSpelling = true)]
            static extern uint midiInGetNumDevs();

            [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
            static extern uint midiInGetDevCaps(nuint deviceId, out MidiInCaps caps, uint capsSize);

            [DllImport("winmm.dll", ExactSpelling = true)]
            static extern uint midiOutGetNumDevs();

            [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
            static extern uint midiOutGetDevCaps(nuint deviceId, out MidiOutCaps caps, uint capsSize);

            public static List<HostMidiPort> Enumerate(bool inputs)
            {
                var ports = new List<HostMidiPort>();

                if (inputs)
                {
                    uint count = midiInGetNumDevs();
                    uint size = (uint)Marshal.SizeOf<MidiInCaps>();

                    for (uint i = 0; i < count; i++)
                        if (midiInGetDevCaps(i, out MidiInCaps caps, size) == MMSYSERR_NOERROR)
                            ports.Add(new HostMidiPort(caps.szPname, i.ToString()));
                }
                else
                {
                    uint count = midiOutGetNumDevs();
                    uint size = (uint)Marshal.SizeOf<MidiOutCaps>();

                    // Device 0 is normally the "Microsoft GS Wavetable Synth". MIDI_MAPPER
                    // ((uint)-1) is deliberately left out: it is not a device the user picked,
                    // and since Windows Vista it just forwards to device 0 anyway.
                    for (uint i = 0; i < count; i++)
                        if (midiOutGetDevCaps(i, out MidiOutCaps caps, size) == MMSYSERR_NOERROR)
                            ports.Add(new HostMidiPort(caps.szPname, i.ToString()));
                }

                return ports;
            }
        }

        /// <summary>
        /// Linux: the ALSA sequencer, the same thing <c>aconnect -l</c> lists. Enumerating means
        /// walking every client and, inside it, every port, keeping those that can be subscribed
        /// to in the direction we need.
        ///
        /// The library is named by its SONAME (<c>libasound.so.2</c>) instead of the plain
        /// "asound": default probing would try the unversioned <c>libasound.so</c> symlink, which
        /// only exists with the development package — the same trap SDL2 fell into
        /// (see Program.RegisterSdlLibraryResolver).
        /// </summary>
        static class Alsa
        {
            const string Lib = "libasound.so.2";

            const int SND_SEQ_OPEN_DUPLEX = 3;              // OPEN_OUTPUT (1) | OPEN_INPUT (2)
            const uint SND_SEQ_PORT_CAP_READ = 1 << 0;      // the port can be read from...
            const uint SND_SEQ_PORT_CAP_WRITE = 1 << 1;     // ...and written to
            const uint SND_SEQ_PORT_CAP_SUBS_READ = 1 << 5; // ...by an external subscription
            const uint SND_SEQ_PORT_CAP_SUBS_WRITE = 1 << 6;

            [DllImport(Lib)] static extern int snd_seq_open(out IntPtr handle, string name, int streams, int mode);
            [DllImport(Lib)] static extern int snd_seq_close(IntPtr handle);
            [DllImport(Lib)] static extern int snd_seq_client_id(IntPtr handle);

            [DllImport(Lib)] static extern int snd_seq_client_info_malloc(out IntPtr info);
            [DllImport(Lib)] static extern void snd_seq_client_info_free(IntPtr info);
            [DllImport(Lib)] static extern void snd_seq_client_info_set_client(IntPtr info, int client);
            [DllImport(Lib)] static extern int snd_seq_client_info_get_client(IntPtr info);
            [DllImport(Lib)] static extern IntPtr snd_seq_client_info_get_name(IntPtr info);
            [DllImport(Lib)] static extern int snd_seq_query_next_client(IntPtr handle, IntPtr info);

            [DllImport(Lib)] static extern int snd_seq_port_info_malloc(out IntPtr info);
            [DllImport(Lib)] static extern void snd_seq_port_info_free(IntPtr info);
            [DllImport(Lib)] static extern void snd_seq_port_info_set_client(IntPtr info, int client);
            [DllImport(Lib)] static extern void snd_seq_port_info_set_port(IntPtr info, int port);
            [DllImport(Lib)] static extern int snd_seq_port_info_get_port(IntPtr info);
            [DllImport(Lib)] static extern IntPtr snd_seq_port_info_get_name(IntPtr info);
            [DllImport(Lib)] static extern uint snd_seq_port_info_get_capability(IntPtr info);
            [DllImport(Lib)] static extern int snd_seq_query_next_port(IntPtr handle, IntPtr info);

            public static List<HostMidiPort> Enumerate(bool inputs)
            {
                var ports = new List<HostMidiPort>();

                // Reading the port list needs a client of our own; it disappears with the close below.
                if (snd_seq_open(out IntPtr seq, "default", SND_SEQ_OPEN_DUPLEX, 0) < 0)
                    return ports;

                IntPtr clientInfo = IntPtr.Zero;
                IntPtr portInfo = IntPtr.Zero;

                try
                {
                    if (snd_seq_client_info_malloc(out clientInfo) < 0 || snd_seq_port_info_malloc(out portInfo) < 0)
                        return ports;

                    uint wanted = inputs
                        ? SND_SEQ_PORT_CAP_READ | SND_SEQ_PORT_CAP_SUBS_READ
                        : SND_SEQ_PORT_CAP_WRITE | SND_SEQ_PORT_CAP_SUBS_WRITE;

                    int ownClient = snd_seq_client_id(seq);

                    snd_seq_client_info_set_client(clientInfo, -1);   // -1 = start before the first client

                    while (snd_seq_query_next_client(seq, clientInfo) >= 0)
                    {
                        int client = snd_seq_client_info_get_client(clientInfo);

                        // Client 0 is ALSA's own "System" (Timer/Announce, not MIDI) and our
                        // transient client is of no use to anyone either.
                        if (client == 0 || client == ownClient)
                            continue;

                        string clientName = Utf8(snd_seq_client_info_get_name(clientInfo));

                        snd_seq_port_info_set_client(portInfo, client);
                        snd_seq_port_info_set_port(portInfo, -1);

                        while (snd_seq_query_next_port(seq, portInfo) >= 0)
                        {
                            if ((snd_seq_port_info_get_capability(portInfo) & wanted) != wanted)
                                continue;

                            int port = snd_seq_port_info_get_port(portInfo);
                            string portName = Utf8(snd_seq_port_info_get_name(portInfo));

                            // "Client: port", the way aconnect and every ALSA front end shows it.
                            ports.Add(new HostMidiPort($"{clientName}: {portName}", $"{client}:{port}"));
                        }
                    }
                }
                finally
                {
                    if (portInfo != IntPtr.Zero) snd_seq_port_info_free(portInfo);
                    if (clientInfo != IntPtr.Zero) snd_seq_client_info_free(clientInfo);
                    snd_seq_close(seq);
                }

                return ports;
            }

            static string Utf8(IntPtr str) => Marshal.PtrToStringUTF8(str) ?? "";
        }

        /// <summary>
        /// macOS: CoreMIDI. Sources are what we can read from, destinations what we can write to;
        /// both are listed with their display name, which is what the user sees in Audio MIDI Setup.
        /// </summary>
        static class CoreMidi
        {
            const string CoreMidiLib = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
            const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

            const uint kCFStringEncodingUTF8 = 0x08000100;

            // ItemCount is a CFIndex-sized count and MIDIObjectRef a 32-bit integer handle.
            [DllImport(CoreMidiLib)] static extern nuint MIDIGetNumberOfSources();
            [DllImport(CoreMidiLib)] static extern uint MIDIGetSource(nuint index);
            [DllImport(CoreMidiLib)] static extern nuint MIDIGetNumberOfDestinations();
            [DllImport(CoreMidiLib)] static extern uint MIDIGetDestination(nuint index);
            [DllImport(CoreMidiLib)] static extern int MIDIObjectGetStringProperty(uint obj, IntPtr propertyId, out IntPtr value);

            [DllImport(CoreFoundationLib)] static extern IntPtr CFStringGetCStringPtr(IntPtr str, uint encoding);
            [DllImport(CoreFoundationLib)]
            [return: MarshalAs(UnmanagedType.U1)]
            static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);
            [DllImport(CoreFoundationLib)] static extern void CFRelease(IntPtr cf);

            public static List<HostMidiPort> Enumerate(bool inputs)
            {
                var ports = new List<HostMidiPort>();

                IntPtr displayName = DisplayNameProperty();
                nuint count = inputs ? MIDIGetNumberOfSources() : MIDIGetNumberOfDestinations();

                for (nuint i = 0; i < count; i++)
                {
                    uint endpoint = inputs ? MIDIGetSource(i) : MIDIGetDestination(i);
                    if (endpoint == 0)
                        continue;

                    string name = EndpointName(endpoint, displayName);
                    ports.Add(new HostMidiPort(name.Length > 0 ? name : $"MIDI endpoint {endpoint}", endpoint.ToString()));
                }

                return ports;
            }

            /// <summary>
            /// Value of CoreMIDI's <c>kMIDIPropertyDisplayName</c> constant. It is an exported
            /// CFStringRef global, so it is read through the dynamic symbol rather than rebuilt as
            /// an equal CFString: how the property dictionary compares its keys is not part of the
            /// public contract.
            /// </summary>
            static IntPtr DisplayNameProperty()
            {
                IntPtr framework = NativeLibrary.Load(CoreMidiLib);
                IntPtr symbol = NativeLibrary.GetExport(framework, "kMIDIPropertyDisplayName");

                return Marshal.ReadIntPtr(symbol);
            }

            static string EndpointName(uint endpoint, IntPtr property)
            {
                if (MIDIObjectGetStringProperty(endpoint, property, out IntPtr cfName) != 0 || cfName == IntPtr.Zero)
                    return "";

                try
                {
                    // The fast path returns the internal buffer, and is allowed to fail (returns
                    // NULL) when the string is not stored as UTF-8; then it has to be converted.
                    IntPtr utf8 = CFStringGetCStringPtr(cfName, kCFStringEncodingUTF8);
                    if (utf8 != IntPtr.Zero)
                        return Marshal.PtrToStringUTF8(utf8) ?? "";

                    byte[] buffer = new byte[512];
                    if (!CFStringGetCString(cfName, buffer, buffer.Length, kCFStringEncodingUTF8))
                        return "";

                    int length = Array.IndexOf(buffer, (byte)0);
                    return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
                }
                finally
                {
                    // MIDIObjectGetStringProperty hands over a reference of its own.
                    CFRelease(cfName);
                }
            }
        }
    }
}
