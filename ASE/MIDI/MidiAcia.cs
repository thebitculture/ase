/*
 *
 * Atari ST MIDI ACIA (6850) emulation.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// The MIDI ACIA (a second Motorola 6850) at $FFFC04 (control/status) and $FFFC06
    /// (data), clocked at 500 kHz and divided by 16 to MIDI's 31250 baud. Where the
    /// bytes actually go — a host MIDI port, the built-in MT-32, or nowhere — is
    /// <see cref="MidiManager"/>'s business; this class only models the chip, and it is
    /// always live regardless of the configured MIDI mode, exactly like the real ACIA
    /// keeps working on an ST with nothing plugged into its DIN sockets.
    ///
    /// The transmitter is double buffered like the real chip: a write parks the byte in
    /// the data register, which drains into the shift register the moment it is free —
    /// so TDRE comes back almost immediately after a first write, and pollers (TOS's
    /// Bconout(3) among them) run at the real 3125 bytes/s without ever seeing a stuck
    /// flag. Its /IRQ output shares MFP GPIP4 with the keyboard ACIA through
    /// <see cref="AciaIrqLine"/>.
    /// </summary>
    public static class MidiAcia
    {
        static readonly object _syncLock = new();

        // 6850 status register bits
        public const byte ACIA_RDRF = 1 << 0;   // receive data register full
        public const byte ACIA_TDRE = 1 << 1;   // transmit data register empty
        public const byte ACIA_FE = 1 << 4;     // framing error (never set: the stream is clean)
        public const byte ACIA_OVRN = 1 << 5;   // receiver overrun
        public const byte ACIA_IRQ = 1 << 7;    // interrupt request

        // 31250 baud, 10 bits per byte (start + 8 + stop), 8 MHz CPU -> 2560 cycles/byte.
        const int CYCLES_PER_BYTE = 2560;

        static byte _status = ACIA_TDRE;
        static byte _control;

        // Receiver: bytes from the host MIDI IN port wait here until the emulated line
        // delivers them, one per byte time, into the (single) receive data register.
        static readonly Queue<byte> _rxQueue = new();
        const int RX_QUEUE_MAX = 4096;          // >1 s of MIDI: beyond this the sender is broken
        static byte _rxData;
        static int _rxCyclesUntilNext;
        static int _rxCyclesSinceLatch;

        // Transmitter: data register + shift register (the double buffer).
        static byte _txData;
        static bool _txDataFull;
        static int _txShiftCyclesLeft;          // 0 = shift register idle

        static bool RxIrqEnabled => (_control & 0x80) != 0;          // control bit 7
        static bool TxIrqEnabled => (_control & 0x60) == 0x20;       // control bits 6-5 = %01

        public static void Reset()
        {
            lock (_syncLock)
            {
                _rxQueue.Clear();
                _status = ACIA_TDRE;
                _control = 0;
                _rxData = 0;
                _rxCyclesUntilNext = 0;
                _rxCyclesSinceLatch = 0;
                _txDataFull = false;
                _txShiftCyclesLeft = 0;

                UpdateIrq();
            }
        }

        public static byte ReadStatus()
        {
            lock (_syncLock)
            {
                return _status;
            }
        }

        public static byte ReadData()
        {
            lock (_syncLock)
            {
                byte result = _rxData;

                _status &= unchecked((byte)~(ACIA_RDRF | ACIA_OVRN | ACIA_FE));
                _rxCyclesSinceLatch = 0;
                UpdateIrq();

                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine($"[[cyan]]MIDI[[/cyan]] RX read [[green]]${result:X2}[[/green]] (PC=${CPU._moira.PC0:X6})");

                return result;
            }
        }

        public static void WriteControl(byte v)
        {
            lock (_syncLock)
            {
                _control = v;

                // Master reset (divider bits 1-0 = %11): clears the status flags and whatever
                // was latched or waiting to leave; the chip comes up with an empty transmitter.
                if ((v & 0x03) == 0x03)
                {
                    _status = ACIA_TDRE;
                    _rxCyclesUntilNext = 0;
                    _rxCyclesSinceLatch = 0;
                    _txDataFull = false;
                    _txShiftCyclesLeft = 0;

                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                        ColoredConsole.WriteLine("[[cyan]]MIDI[[/cyan]] 6850 master reset");
                }
                else if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                {
                    ColoredConsole.WriteLine(
                        $"[[cyan]]MIDI[[/cyan]] control = [[yellow]]${v:X2}[[/yellow]] " +
                        $"(RX irq {(RxIrqEnabled ? "on" : "off")}, TX irq {(TxIrqEnabled ? "on" : "off")})");
                }

                UpdateIrq();
            }
        }

        public static void WriteData(byte v)
        {
            lock (_syncLock)
            {
                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine($"[[cyan]]MIDI[[/cyan]] TX [[yellow]]${v:X2}[[/yellow]] (PC=${CPU._moira.PC0:X6})");

                // A write always lands in the data register (writing over an unsent byte
                // replaces it, like on the real chip) and clears TDRE until it drains.
                _txData = v;
                _txDataFull = true;
                _status &= unchecked((byte)~ACIA_TDRE);

                if (_txShiftCyclesLeft <= 0)
                    StartShift();

                UpdateIrq();
            }
        }

        /// <summary>
        /// Moves the parked byte into the shift register — i.e. puts it on the wire — and
        /// frees the data register. Delivery to the sink happens here, at the START of the
        /// byte's transmission: the byte time is still honoured (the shift register stays
        /// busy for it), but the host side hears the byte a byte-time earlier, which only
        /// reduces latency. Caller holds _syncLock.
        /// </summary>
        static void StartShift()
        {
            byte b = _txData;
            _txDataFull = false;
            _status |= ACIA_TDRE;
            _txShiftCyclesLeft = CYCLES_PER_BYTE;

            // MidiManager never lets an exception out: this runs inside the CPU bus-write
            // callback, where an escaped exception would kill the process.
            MidiManager.TransmitByte(b);
        }

        /// <summary>
        /// Bytes arriving from the host MIDI IN port (called from the platform backend's
        /// own thread). They queue here and are delivered at line speed by Sync.
        /// </summary>
        public static void Receive(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return;

            lock (_syncLock)
            {
                foreach (byte b in bytes)
                {
                    // A sender this far ahead of a 31250 baud line is not MIDI data worth
                    // keeping; dropping the newest bytes mirrors what the wire would do.
                    if (_rxQueue.Count >= RX_QUEUE_MAX)
                    {
                        if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                            ColoredConsole.WriteLine("[[cyan]]MIDI[[/cyan]] [[red]]RX queue full[[/red]], host byte dropped");
                        return;
                    }

                    _rxQueue.Enqueue(b);
                }
            }
        }

        /// <summary>
        /// Advances the serial line. Called from the emulation loop once per scanline —
        /// far finer than the 2560-cycle byte time, so pacing stays accurate.
        /// </summary>
        public static void Sync(int cycles)
        {
            lock (_syncLock)
            {
                // Transmitter: when the current byte finishes, the parked one (if any)
                // follows it immediately, which is what sustains the full line rate.
                if (_txShiftCyclesLeft > 0)
                {
                    _txShiftCyclesLeft -= cycles;
                    if (_txShiftCyclesLeft <= 0)
                    {
                        _txShiftCyclesLeft = 0;
                        if (_txDataFull)
                        {
                            StartShift();
                            UpdateIrq();
                        }
                    }
                }

                // Receiver.
                if ((_status & ACIA_RDRF) != 0)
                {
                    _rxCyclesSinceLatch += cycles;

                    // Same rescue as the keyboard ACIA (see ACIA.Sync for the full story):
                    // the shared GPIP4 line is edge sensitive at the MFP, so a byte latched
                    // while the keyboard was holding the line low — or a pending bit the
                    // program cleared without reading the data — would strand the receiver
                    // forever. After a whole byte time with the register still full and the
                    // channel neither pending nor in service, re-assert the request. Unlike
                    // the keyboard path there is no retire-on-read bookkeeping: a spurious
                    // MIDI interrupt just finds RDRF clear and returns, it cannot
                    // desynchronise a packet parser.
                    if (_rxCyclesSinceLatch >= CYCLES_PER_BYTE && RxIrqEnabled
                        && (ASEMain._mfp.IPRB & MFP68901.RegB.ACIA) == 0
                        && (ASEMain._mfp.ISRB & MFP68901.RegB.ACIA) == 0)
                    {
                        ASEMain._mfp.SetInterruptPending(MFP68901.RegB.ACIA, true);
                        _rxCyclesSinceLatch = 0;
                    }

                    if (_rxQueue.Count == 0)
                    {
                        _rxCyclesUntilNext = 0;
                        return;
                    }

                    // The line keeps delivering whether or not the program reads: a byte
                    // completed while the register is still full is lost and OVRN set,
                    // like on the real 6850. This is also what drains the queue when the
                    // ST ignores its MIDI IN entirely.
                    _rxCyclesUntilNext -= cycles;
                    if (_rxCyclesUntilNext <= 0)
                    {
                        _rxQueue.Dequeue();
                        _status |= ACIA_OVRN;
                        _rxCyclesUntilNext = CYCLES_PER_BYTE;
                        UpdateIrq();

                        if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                            ColoredConsole.WriteLine($"[[cyan]]MIDI[[/cyan]] [[red]]overrun[[/red]] byte dropped (queue={_rxQueue.Count})");
                    }
                    return;
                }

                if (_rxQueue.Count == 0)
                {
                    _rxCyclesUntilNext = 0;
                    return;
                }

                _rxCyclesUntilNext -= cycles;
                if (_rxCyclesUntilNext <= 0)
                {
                    _rxData = _rxQueue.Dequeue();
                    _status |= ACIA_RDRF;
                    _rxCyclesSinceLatch = 0;
                    _rxCyclesUntilNext = CYCLES_PER_BYTE;
                    UpdateIrq();

                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                        ColoredConsole.WriteLine($"[[cyan]]MIDI[[/cyan]] RX [[green]]${_rxData:X2}[[/green]] -> IRQ (queue={_rxQueue.Count})");
                }
            }
        }

        /// <summary>
        /// Recomputes the level-sensitive /IRQ output from the status flags and the control
        /// register's enables, mirrors it into status bit 7 and onto the shared GPIP4 line.
        /// Caller holds _syncLock.
        /// </summary>
        static void UpdateIrq()
        {
            bool irq = (RxIrqEnabled && (_status & (ACIA_RDRF | ACIA_OVRN)) != 0)
                    || (TxIrqEnabled && (_status & ACIA_TDRE) != 0);

            if (irq) _status |= ACIA_IRQ;
            else _status &= unchecked((byte)~ACIA_IRQ);

            AciaIrqLine.SetMidi(irq);
        }

        // ==================== Snapshot ====================

        public static void SaveState(Snapshot.Writer w)
        {
            lock (_syncLock)
            {
                w.U8(_status);
                w.U8(_control);
                w.U8(_rxData);
                w.U8(_txData);
                w.Bool(_txDataFull);
                w.I32(_txShiftCyclesLeft);
                w.I32(_rxCyclesUntilNext);
                // The host-side receive queue is transient (it belongs to the session's
                // MIDI devices, not to the machine) and is not stored.
            }
        }

        public static void LoadState(Snapshot.Reader r)
        {
            lock (_syncLock)
            {
                _status = r.U8();
                _control = r.U8();
                _rxData = r.U8();
                _txData = r.U8();
                _txDataFull = r.Bool();
                _txShiftCyclesLeft = r.I32();
                _rxCyclesUntilNext = r.I32();

                _rxQueue.Clear();
                _rxCyclesSinceLatch = 0;

                UpdateIrq();
            }
        }
    }
}
