namespace ASE
{
    /// <summary>
    /// The single interrupt line both 6850 ACIAs share. On the ST the keyboard ACIA
    /// ($FFFC00) and the MIDI ACIA ($FFFC04) have their open-drain /IRQ outputs wired
    /// together onto MFP GPIP bit 4 (active low): the line is low while EITHER chip
    /// requests service, and only returns high when both have been satisfied. Level-6
    /// handlers therefore read both status registers to find out who is asking.
    ///
    /// Each chip reports its own request state here instead of driving the GPIP bit
    /// directly — otherwise one chip releasing the line would silently mask the other
    /// chip's pending request (the MFP input is edge sensitive, so a request hidden
    /// behind an already-low line never produces a new edge; both ACIA emulations carry
    /// a rescue re-assert for exactly that case, see their Sync methods).
    /// </summary>
    public static class AciaIrqLine
    {
        static readonly object _lock = new();
        static bool _keyboard;
        static bool _midi;

        /// <summary>Keyboard ACIA interrupt request (true = asserted, i.e. line pulled low).</summary>
        public static void SetKeyboard(bool irqActive)
        {
            lock (_lock)
            {
                _keyboard = irqActive;
                Apply();
            }
        }

        /// <summary>MIDI ACIA interrupt request (true = asserted, i.e. line pulled low).</summary>
        public static void SetMidi(bool irqActive)
        {
            lock (_lock)
            {
                _midi = irqActive;
                Apply();
            }
        }

        static void Apply()
        {
            // GPIP bit 4 carries the wired-OR of both requests: low (false) while any is active.
            var mfp = ASEMain._mfp;
            if (mfp != null)
                mfp.SetGPIOBit(4, !(_keyboard || _midi));
        }
    }
}
