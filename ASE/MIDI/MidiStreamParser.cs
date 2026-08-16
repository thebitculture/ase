using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Reassembles the raw byte stream leaving the MIDI ACIA into complete messages for
    /// an <see cref="IMidiOutput"/>. The ST software side is free to use every serial
    /// trick — running status (a data byte stream reusing the last status), system
    /// real-time bytes dropped in the middle of another message, multi-kilobyte SysEx
    /// dumps — and the backend APIs underneath all want whole messages, so the framing
    /// happens once, here.
    ///
    /// Not thread-safe: it is fed exclusively from the emulation thread
    /// (<see cref="MidiManager.TransmitByte"/>).
    /// </summary>
    public sealed class MidiStreamParser
    {
        readonly IMidiOutput _output;

        // Current channel/system-common message being collected.
        byte _status;                // 0 = none
        int _expected;               // total bytes of the running message (1-3)
        byte _data1;
        int _dataCount;

        // System-exclusive collection. MT-32 memory dumps run to a few KB; anything past
        // this cap is a corrupt stream, not music.
        readonly List<byte> _sysex = new();
        bool _inSysex;
        const int SYSEX_MAX = 128 * 1024;

        public MidiStreamParser(IMidiOutput output)
        {
            _output = output;
        }

        public void Feed(byte b)
        {
            // System real-time ($F8-$FF): forwarded at once, transparent to any message
            // (including a SysEx) it happens to interrupt.
            if (b >= 0xF8)
            {
                _output.RealTime(b);
                return;
            }

            if (b == 0xF0)
            {
                // A new SysEx aborts whatever was being collected.
                AbortSysexIfOpen("a new $F0");
                _status = 0;
                _inSysex = true;
                _sysex.Clear();
                _sysex.Add(0xF0);
                return;
            }

            if (b == 0xF7)
            {
                if (_inSysex)
                {
                    _sysex.Add(0xF7);
                    _inSysex = false;
                    _output.SysEx(_sysex.ToArray());

                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                        ColoredConsole.WriteLine($"[[cyan]]MIDI[[/cyan]] SysEx sent ({_sysex.Count} bytes)");

                    _sysex.Clear();
                }
                // A stray EOX outside a SysEx is ignored, like every receiver does.
                return;
            }

            if (b >= 0x80)
            {
                // Any status byte other than real-time terminates an unfinished SysEx —
                // and since the terminator was not $F7 the dump is incomplete, so it is
                // dropped rather than handed over half-built.
                AbortSysexIfOpen($"status ${b:X2}");

                _expected = ExpectedLength(b);
                _dataCount = 0;

                if (_expected == 1)
                {
                    // Single-byte system common ($F6 Tune Request; $F4/$F5 undefined).
                    _output.ShortMessage(b, 0, 0, 1);
                    _status = 0;    // system common always cancels running status
                }
                else
                {
                    _status = b;
                }
                return;
            }

            // Data byte.
            if (_inSysex)
            {
                if (_sysex.Count < SYSEX_MAX)
                    _sysex.Add(b);
                return;
            }

            if (_status == 0)
                return;     // stray data with no status to attach it to

            if (_dataCount == 0 && _expected == 3)
            {
                _data1 = b;
                _dataCount = 1;
                return;
            }

            // Message complete.
            if (_expected == 3)
                _output.ShortMessage(_status, _data1, b, 3);
            else
                _output.ShortMessage(_status, b, 0, 2);

            if (_status >= 0xF0)
                _status = 0;    // system common: no running status
            else
                _dataCount = 0; // channel message: keep the status for running-status data
        }

        void AbortSysexIfOpen(string reason)
        {
            if (!_inSysex)
                return;

            _inSysex = false;

            if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                ColoredConsole.WriteLine($"[[cyan]]MIDI[[/cyan]] unterminated SysEx ({_sysex.Count} bytes) dropped by {reason}");

            _sysex.Clear();
        }

        /// <summary>Total length in bytes of the message the given status byte opens.</summary>
        static int ExpectedLength(byte status) => (status & 0xF0) switch
        {
            0xC0 => 2,              // program change
            0xD0 => 2,              // channel pressure
            0xF0 => status switch
            {
                0xF1 => 2,          // MTC quarter frame
                0xF2 => 3,          // song position pointer
                0xF3 => 2,          // song select
                _ => 1              // $F4/$F5 undefined, $F6 tune request
            },
            _ => 3                  // note on/off, poly pressure, control change, pitch bend
        };
    }
}
