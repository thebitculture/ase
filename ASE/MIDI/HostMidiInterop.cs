using System.Runtime.InteropServices;

namespace ASE
{
    /// <summary>
    /// ALSA sequencer bindings shared by <see cref="HostMidiOutput"/> and
    /// <see cref="HostMidiInput"/> (the enumeration in <see cref="HostMidi"/> keeps its
    /// own private copy of the read-only subset it needs). Named by SONAME
    /// (<c>libasound.so.2</c>) for the same reason explained there: the bare
    /// <c>libasound.so</c> symlink only exists with the development package.
    /// </summary>
    internal static unsafe class Alsa
    {
        const string Lib = "libasound.so.2";

        public const int SND_SEQ_OPEN_OUTPUT = 1;
        public const int SND_SEQ_OPEN_INPUT = 2;
        public const int SND_SEQ_NONBLOCK = 1;              // 'mode' argument of snd_seq_open

        public const uint SND_SEQ_PORT_CAP_READ = 1 << 0;
        public const uint SND_SEQ_PORT_CAP_WRITE = 1 << 1;
        public const uint SND_SEQ_PORT_CAP_SUBS_READ = 1 << 5;
        public const uint SND_SEQ_PORT_CAP_SUBS_WRITE = 1 << 6;

        public const uint SND_SEQ_PORT_TYPE_MIDI_GENERIC = 1 << 1;
        public const uint SND_SEQ_PORT_TYPE_APPLICATION = 1 << 20;

        public const byte SND_SEQ_EVENT_NONE = 66;          // encoder: "message not complete yet"
        public const byte SND_SEQ_ADDRESS_SUBSCRIBERS = 254;
        public const byte SND_SEQ_ADDRESS_UNKNOWN = 253;
        public const byte SND_SEQ_QUEUE_DIRECT = 253;       // bypass the queues, deliver now

        /// <summary>
        /// snd_seq_event_t (28 bytes): type/flags/tag/queue, an 8-byte timestamp union,
        /// source and destination addresses, and a 12-byte data union of which only the
        /// variable-length (SysEx) view is spelled out — the encoder/decoder fill and
        /// read the rest. The C struct packs the ext pointer unaligned at offset 20,
        /// hence the explicit layout with Pack = 1.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 28, Pack = 1)]
        public struct SndSeqEvent
        {
            [FieldOffset(0)] public byte type;
            [FieldOffset(1)] public byte flags;
            [FieldOffset(2)] public byte tag;
            [FieldOffset(3)] public byte queue;
            [FieldOffset(4)] public uint timeTick;
            [FieldOffset(8)] public uint timeSecOrNsec;
            [FieldOffset(12)] public byte sourceClient;
            [FieldOffset(13)] public byte sourcePort;
            [FieldOffset(14)] public byte destClient;
            [FieldOffset(15)] public byte destPort;
            [FieldOffset(16)] public uint extLen;
            [FieldOffset(20)] public IntPtr extPtr;
        }

        [DllImport(Lib)] public static extern int snd_seq_open(out IntPtr handle, string name, int streams, int mode);
        [DllImport(Lib)] public static extern int snd_seq_close(IntPtr handle);
        [DllImport(Lib)] public static extern int snd_seq_set_client_name(IntPtr handle, string name);
        [DllImport(Lib)] public static extern int snd_seq_create_simple_port(IntPtr handle, string name, uint caps, uint type);
        [DllImport(Lib)] public static extern int snd_seq_connect_to(IntPtr handle, int myPort, int destClient, int destPort);
        [DllImport(Lib)] public static extern int snd_seq_connect_from(IntPtr handle, int myPort, int srcClient, int srcPort);
        [DllImport(Lib)] public static extern int snd_seq_event_output(IntPtr handle, ref SndSeqEvent ev);
        [DllImport(Lib)] public static extern int snd_seq_drain_output(IntPtr handle);

        /// <summary>Fetches the next incoming event (a pointer into ALSA's own buffer,
        /// valid until the next call). Returns -EAGAIN (-11) in non-blocking mode when
        /// there is nothing to read.</summary>
        [DllImport(Lib)] public static extern int snd_seq_event_input(IntPtr handle, out IntPtr ev);

        [DllImport(Lib)] public static extern int snd_midi_event_new(nuint bufSize, out IntPtr dev);
        [DllImport(Lib)] public static extern void snd_midi_event_free(IntPtr dev);
        [DllImport(Lib)] public static extern void snd_midi_event_no_status(IntPtr dev, int on);
        [DllImport(Lib)] public static extern nint snd_midi_event_encode(IntPtr dev, byte* buf, nint count, ref SndSeqEvent ev);
        [DllImport(Lib)] public static extern nint snd_midi_event_decode(IntPtr dev, byte* buf, nint count, IntPtr ev);
    }

    /// <summary>Just enough CoreFoundation to hand CFStrings to CoreMIDI.</summary>
    internal static class CoreFoundation
    {
        const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const uint kCFStringEncodingUTF8 = 0x08000100;

        [DllImport(Lib)] static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string str, uint encoding);
        [DllImport(Lib)] static extern void CFRelease(IntPtr cf);

        public static IntPtr CreateString(string s) => CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

        public static void Release(IntPtr cf)
        {
            if (cf != IntPtr.Zero)
                CFRelease(cf);
        }
    }
}
