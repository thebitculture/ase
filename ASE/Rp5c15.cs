using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// The Mega ST's battery-backed clock: a Ricoh RP5C15 on the odd bytes of $FFFC21-$FFFC3F,
    /// one 4-bit register each, sixteen of them in two banks. Bank 0 is the time, a BCD digit per
    /// register from the seconds to the tens of years; bank 1 holds the alarm (its pin is not
    /// connected on the Mega), the 12/24-hour select and the leap-year counter. The mode, test and
    /// reset registers answer in both. Semantics from Ricoh's datasheet as MAME's rp5c15.cpp
    /// reads it; layout as TOS and EmuTOS drive it.
    /// <para>
    /// Always fitted in the Mega model, and in an ST or STE when the configuration says so
    /// (<c>RealTimeClock</c>, off by default). TOS probes for it on every machine — mode $09
    /// into $FFFC3B, $0A05 MOVEPed into the alarm minutes at $FFFC25/27, read back under $0F0F —
    /// and at boot it is the only clock TOS 1.0x and 1.6x read: their path to the IKBD's
    /// (<see cref="IkbdClock"/>) is never taken, so without this chip they start on the ROM's
    /// date. EmuTOS runs the same probe and names the machine after it: an ST that answers is an
    /// "Atari Mega ST" on its welcome screen (the _MCH cookie is ST either way, and an STE stays
    /// an STE) — which is why the switch is off unless asked for, leaving the empty socket
    /// <see cref="Memory"/> serves there.
    /// </para>
    /// <para>
    /// The year register counts from 1980, the convention TOS and EmuTOS share, and the leap
    /// counter is 0 in a leap year. Set from the host's local time at power-on, and again when a
    /// snapshot is restored or the time warp goes back, outside a session
    /// (<see cref="SetFromHost"/>); in between it counts emulated time, so it is machine state:
    /// snapshot section "RTC ", the fingerprint, the same on every computer of a shared game.
    /// Emulation thread, or with it stopped.
    /// </para>
    /// </summary>
    public static class Rp5c15
    {
        public static bool Fitted => ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.Mega
                                  || ConfigOptions.RunninConfig.RealTimeClock;

        // Bank 0
        const int Sec1 = 0, Min1 = 2, Hour1 = 4, Hour10 = 5, Weekday = 6, Day1 = 7, Month1 = 9, Year1 = 11;
        // Bank 1
        const int ClockOut = 0, Adjust = 1, Select24 = 10, LeapYear = 11;
        // Both banks
        const int Mode = 13, Test = 14, Reset = 15;

        const byte ModeBank1 = 0x01;
        const byte ModeTimerEnable = 0x08;
        const byte ResetAlarm = 0x01;
        const byte ResetTimer = 0x02;

        // The bits each register holds; bank 1's ADJUST and its two unused registers hold none
        static readonly byte[] Bank0Mask = [0xF, 0x7, 0xF, 0x7, 0xF, 0x3, 0x7, 0xF, 0x3, 0xF, 0x1, 0xF, 0xF];
        static readonly byte[] Bank1Mask = [0x7, 0x0, 0xF, 0x7, 0xF, 0x3, 0x7, 0xF, 0x3, 0x0, 0x1, 0x3, 0x0];

        static readonly byte[] _time = new byte[13];
        static readonly byte[] _bank1 = new byte[13];
        static byte _mode;

        // Emulated cycles since the last second: the 32.768 kHz divider, which TIMER RESET clears
        static long _cycles;

        // ASE paces the machine at 8,000,000 cycles per second (ASEMain.EmulatorLoop)
        const long CyclesPerSecond = 8_000_000;

        /// <summary>
        /// The host's local time into the time counters and the leap counter. At power-on the
        /// rest is set too, to a Mega whose clock TOS has set: 24 hours, timer running, bank 0, no
        /// alarm. Otherwise it is left as the program has it, the hours following its 12/24 choice.
        /// </summary>
        public static void SetFromHost(DateTime now, bool powerOn)
        {
            if (powerOn)
            {
                Array.Clear(_bank1);
                _bank1[ClockOut] = 1;
                _bank1[Select24] = 1;
                _mode = ModeTimerEnable;
            }

            int years = Math.Clamp(now.Year - 1980, 0, 99);
            Put(Year1, years);
            _bank1[LeapYear] = (byte)(years & 3);
            Put(Month1, now.Month);
            Put(Day1, now.Day);
            _time[Weekday] = (byte)now.DayOfWeek;
            PutHour(now.Hour);
            Put(Min1, now.Minute);
            Put(Sec1, now.Second);

            // The next second ticks when the host's does
            _cycles = now.Millisecond * (CyclesPerSecond / 1000);
        }

        /// <summary>One of the odd bytes of $FFFC21-$FFFC3F. The chip drives D0-D3 only; the upper
        /// half of the byte floats high, as the empty socket does.</summary>
        public static byte Read(uint addr)
        {
            int reg = (int)((addr & 0x1F) >> 1);

            byte v = reg switch
            {
                Mode => _mode,
                Test or Reset => 0,     // write-only
                _ => (_mode & ModeBank1) == 0 ? _time[reg] : _bank1[reg],
            };

            return (byte)(0xF0 | v);
        }

        public static void Write(uint addr, byte v)
        {
            int reg = (int)((addr & 0x1F) >> 1);
            v &= 0x0F;

            switch (reg)
            {
                case Mode:
                    _mode = v;
                    break;

                // The test bits speed the counters up for factory testing; nothing on the Mega
                // sets them
                case Test:
                    break;

                // The 16 Hz and 1 Hz bits gate pulses onto the alarm pin, which goes nowhere
                case Reset:
                    if ((v & ResetAlarm) != 0)
                        Array.Clear(_bank1, 2, 7);  // alarm minutes to tens of days
                    if ((v & ResetTimer) != 0)
                        _cycles = 0;
                    break;

                default:
                    if ((_mode & ModeBank1) == 0)
                        _time[reg] = (byte)(v & Bank0Mask[reg]);
                    else if (reg == Adjust)
                    {
                        // To the nearest minute
                        if ((v & 1) != 0)
                        {
                            bool roundUp = Get(Sec1) >= 30;
                            Put(Sec1, roundUp ? 59 : 0);
                            if (roundUp)
                                AdvanceSecond();
                        }
                    }
                    else
                        _bank1[reg] = (byte)(v & Bank1Mask[reg]);
                    break;
            }
        }

        public static void Sync(int cycles)
        {
            _cycles += cycles;
            if (_cycles < CyclesPerSecond)
                return;

            _cycles -= CyclesPerSecond;

            // With the timer stopped (while a program writes the time) the second is lost
            if ((_mode & ModeTimerEnable) != 0)
                AdvanceSecond();
        }

        static void AdvanceSecond()
        {
            int seconds = Get(Sec1) + 1;
            if (seconds < 60) { Put(Sec1, seconds); return; }
            Put(Sec1, 0);

            int minutes = Get(Min1) + 1;
            if (minutes < 60) { Put(Min1, minutes); return; }
            Put(Min1, 0);

            if (!AdvanceHour())
                return;

            _time[Weekday] = (byte)((_time[Weekday] + 1) % 7);

            int day = Get(Day1) + 1;
            if (day <= DaysInMonth()) { Put(Day1, day); return; }
            Put(Day1, 1);

            int month = Get(Month1) + 1;
            if (month <= 12) { Put(Month1, month); return; }
            Put(Month1, 1);

            Put(Year1, (Get(Year1) + 1) % 100);
            _bank1[LeapYear] = (byte)((_bank1[LeapYear] + 1) & 3);
        }

        // One hour on; true when the day turns over
        static bool AdvanceHour()
        {
            if (Is24Hours)
            {
                int hours = Get(Hour1) + 1;
                Put(Hour1, hours % 24);
                return hours == 24;
            }

            // 12 hours: bit 1 of the tens digit is PM, and the hours run 12, 1 ... 11
            bool pm = (_time[Hour10] & 2) != 0;
            int hour = (_time[Hour10] & 1) * 10 + _time[Hour1] + 1;

            if (hour == 12)
                pm = !pm;
            else if (hour == 13)
                hour = 1;

            Put12(hour, pm);
            return hour == 12 && !pm;
        }

        static void PutHour(int hour24)
        {
            if (Is24Hours)
                Put(Hour1, hour24);
            else
                Put12(hour24 % 12 == 0 ? 12 : hour24 % 12, hour24 >= 12);
        }

        static void Put12(int hour, bool pm)
        {
            _time[Hour1] = (byte)(hour % 10);
            _time[Hour10] = (byte)(hour / 10 | (pm ? 2 : 0));
        }

        static bool Is24Hours => (_bank1[Select24] & 1) != 0;

        static int DaysInMonth() => Get(Month1) switch
        {
            2 => _bank1[LeapYear] == 0 ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };

        // A counter from its units digit and the tens digit above it
        static int Get(int units) => _time[units + 1] * 10 + _time[units];

        static void Put(int units, int value)
        {
            _time[units] = (byte)(value % 10);
            _time[units + 1] = (byte)(value / 10 & Bank0Mask[units + 1]);
        }

        public static void SaveState(Snapshot.Writer w)
        {
            w.Bytes(_time);
            w.Bytes(_bank1);
            w.U8(_mode);
            w.I64(_cycles);
        }

        public static void LoadState(Snapshot.Reader r)
        {
            Array.Copy(r.Bytes(_time.Length), _time, _time.Length);
            Array.Copy(r.Bytes(_bank1.Length), _bank1, _bank1.Length);
            _mode = r.U8();
            _cycles = r.I64();
        }
    }
}
