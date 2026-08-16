namespace ASE
{
    /// <summary>
    /// A destination for the emulated ST's MIDI OUT: a host MIDI port
    /// (<see cref="HostMidiOutput"/>) or the built-in MT-32 (<see cref="Mt32Backend"/>).
    /// It receives whole messages, not raw bytes — <see cref="MidiStreamParser"/> sits in
    /// front and reassembles the ACIA's byte stream (running status included) because
    /// every backend API underneath (winmm, the ALSA sequencer, CoreMIDI, libmt32emu)
    /// wants framed messages.
    ///
    /// All methods are called from the emulation thread and must not throw: the call
    /// chain starts inside the CPU's bus-write callback, which native code invokes.
    /// </summary>
    public interface IMidiOutput : IDisposable
    {
        /// <summary>Where the messages go, for log messages ("Munt MT-32", a port name…).</summary>
        string Description { get; }

        /// <summary>
        /// A complete channel or system-common message. <paramref name="length"/> (1-3) is
        /// how many bytes the message actually has; unused data bytes are 0.
        /// </summary>
        void ShortMessage(byte status, byte data1, byte data2, int length);

        /// <summary>A complete system-exclusive message, $F0 first and $F7 last.</summary>
        void SysEx(byte[] message);

        /// <summary>A system real-time byte ($F8-$FF); these interleave with anything.</summary>
        void RealTime(byte status);
    }
}
