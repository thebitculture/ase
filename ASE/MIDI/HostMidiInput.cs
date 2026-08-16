using System.Runtime.InteropServices;
using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Feeds a host MIDI input port into the emulated ST's MIDI IN, in
    /// <see cref="ConfigOptions.MIDIEmulationOptions.System"/> mode. The counterpart of
    /// <see cref="HostMidiOutput"/>: same per-platform backends, same open-by-name
    /// resolution, same never-fatal failures.
    ///
    /// Whatever arrives is flattened back to the raw bytes a DIN cable would carry and
    /// handed to the delivery callback (<see cref="MidiAcia.Receive"/>), which does the
    /// 31250-baud pacing — so each backend's only job is capturing events on whatever
    /// thread its platform API uses.
    /// </summary>
    public static class HostMidiInput
    {
        /// <summary>Opens the host input port with the given name, delivering its bytes
        /// to <paramref name="deliver"/>; null (with a console report) on failure.</summary>
        public static IDisposable Open(string portName, Action<byte[]> deliver)
        {
            try
            {
                HostMidiPort port = HostMidi.Inputs().FirstOrDefault(p => p.Name == portName);
                if (port.Id is null)
                {
                    ColoredConsole.WriteLine($"MIDI: configured input port [[red]]{portName}[[/red]] is not present — the ST MIDI IN stays silent.");
                    return null;
                }

                IDisposable input =
                    OperatingSystem.IsWindows() ? new WinMMInput(uint.Parse(port.Id), deliver) :
                    OperatingSystem.IsMacOS() ? new CoreMidiInput(uint.Parse(port.Id), deliver) :
                    OperatingSystem.IsLinux() ? new AlsaInput(port.Id, deliver) :
                    null;

                if (input != null)
                    ColoredConsole.WriteLine($"MIDI: [[green]]{port.Name}[[/green]] -> ST MIDI IN.", ConfigOptions.DebugModes.Quiet);

                return input;
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"MIDI: cannot open input port [[red]]{portName}[[/red]] ({ex.Message}).");
                return null;
            }
        }

        /// <summary>Bytes of a short message, reconstructed from its status byte.</summary>
        static int ShortMessageLength(byte status) => (status & 0xF0) switch
        {
            0xC0 => 2,
            0xD0 => 2,
            0xF0 => status switch
            {
                0xF1 => 2,
                0xF2 => 3,
                0xF3 => 2,
                _ => 1
            },
            _ => 3
        };

        /// <summary>
        /// Windows MME. Short messages arrive packed in MIM_DATA on winmm's callback
        /// thread; SysEx needs prepared buffers the driver fills and returns via
        /// MIM_LONGDATA, re-queued until the port closes.
        /// </summary>
        sealed class WinMMInput : IDisposable
        {
            const uint MMSYSERR_NOERROR = 0;
            const uint CALLBACK_FUNCTION = 0x00030000;
            const uint MIM_DATA = 0x3C3;
            const uint MIM_LONGDATA = 0x3C4;

            const int SYSEX_BUFFERS = 2;
            const int SYSEX_BUFFER_SIZE = 4096;

            delegate void MidiInProc(IntPtr hMidiIn, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

            [StructLayout(LayoutKind.Sequential)]
            struct MIDIHDR
            {
                public IntPtr lpData;
                public uint dwBufferLength;
                public uint dwBytesRecorded;
                public IntPtr dwUser;
                public uint dwFlags;
                public IntPtr lpNext;
                public IntPtr reserved;
                public uint dwOffset;
                public IntPtr dwReserved0, dwReserved1, dwReserved2, dwReserved3,
                              dwReserved4, dwReserved5, dwReserved6, dwReserved7;
            }

            [DllImport("winmm.dll")] static extern uint midiInOpen(out IntPtr hmi, uint deviceId, MidiInProc callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] static extern uint midiInClose(IntPtr hmi);
            [DllImport("winmm.dll")] static extern uint midiInStart(IntPtr hmi);
            [DllImport("winmm.dll")] static extern uint midiInStop(IntPtr hmi);
            [DllImport("winmm.dll")] static extern uint midiInReset(IntPtr hmi);
            [DllImport("winmm.dll")] static extern uint midiInPrepareHeader(IntPtr hmi, IntPtr header, uint headerSize);
            [DllImport("winmm.dll")] static extern uint midiInUnprepareHeader(IntPtr hmi, IntPtr header, uint headerSize);
            [DllImport("winmm.dll")] static extern uint midiInAddBuffer(IntPtr hmi, IntPtr header, uint headerSize);

            static readonly uint HeaderSize = (uint)Marshal.SizeOf<MIDIHDR>();

            IntPtr _handle;
            readonly MidiInProc _callback;      // rooted: the driver holds this function pointer
            readonly Action<byte[]> _deliver;
            readonly IntPtr[] _sysexHeaders = new IntPtr[SYSEX_BUFFERS];
            volatile bool _closing;

            public WinMMInput(uint deviceId, Action<byte[]> deliver)
            {
                _deliver = deliver;
                _callback = OnMessage;

                uint rc = midiInOpen(out _handle, deviceId, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
                if (rc != MMSYSERR_NOERROR)
                    throw new InvalidOperationException($"midiInOpen failed with code {rc}");

                try
                {
                    for (int i = 0; i < SYSEX_BUFFERS; i++)
                    {
                        IntPtr header = Marshal.AllocHGlobal((int)HeaderSize);
                        Marshal.StructureToPtr(new MIDIHDR
                        {
                            lpData = Marshal.AllocHGlobal(SYSEX_BUFFER_SIZE),
                            dwBufferLength = SYSEX_BUFFER_SIZE
                        }, header, false);

                        midiInPrepareHeader(_handle, header, HeaderSize);
                        midiInAddBuffer(_handle, header, HeaderSize);
                        _sysexHeaders[i] = header;
                    }

                    if (midiInStart(_handle) != MMSYSERR_NOERROR)
                        throw new InvalidOperationException("midiInStart failed");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            void OnMessage(IntPtr hMidiIn, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
            {
                try
                {
                    if (msg == MIM_DATA)
                    {
                        // param1 packs the message: status | data1 << 8 | data2 << 16.
                        uint packed = (uint)param1.ToInt64();
                        byte status = (byte)packed;
                        int length = status >= 0xF8 ? 1 : ShortMessageLength(status);

                        var bytes = new byte[length];
                        bytes[0] = status;
                        if (length > 1) bytes[1] = (byte)(packed >> 8);
                        if (length > 2) bytes[2] = (byte)(packed >> 16);

                        _deliver(bytes);
                    }
                    else if (msg == MIM_LONGDATA)
                    {
                        // A SysEx buffer came back. During shutdown midiInReset returns
                        // them empty; they must not be re-queued then.
                        var hdr = Marshal.PtrToStructure<MIDIHDR>(param1);

                        if (hdr.dwBytesRecorded > 0)
                        {
                            var bytes = new byte[hdr.dwBytesRecorded];
                            Marshal.Copy(hdr.lpData, bytes, 0, bytes.Length);
                            _deliver(bytes);
                        }

                        if (!_closing)
                            midiInAddBuffer(_handle, param1, HeaderSize);
                    }
                }
                catch
                {
                    // Nothing may escape into winmm's callback thread.
                }
            }

            public void Dispose()
            {
                if (_handle == IntPtr.Zero)
                    return;

                _closing = true;
                midiInStop(_handle);
                midiInReset(_handle);   // returns the queued SysEx buffers synchronously

                foreach (IntPtr header in _sysexHeaders)
                {
                    if (header == IntPtr.Zero)
                        continue;

                    midiInUnprepareHeader(_handle, header, HeaderSize);
                    Marshal.FreeHGlobal(Marshal.PtrToStructure<MIDIHDR>(header).lpData);
                    Marshal.FreeHGlobal(header);
                }

                midiInClose(_handle);
                _handle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Linux: an ALSA sequencer client with a writable port subscribed to the
        /// source, drained by a polling thread (the client is opened non-blocking so
        /// closing never has to interrupt a blocked read). Events are flattened back to
        /// raw MIDI by ALSA's own snd_midi_event decoder.
        /// </summary>
        sealed unsafe class AlsaInput : IDisposable
        {
            IntPtr _seq;
            IntPtr _decoder;
            readonly Thread _thread;
            readonly Action<byte[]> _deliver;
            volatile bool _closing;

            const int DECODE_BUFFER_SIZE = 65536;
            const int EAGAIN = -11;

            public AlsaInput(string id, Action<byte[]> deliver)
            {
                _deliver = deliver;

                string[] parts = id.Split(':');
                int srcClient = int.Parse(parts[0]);
                int srcPort = int.Parse(parts[1]);

                if (Alsa.snd_seq_open(out _seq, "default", Alsa.SND_SEQ_OPEN_INPUT, Alsa.SND_SEQ_NONBLOCK) < 0)
                    throw new InvalidOperationException("snd_seq_open failed");

                try
                {
                    Alsa.snd_seq_set_client_name(_seq, "ASE");

                    int port = Alsa.snd_seq_create_simple_port(_seq, "MIDI IN",
                        Alsa.SND_SEQ_PORT_CAP_WRITE | Alsa.SND_SEQ_PORT_CAP_SUBS_WRITE,
                        Alsa.SND_SEQ_PORT_TYPE_MIDI_GENERIC | Alsa.SND_SEQ_PORT_TYPE_APPLICATION);
                    if (port < 0)
                        throw new InvalidOperationException("snd_seq_create_simple_port failed");

                    if (Alsa.snd_seq_connect_from(_seq, port, srcClient, srcPort) < 0)
                        throw new InvalidOperationException($"cannot subscribe to {srcClient}:{srcPort}");

                    if (Alsa.snd_midi_event_new(DECODE_BUFFER_SIZE, out _decoder) < 0)
                        throw new InvalidOperationException("snd_midi_event_new failed");

                    Alsa.snd_midi_event_no_status(_decoder, 1);
                }
                catch
                {
                    Dispose();
                    throw;
                }

                _thread = new Thread(ReadLoop) { IsBackground = true, Name = "ASE MIDI IN" };
                _thread.Start();
            }

            void ReadLoop()
            {
                var buffer = new byte[DECODE_BUFFER_SIZE];

                while (!_closing)
                {
                    int rc = Alsa.snd_seq_event_input(_seq, out IntPtr ev);

                    if (rc == EAGAIN)
                    {
                        // A millisecond of poll latency is well under a byte time at
                        // 31250 baud, and the ACIA repaces everything anyway.
                        Thread.Sleep(1);
                        continue;
                    }

                    if (rc < 0)
                        break;      // the sequencer went away (or we are closing)

                    nint n;
                    fixed (byte* p = buffer)
                        n = Alsa.snd_midi_event_decode(_decoder, p, buffer.Length, ev);

                    if (n > 0)
                    {
                        var bytes = new byte[n];
                        Array.Copy(buffer, bytes, (int)n);
                        _deliver(bytes);
                    }
                    // n < 0: an event with no MIDI representation (port notifications
                    // and the like) — skipped.
                }
            }

            public void Dispose()
            {
                _closing = true;

                if (_thread != null && _thread.IsAlive)
                    _thread.Join(500);

                if (_decoder != IntPtr.Zero)
                {
                    Alsa.snd_midi_event_free(_decoder);
                    _decoder = IntPtr.Zero;
                }
                if (_seq != IntPtr.Zero)
                {
                    Alsa.snd_seq_close(_seq);
                    _seq = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// macOS: a CoreMIDI input port connected to the source endpoint. CoreMIDI calls
        /// the read procedure on its own realtime thread with a packet list whose bytes
        /// are already the raw MIDI stream, so they pass straight through. The walk
        /// mirrors the MIDIPacketNext macro, 4-byte alignment on ARM included.
        /// </summary>
        sealed class CoreMidiInput : IDisposable
        {
            const string CoreMidiLib = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

            delegate void MIDIReadProc(IntPtr packetList, IntPtr readProcRefCon, IntPtr srcConnRefCon);

            [DllImport(CoreMidiLib)] static extern int MIDIClientCreate(IntPtr name, IntPtr notifyProc, IntPtr notifyRefCon, out uint client);
            [DllImport(CoreMidiLib)] static extern int MIDIClientDispose(uint client);
            [DllImport(CoreMidiLib)] static extern int MIDIInputPortCreate(uint client, IntPtr portName, MIDIReadProc readProc, IntPtr refCon, out uint port);
            [DllImport(CoreMidiLib)] static extern int MIDIPortDispose(uint port);
            [DllImport(CoreMidiLib)] static extern int MIDIPortConnectSource(uint port, uint source, IntPtr connRefCon);
            [DllImport(CoreMidiLib)] static extern int MIDIPortDisconnectSource(uint port, uint source);

            uint _client;
            uint _port;
            readonly uint _endpoint;
            readonly MIDIReadProc _readProc;    // rooted: CoreMIDI holds this function pointer
            readonly Action<byte[]> _deliver;
            volatile bool _closing;

            public CoreMidiInput(uint endpoint, Action<byte[]> deliver)
            {
                _endpoint = endpoint;
                _deliver = deliver;
                _readProc = OnPackets;

                IntPtr cfName = CoreFoundation.CreateString("ASE");
                try
                {
                    if (MIDIClientCreate(cfName, IntPtr.Zero, IntPtr.Zero, out _client) != 0)
                        throw new InvalidOperationException("MIDIClientCreate failed");
                    if (MIDIInputPortCreate(_client, cfName, _readProc, IntPtr.Zero, out _port) != 0)
                        throw new InvalidOperationException("MIDIInputPortCreate failed");
                    if (MIDIPortConnectSource(_port, endpoint, IntPtr.Zero) != 0)
                        throw new InvalidOperationException("MIDIPortConnectSource failed");
                }
                catch
                {
                    Dispose();
                    throw;
                }
                finally
                {
                    CoreFoundation.Release(cfName);
                }
            }

            void OnPackets(IntPtr packetList, IntPtr readProcRefCon, IntPtr srcConnRefCon)
            {
                if (_closing)
                    return;

                try
                {
                    int numPackets = Marshal.ReadInt32(packetList, 0);
                    IntPtr packet = packetList + 4;

                    // The MIDIPacket structs are pack(4): timestamp at +0, length at +8,
                    // data at +10; the next packet follows the data, rounded up to a
                    // 4-byte boundary on ARM (and not on Intel), like MIDIPacketNext.
                    bool alignToFour = RuntimeInformation.ProcessArchitecture
                        is Architecture.Arm64 or Architecture.Arm;

                    for (int i = 0; i < numPackets; i++)
                    {
                        int length = (ushort)Marshal.ReadInt16(packet, 8);

                        if (length > 0)
                        {
                            var bytes = new byte[length];
                            Marshal.Copy(packet + 10, bytes, 0, length);
                            _deliver(bytes);
                        }

                        long next = packet.ToInt64() + 10 + length;
                        if (alignToFour)
                            next = (next + 3) & ~3L;
                        packet = (IntPtr)next;
                    }
                }
                catch
                {
                    // Nothing may escape into CoreMIDI's realtime thread.
                }
            }

            public void Dispose()
            {
                _closing = true;

                if (_port != 0)
                {
                    MIDIPortDisconnectSource(_port, _endpoint);
                    MIDIPortDispose(_port);
                    _port = 0;
                }
                if (_client != 0)
                {
                    MIDIClientDispose(_client);
                    _client = 0;
                }
            }
        }
    }
}
