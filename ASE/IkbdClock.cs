namespace ASE
{
    /// <summary>
    /// The IKBD's time-of-day clock, behind commands $1B (SET) and $1C (INTERROGATE): six BCD
    /// bytes — year, month, day, hour, minute, second — that the 6301's ROM keeps in its RAM and
    /// counts once a second. Every model has it, and on a machine without the Mega's RP5C15 it is
    /// where EmuTOS and TOS 2.05/2.06 take the date from at boot. TOS 1.04 to 1.62 have the code
    /// to read it there too, behind a branch that is never taken (see HARDWARE-NOTES.md, <i>The
    /// IKBD clock's year</i>), and start on the ROM's date unless the RP5C15 answers.
    /// <para>
    /// The real one starts at zero, with no battery behind it, and TOS keeps its own build date
    /// when it finds the month at zero. Here it is set from the host's local time at power-on,
    /// and again when a snapshot is restored or the time warp goes back, outside a session
    /// (<see cref="SetFromHost"/>); in between it counts emulated time. That makes it machine
    /// state like any other: saved at the end of the "ACIA" section, in the fingerprint, and the
    /// same on every computer of a shared game, whose machines all start from one snapshot.
    /// </para>
    /// <para>
    /// The year byte is not BCD past 1999, on purpose. Atari's TOS (1.02 to 2.06 alike) reads it
    /// as bcd(YY) - 80 into the 7-bit years-since-1980 field of a GEMDOS date, and writes it as
    /// BCD(year - 1980) + $80: it only knows 1980-1999 as valid BCD, so $26 reads as 2054. Its
    /// own encoding of 2026 is $C6, which TOS reads back correctly — and so does EmuTOS, which
    /// takes a year of 80 or more as 19YY with the tens digit weighed in full (12 × 10 + 6 + 1900).
    /// So the host's year goes in as years since 1900, tens digit and all. The 6301 can hold any
    /// byte; only $1B refuses one that is not BCD, as the real IKBD does — which is why setting a
    /// post-1999 date from TOS' control panel leaves the year alone, on hardware and here.
    /// </para>
    /// <para>
    /// Counting follows Hatari's port of the IKBD ROM (<c>IKBD_UpdateClockOnVBL</c>): BCD
    /// digits carried with DAA, no range checks on what $1B stored, a month of zero stops the
    /// clock, and a year of $99 rolls over to $00 — a game ending when the clock reaches the new
    /// century depends on that. Only a year above $99, which nothing but the host can put
    /// there, carries its units into the tens digit instead, so a session running into New Year
    /// keeps a year TOS can read. Emulation thread, or with it stopped.
    /// </para>
    /// </summary>
    public static class IkbdClock
    {
        // Year, month, day, hour, minute, second, in BCD
        static readonly byte[] _clock = new byte[6];

        // Emulated cycles since the last second
        static long _cycles;

        // ASE paces the machine at 8,000,000 cycles per second (ASEMain.EmulatorLoop)
        const long CyclesPerSecond = 8_000_000;

        // Size of what SaveState writes, for the ACIA section to recognise it
        public const int StateSize = 6 + 8;

        public static void SetFromHost(DateTime now)
        {
            int years = Math.Clamp(now.Year - 1900, 80, 159);
            _clock[0] = (byte)((years / 10) << 4 | years % 10);
            _clock[1] = Bcd(now.Month);
            _clock[2] = Bcd(now.Day);
            _clock[3] = Bcd(now.Hour);
            _clock[4] = Bcd(now.Minute);
            _clock[5] = Bcd(now.Second);

            // The next second ticks when the host's does
            _cycles = now.Millisecond * (CyclesPerSecond / 1000);
        }

        static byte Bcd(int value) => (byte)((value / 10) << 4 | value % 10);

        public static void Advance(int cycles)
        {
            _cycles += cycles;
            if (_cycles < CyclesPerSecond)
                return;

            _cycles -= CyclesPerSecond;
            TickSecond();
        }

        /// <summary>$1B: the six bytes after the command. A byte that is not BCD leaves its field
        /// as it was; the others are stored whatever their range.</summary>
        public static void Set(IReadOnlyList<byte> command)
        {
            for (int i = 0; i < 6; i++)
            {
                byte v = command[i + 1];
                if ((v & 0x0F) <= 0x09 && (v & 0xF0) <= 0x90)
                    _clock[i] = v;
            }
        }

        /// <summary>$1C: the $FC packet the IKBD answers with.</summary>
        public static byte[] Packet()
            => [0xFC, _clock[0], _clock[1], _clock[2], _clock[3], _clock[4], _clock[5]];

        static void TickSecond()
        {
            // Days in each month, indexed by the BCD month minus one ($10-$12 at 15-17)
            ReadOnlySpan<byte> dayMax = [0x32, 0x29, 0x32, 0x31, 0x32, 0x31, 0x32, 0x32, 0x31, 0, 0, 0, 0, 0, 0, 0x32, 0x31, 0x32];
            ReadOnlySpan<byte> valMax = [0xFF, 0x13, 0x00, 0x24, 0x60, 0x60];

            if (_clock[1] == 0)
                return;

            for (int i = 5; i >= 0; i--)
            {
                if (i == 0 && _clock[0] > 0x99)
                {
                    _clock[0] = (_clock[0] & 0x0F) == 9 ? (byte)((_clock[0] & 0xF0) + 0x10) : (byte)(_clock[0] + 1);
                    return;
                }

                byte val = DecimalAdjust((byte)(_clock[i] + 1));
                byte max = valMax[i];

                if (i == 2)
                {
                    // A month past $12 was never checked by $1B; the ROM would index past its
                    // table, so it is taken as December
                    int month = Math.Min((int)_clock[1], 0x12);
                    max = dayMax[month - 1];

                    // The ROM's leap-year test, on the BCD year
                    if (_clock[1] == 2)
                    {
                        int year = _clock[0];
                        if ((year & 0x10) != 0)
                            year += 0x0A;
                        if ((year & 0x03) == 0)
                            max = 0x30;
                    }
                }

                if (val != max)
                {
                    _clock[i] = val;
                    return;
                }

                // Day and month start at 1, the time at 0
                _clock[i] = (byte)(i == 1 || i == 2 ? 1 : 0);
            }
        }

        // The 6301's DAA after an increment
        static byte DecimalAdjust(byte v)
        {
            if ((v & 0x0F) > 0x09)
                v += 0x06;
            if ((v & 0xF0) > 0x90)
                v += 0x60;
            return v;
        }

        public static void SaveState(Snapshot.Writer w)
        {
            foreach (byte b in _clock)
                w.U8(b);
            w.I64(_cycles);
        }

        public static void LoadState(Snapshot.Reader r)
        {
            for (int i = 0; i < 6; i++)
                _clock[i] = r.U8();
            _cycles = r.I64();
        }
    }
}
