using System.Runtime.InteropServices;
using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Sends the emulated ST's MIDI OUT to a host MIDI port, in
    /// <see cref="ConfigOptions.MIDIEmulationOptions.System"/> mode. One backend per
    /// platform, mirroring <see cref="HostMidi"/>'s enumeration: winmm on Windows, the
    /// ALSA sequencer on Linux, CoreMIDI on macOS. The configured port *name* is
    /// resolved against a fresh enumeration at open time, since the platform handles
    /// shift as devices come and go.
    ///
    /// Failures never stop the machine: the port simply stays unconnected (Open returns
    /// null after reporting why), like a DIN cable to nowhere.
    /// </summary>
    public static class HostMidiOutput
    {
        /// <summary>Opens the host output port with the given name, or null (with a
        /// console report) when it is missing or cannot be opened.</summary>
        public static IMidiOutput Open(string portName)
        {
            try
            {
                HostMidiPort port = HostMidi.Outputs().FirstOrDefault(p => p.Name == portName);
                if (port.Id is null)
                {
                    ColoredConsole.WriteLine($"MIDI: configured output port [[red]]{portName}[[/red]] is not present — the ST MIDI OUT goes nowhere.");
                    return null;
                }

                IMidiOutput output =
                    OperatingSystem.IsWindows() ? new WinMMOutput(uint.Parse(port.Id), port.Name) :
                    OperatingSystem.IsMacOS() ? new CoreMidiOutput(uint.Parse(port.Id), port.Name) :
                    OperatingSystem.IsLinux() ? new AlsaOutput(port.Id, port.Name) :
                    null;

                if (output != null)
                    ColoredConsole.WriteLine($"MIDI: ST MIDI OUT -> [[green]]{port.Name}[[/green]].", ConfigOptions.DebugModes.Quiet);

                return output;
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"MIDI: cannot open output port [[red]]{portName}[[/red]] ({ex.Message}).");
                return null;
            }
        }

        /// <summary>
        /// Windows MME. Short messages go out with midiOutShortMsg; SysEx needs a
        /// prepared MIDIHDR handed to midiOutLongMsg, which the driver returns
        /// asynchronously — finished buffers are reaped on later calls instead of
        /// blocking the emulation thread while a hardware port clocks the dump out.
        /// </summary>
        sealed class WinMMOutput : IMidiOutput
        {
            const uint MMSYSERR_NOERROR = 0;
            const uint MHDR_DONE = 1;

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

            [DllImport("winmm.dll")] static extern uint midiOutOpen(out IntPtr hmo, uint deviceId, IntPtr callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr hmo);
            [DllImport("winmm.dll")] static extern uint midiOutReset(IntPtr hmo);
            [DllImport("winmm.dll")] static extern uint midiOutShortMsg(IntPtr hmo, uint msg);
            [DllImport("winmm.dll")] static extern uint midiOutLongMsg(IntPtr hmo, IntPtr header, uint headerSize);
            [DllImport("winmm.dll")] static extern uint midiOutPrepareHeader(IntPtr hmo, IntPtr header, uint headerSize);
            [DllImport("winmm.dll")] static extern uint midiOutUnprepareHeader(IntPtr hmo, IntPtr header, uint headerSize);

            IntPtr _handle;
            readonly List<IntPtr> _inFlight = new();    // MIDIHDRs the driver still owns
            static readonly uint HeaderSize = (uint)Marshal.SizeOf<MIDIHDR>();

            public string Description { get; }

            public WinMMOutput(uint deviceId, string name)
            {
                Description = name;

                uint rc = midiOutOpen(out _handle, deviceId, IntPtr.Zero, IntPtr.Zero, 0);
                if (rc != MMSYSERR_NOERROR)
                    throw new InvalidOperationException($"midiOutOpen failed with code {rc}");
            }

            public void ShortMessage(byte status, byte data1, byte data2, int length)
                => midiOutShortMsg(_handle, (uint)status | ((uint)data1 << 8) | ((uint)data2 << 16));

            public void RealTime(byte status)
                => midiOutShortMsg(_handle, status);

            public void SysEx(byte[] message)
            {
                Reap();

                IntPtr data = Marshal.AllocHGlobal(message.Length);
                Marshal.Copy(message, 0, data, message.Length);

                // The header must sit at a stable address while the driver owns it, so it
                // lives in unmanaged memory too.
                IntPtr header = Marshal.AllocHGlobal((int)HeaderSize);
                var hdr = new MIDIHDR
                {
                    lpData = data,
                    dwBufferLength = (uint)message.Length,
                    dwBytesRecorded = (uint)message.Length
                };
                Marshal.StructureToPtr(hdr, header, false);

                if (midiOutPrepareHeader(_handle, header, HeaderSize) == MMSYSERR_NOERROR
                    && midiOutLongMsg(_handle, header, HeaderSize) == MMSYSERR_NOERROR)
                {
                    _inFlight.Add(header);
                }
                else
                {
                    midiOutUnprepareHeader(_handle, header, HeaderSize);
                    FreeHeader(header);
                }
            }

            /// <summary>Releases the SysEx buffers the driver has finished with.</summary>
            void Reap()
            {
                for (int i = _inFlight.Count - 1; i >= 0; i--)
                {
                    var hdr = Marshal.PtrToStructure<MIDIHDR>(_inFlight[i]);
                    if ((hdr.dwFlags & MHDR_DONE) == 0)
                        continue;

                    midiOutUnprepareHeader(_handle, _inFlight[i], HeaderSize);
                    FreeHeader(_inFlight[i]);
                    _inFlight.RemoveAt(i);
                }
            }

            static void FreeHeader(IntPtr header)
            {
                Marshal.FreeHGlobal(Marshal.PtrToStructure<MIDIHDR>(header).lpData);
                Marshal.FreeHGlobal(header);
            }

            public void Dispose()
            {
                if (_handle == IntPtr.Zero)
                    return;

                // Reset returns any in-flight long buffers (marked done) immediately.
                midiOutReset(_handle);
                Reap();

                // Anything still not returned leaks its few bytes rather than risking a
                // use-after-free in the driver; it should never happen after a reset.
                _inFlight.Clear();

                midiOutClose(_handle);
                _handle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Linux: an ALSA sequencer client of our own ("ASE") with a readable port,
        /// subscribed to the destination. The raw MIDI bytes are turned back into
        /// sequencer events by ALSA's own snd_midi_event encoder, which handles every
        /// message type (SysEx included) without hand-building event structs.
        /// </summary>
        sealed unsafe class AlsaOutput : IMidiOutput
        {
            public string Description { get; }

            IntPtr _seq;
            IntPtr _encoder;
            readonly int _port;

            public AlsaOutput(string id, string name)
            {
                Description = name;

                // "client:port", the form HostMidi.Enumerate builds.
                string[] parts = id.Split(':');
                int destClient = int.Parse(parts[0]);
                int destPort = int.Parse(parts[1]);

                if (Alsa.snd_seq_open(out _seq, "default", Alsa.SND_SEQ_OPEN_OUTPUT, 0) < 0)
                    throw new InvalidOperationException("snd_seq_open failed");

                try
                {
                    Alsa.snd_seq_set_client_name(_seq, "ASE");

                    _port = Alsa.snd_seq_create_simple_port(_seq, "MIDI OUT",
                        Alsa.SND_SEQ_PORT_CAP_READ | Alsa.SND_SEQ_PORT_CAP_SUBS_READ,
                        Alsa.SND_SEQ_PORT_TYPE_MIDI_GENERIC | Alsa.SND_SEQ_PORT_TYPE_APPLICATION);
                    if (_port < 0)
                        throw new InvalidOperationException("snd_seq_create_simple_port failed");

                    if (Alsa.snd_seq_connect_to(_seq, _port, destClient, destPort) < 0)
                        throw new InvalidOperationException($"cannot subscribe to {destClient}:{destPort}");

                    if (Alsa.snd_midi_event_new(65536, out _encoder) < 0)
                        throw new InvalidOperationException("snd_midi_event_new failed");

                    // The parser already resolved running status; keep the event stream explicit.
                    Alsa.snd_midi_event_no_status(_encoder, 1);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void ShortMessage(byte status, byte data1, byte data2, int length)
            {
                byte* buf = stackalloc byte[3] { status, data1, data2 };
                SendBytes(buf, length);
            }

            public void RealTime(byte status) => SendBytes(&status, 1);

            public void SysEx(byte[] message)
            {
                fixed (byte* p = message)
                    SendBytes(p, message.Length);
            }

            /// <summary>
            /// Feeds raw MIDI bytes to the encoder and ships every event it completes.
            /// A SysEx larger than the encoder buffer comes out as several consecutive
            /// SYSEX events, which is the sequencer's native way of fragmenting them.
            /// </summary>
            void SendBytes(byte* bytes, int length)
            {
                int offset = 0;
                while (offset < length)
                {
                    var ev = new Alsa.SndSeqEvent();
                    nint consumed = Alsa.snd_midi_event_encode(_encoder, bytes + offset, length - offset, ref ev);
                    if (consumed <= 0)
                        break;      // encoder refused the byte: nothing more we can do

                    offset += (int)consumed;

                    if (ev.type == Alsa.SND_SEQ_EVENT_NONE)
                        continue;   // message not complete yet

                    ev.sourcePort = (byte)_port;
                    ev.destClient = Alsa.SND_SEQ_ADDRESS_SUBSCRIBERS;
                    ev.destPort = Alsa.SND_SEQ_ADDRESS_UNKNOWN;
                    ev.queue = Alsa.SND_SEQ_QUEUE_DIRECT;

                    Alsa.snd_seq_event_output(_seq, ref ev);
                    Alsa.snd_seq_drain_output(_seq);
                }
            }

            public void Dispose()
            {
                if (_encoder != IntPtr.Zero)
                {
                    Alsa.snd_midi_event_free(_encoder);
                    _encoder = IntPtr.Zero;
                }
                if (_seq != IntPtr.Zero)
                {
                    Alsa.snd_seq_close(_seq);   // takes the port and subscriptions with it
                    _seq = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// macOS: a CoreMIDI client with an output port; each message is wrapped in a
        /// single-packet MIDIPacketList (built by hand — the variable-length C structs
        /// don't marshal) and handed to MIDISend, which copies it.
        /// </summary>
        sealed class CoreMidiOutput : IMidiOutput
        {
            const string CoreMidiLib = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

            [DllImport(CoreMidiLib)] static extern int MIDIClientCreate(IntPtr name, IntPtr notifyProc, IntPtr notifyRefCon, out uint client);
            [DllImport(CoreMidiLib)] static extern int MIDIClientDispose(uint client);
            [DllImport(CoreMidiLib)] static extern int MIDIOutputPortCreate(uint client, IntPtr portName, out uint port);
            [DllImport(CoreMidiLib)] static extern int MIDIPortDispose(uint port);
            [DllImport(CoreMidiLib)] static extern int MIDISend(uint port, uint dest, IntPtr packetList);

            uint _client;
            uint _port;
            readonly uint _endpoint;
            IntPtr _packetBuf;
            int _packetBufSize;

            // MIDIPacketList: UInt32 numPackets, then the packet — MIDITimeStamp (8),
            // UInt16 length, then the bytes (the structs are pack(4), so no padding here).
            const int PACKET_DATA_OFFSET = 4 + 8 + 2;

            public string Description { get; }

            public CoreMidiOutput(uint endpoint, string name)
            {
                Description = name;
                _endpoint = endpoint;

                IntPtr cfName = CoreFoundation.CreateString("ASE");
                try
                {
                    if (MIDIClientCreate(cfName, IntPtr.Zero, IntPtr.Zero, out _client) != 0)
                        throw new InvalidOperationException("MIDIClientCreate failed");
                    if (MIDIOutputPortCreate(_client, cfName, out _port) != 0)
                        throw new InvalidOperationException("MIDIOutputPortCreate failed");
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

            public void ShortMessage(byte status, byte data1, byte data2, int length)
            {
                Span<byte> msg = stackalloc byte[3] { status, data1, data2 };
                Send(msg[..length]);
            }

            public void RealTime(byte status)
            {
                Span<byte> msg = stackalloc byte[1] { status };
                Send(msg);
            }

            public void SysEx(byte[] message)
            {
                // CoreMIDI continues a SysEx across packets/sends on the same port, so a
                // dump longer than a packet's 16-bit length simply goes out in slices.
                const int chunk = 8192;
                for (int off = 0; off < message.Length; off += chunk)
                    Send(message.AsSpan(off, Math.Min(chunk, message.Length - off)));
            }

            void Send(ReadOnlySpan<byte> bytes)
            {
                int needed = PACKET_DATA_OFFSET + bytes.Length;
                if (_packetBuf == IntPtr.Zero || _packetBufSize < needed)
                {
                    if (_packetBuf != IntPtr.Zero) Marshal.FreeHGlobal(_packetBuf);
                    _packetBufSize = Math.Max(needed, 64);
                    _packetBuf = Marshal.AllocHGlobal(_packetBufSize);
                }

                Marshal.WriteInt32(_packetBuf, 0, 1);                       // numPackets
                Marshal.WriteInt64(_packetBuf, 4, 0);                       // timeStamp: now
                Marshal.WriteInt16(_packetBuf, 12, (short)bytes.Length);    // length

                unsafe
                {
                    bytes.CopyTo(new Span<byte>((byte*)_packetBuf + PACKET_DATA_OFFSET, bytes.Length));
                }

                MIDISend(_port, _endpoint, _packetBuf);
            }

            public void Dispose()
            {
                if (_port != 0) { MIDIPortDispose(_port); _port = 0; }
                if (_client != 0) { MIDIClientDispose(_client); _client = 0; }
                if (_packetBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_packetBuf); _packetBuf = IntPtr.Zero; }
            }
        }
    }
}
