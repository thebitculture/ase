/*
 * 
 * Atari ST ACIA emulation functions.
 * 
 * Some parts inspired in Hatari emulator created by Thomas Huth and others.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using static ASE.Config;
using System.Collections.Generic;

namespace ASE
{
    public static class ACIA
    {
        private static readonly object _syncLock = new object();

        public const byte ACIA_RDRF = 1 << 0;
        public const byte ACIA_TDRE = 1 << 1;
        public const byte ACIA_IRQ = 1 << 7;  // interrupt request (status)
        public const byte ACIA_OVRN = 1 << 5; // Overrun Error
        public const byte ACIA_FE = 1 << 4;   // Framing Error

        public static Queue<byte> IkbdRx = new();

        public static byte AciaKbdStatus;   // ACIA Status register
        public static byte AciaKbdControl;  // ACIA Control register

        // CPU cycles per byte (7812.5 baud @ 8MHz ~= 10240 cycles)
        private const int CYCLES_PER_BYTE = 10240;
        private static int _cyclesUntilNextByte = 0;

        public static byte JoystickState = 0;

        private static List<byte> _commandBuffer = new List<byte>();

        // Enabled or disabled for the ACIA chip
        // Confirmation pending: after IKBD reset, both mouse and joystick are active?
        public static bool JoystickEnabled = true;
        public static bool MouseEnabled = true;

        // $17 SET JOYSTICK MONITORING: the IKBD stops scanning the keyboard and the mouse and
        // just samples the sticks, sending a fixed 2-byte packet (%000000xy fire buttons,
        // %nnnnmmmm directions; x/n = joy 0, y/m = joy 1) every 'rate' hundredths of a second.
        // Exited by the other joystick-mode commands ($14/$15/$1A) or an IKBD reset.
        public static bool JoystickMonitoring = false;
        private static long _monitoringPeriodCycles = 0;
        private static long _monitoringCountdown = 0;
        private const long CYCLES_PER_CENTISECOND = 80128; // 512 cycles * 313 lines * 50 Hz / 100

        // for the joy -> bit 0 up, 1: down, 2: left, 3: right, 7: fire
        public const byte JOY_UP = 0x01;
        public const byte JOY_DOWN = 0x02;
        public const byte JOY_LEFT = 0x04;
        public const byte JOY_RIGHT = 0x08;
        public const byte JOY_FIRE = 0x80;

        // for the mouse -> bit 0 = No button, 1 = right, 2 = left
        public static int _mouseButtons = 0;

        private static byte _latchedData = 0;
        private static bool _hasLatchedData = false;

        // IKBD command length table
        // Key = first byte (command), Value = total bytes expected (including command byte)
        // Commands not in this table are unknown -> discard immediately.
        private static readonly Dictionary<byte, int> _commandLengths = new Dictionary<byte, int>
        {
            { 0x07, 2 },  // SET MOUSE BUTTON ACTION
            { 0x08, 1 },  // SET RELATIVE MOUSE POSITION REPORTING
            { 0x09, 5 },  // SET ABSOLUTE MOUSE POSITIONING
            { 0x0A, 3 },  // SET MOUSE KEYCODE MODE
            { 0x0B, 3 },  // SET MOUSE THRESHOLD
            { 0x0C, 3 },  // SET MOUSE SCALE
            { 0x0D, 1 },  // INTERROGATE MOUSE POSITION
            { 0x0E, 6 },  // SET INTERNAL MOUSE POSITION (LOAD)
            { 0x0F, 1 },  // SET Y=0 AT BOTTOM
            { 0x10, 1 },  // SET Y=0 AT TOP
            { 0x11, 1 },  // RESUME
            { 0x12, 1 },  // DISABLE MOUSE
            { 0x13, 1 },  // PAUSE OUTPUT
            { 0x14, 1 },  // SET JOYSTICK EVENT REPORTING (auto mode)
            { 0x15, 1 },  // SET JOYSTICK INTERROGATION MODE
            { 0x16, 1 },  // JOYSTICK INTERROGATE
            { 0x17, 2 },  // SET JOYSTICK MONITORING
            { 0x18, 1 },  // SET FIRE BUTTON MONITORING (duration)
            { 0x19, 7 },  // SET JOYSTICK KEYCODE MODE
            { 0x1A, 1 },  // DISABLE JOYSTICKS
            { 0x1B, 7 },  // SET TIME-OF-DAY CLOCK
            { 0x1C, 1 },  // INTERROGATE TIME-OF-DAY CLOCK
            { 0x20, 4 },  // MEMORY LOAD
            { 0x21, 3 },  // MEMORY READ
            { 0x22, 3 },  // CONTROLLER EXECUTE
            { 0x80, 2 },  // RESET
            // Status inquiry commands (top bit set)
            { 0x87, 1 },  // REPORT MOUSE BUTTON ACTION
            { 0x88, 1 },  // REPORT MOUSE MODE (relative)
            { 0x89, 1 },  // REPORT MOUSE MODE (absolute)
            { 0x8A, 1 },  // REPORT MOUSE MODE (keycode)
            { 0x8B, 1 },  // REPORT MOUSE THRESHOLD
            { 0x8C, 1 },  // REPORT MOUSE SCALE
            { 0x8F, 1 },  // REPORT MOUSE VERTICAL
            { 0x90, 1 },  // REPORT MOUSE VERTICAL
            { 0x92, 1 },  // REPORT MOUSE AVAILABILITY
            { 0x94, 1 },  // REPORT JOYSTICK MODE
            { 0x95, 1 },  // REPORT JOYSTICK MODE
            { 0x99, 1 },  // REPORT JOYSTICK MODE
            { 0x9A, 1 },  // REPORT JOYSTICK AVAILABILITY
        };

        // Map SDL_Scancode (index) to Atari ST scancode (value)
        public static byte[] AtariScancodes = new byte[256]
        {
            // padding
            0x00, 0x00, 0x00, 0x00, 
            
            // 4 - 29 (A - Z)
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, // A-H
            0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, // I-P
            0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, // Q-X
            0x15, 0x2C,                                     // Y, Z
            // 30 - 39 (1 - 0)
            0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B,
            // 40 - 44 (Enter, Esc, Backspace, Tab, Space)
            0x1C, 0x01, 0x0E, 0x0F, 0x39,
            // 45 - 56 (- = [ ] \ # ; ' ` , . /)
            0x0C, 0x0D, 0x1A, 0x1B, 0x2B, 0x2B, 0x27, 0x28, 0x29, 0x33, 0x34, 0x35,
            // 57 (CapsLock)
            0x3A,
            // 58 - 69 (F1 - F12) - F11 and F12 reserved for special functions
            0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x00, 0x00,
            // 70 - 72 (PrintScreen, ScrollLock, Pause)
            0x00, 0x00, 0x00,
            // 73 - 78 (Insert, Home, PageUp, Delete, End, PageDown)
            0x52, 0x47, 0x63, 0x53, 0x00, 0x00,
            // 79 - 82 (Right, Left, Down, Up)
            0x4D, 0x4B, 0x50, 0x48,
            // 83 - 99 (Keypad: Numlock, /, *, -, +, Enter, 1-9, 0, .)
            0x00, 0x65, 0x66, 0x4A, 0x4E, 0x72,
            0x6D, 0x6E, 0x6F, 0x6A, 0x6B, 0x6C, 0x67, 0x68, 0x69, 0x70,
            0x62, // Mapped numeric . to Atari Help key <- <- This should be configurable (as the rest of keyboard keys)
            // padding 124
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,
            // 224 - 230
            0x1D, // 224: Left Ctrl
            0x2A, // 225: Left Shift
            0x38, // 226: Left Alt
            0x00, // 227: Left GUI (Windows key)
            0x1D, // 228: Right Ctrl (leftt Ctrl en ST)
            0x36, // 229: Right Shift
            0x38, // 230: Right Alt (AltGr)
            // padding to end
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0
        };

        public static void Reset()
        {
            lock (_syncLock)
            {
                IkbdRx.Clear();
                _commandBuffer.Clear();
                _mouseButtons = 0;
                JoystickState = 0;

                _hasLatchedData = false;
                _latchedData = 0;
                _cyclesUntilNextByte = 0;

                AciaKbdStatus = ACIA_TDRE;
                AciaKbdControl = 0;

                // After IKBD boot, mouse (relative) and joystick (auto) are enabled?
                JoystickEnabled = true;
                MouseEnabled = true;
                JoystickMonitoring = false;

                // MFP IRQ On
                ASEMain._mfp.SetGPIOBit(4, true);
            }
        }

        // Snapshot

        public static void SaveState(Snapshot.Writer w)
        {
            lock (_syncLock)
            {
                w.U8(AciaKbdStatus);
                w.U8(AciaKbdControl);
                w.U8(JoystickState);
                w.Bool(JoystickEnabled);
                w.Bool(MouseEnabled);
                w.Bool(JoystickMonitoring);
                w.Bool(_hasLatchedData);
                w.U8(_latchedData);
                w.I32(_mouseButtons);
                w.I32(_cyclesUntilNextByte);
                w.I64(_monitoringPeriodCycles);
                w.I64(_monitoringCountdown);

                w.U16((ushort)IkbdRx.Count);
                foreach (byte b in IkbdRx)
                    w.U8(b);

                w.U16((ushort)_commandBuffer.Count);
                foreach (byte b in _commandBuffer)
                    w.U8(b);
            }
        }

        public static void LoadState(Snapshot.Reader r)
        {
            lock (_syncLock)
            {
                AciaKbdStatus = r.U8();
                AciaKbdControl = r.U8();
                JoystickState = r.U8();
                JoystickEnabled = r.Bool();
                MouseEnabled = r.Bool();
                JoystickMonitoring = r.Bool();
                _hasLatchedData = r.Bool();
                _latchedData = r.U8();
                _mouseButtons = r.I32();
                _cyclesUntilNextByte = r.I32();
                _monitoringPeriodCycles = r.I64();
                _monitoringCountdown = r.I64();

                IkbdRx.Clear();
                int rxCount = r.U16();
                for (int i = 0; i < rxCount; i++)
                    IkbdRx.Enqueue(r.U8());

                _commandBuffer.Clear();
                int cmdCount = r.U16();
                for (int i = 0; i < cmdCount; i++)
                    _commandBuffer.Add(r.U8());
            }
        }

        public static void Sync(int cycles)
        {
            lock (_syncLock)
            {
                // Joystick monitoring runs on its own sample clock, independent of whether the
                // program is keeping up with the receiver (unread packets are simply lost to
                // the overrun logic below, like on real hardware).
                if (JoystickMonitoring)
                {
                    _monitoringCountdown -= cycles;
                    if (_monitoringCountdown <= 0)
                    {
                        _monitoringCountdown += _monitoringPeriodCycles;

                        // Joy 0 mirrors the emulated stick, same as the $16 interrogation
                        // reply, so port-0 and port-1 readers both work with a single stick.
                        byte fires = (byte)(((JoystickState & JOY_FIRE) >> 6) | ((JoystickState & JOY_FIRE) >> 7));
                        byte dirs = (byte)(((JoystickState & 0x0F) << 4) | (JoystickState & 0x0F));
                        PushIkbd_Internal(fires);
                        PushIkbd_Internal(dirs);
                    }
                }

                // If there's already a byte waiting for the CPU to read, nothing new can be
                // latched -- but the IKBD does NOT stop transmitting. On the real 6850 a byte
                // that arrives while the receive register is still full is simply LOST and the
                // OVRN flag is set. Modelling this loss is load-bearing: a program that ignores
                // the ACIA for a while (Ex. Rodland's loader polls with $16 interrogations for
                // minutes without reading a single reply) must find FRESH data when it finally
                // starts reading. Simply pausing the queue instead let thousands of stale reply
                // bytes pile up, so at the title screen the game was reading joystick states
                // from minutes ago and the fire button appeared dead.
                if (_hasLatchedData)
                {
                    // The 6850 holds its IRQ line asserted (by level) as long as the
                    // receive register is full, i.e. until the CPU reads the data. The MFP
                    // GPIP4 input, however, is edge triggered: if the program cleared the
                    // ACIA pending bit without reading the data first (typically while
                    // reprogramming the MFP at start-up), the request would be lost forever
                    // and this byte -- plus every key/joystick byte queued behind it --
                    // would never be delivered. Re-assert the pending bit to emulate the
                    // level-sensitive line and keep the receiver from dead-locking.
                    if ((ASEMain._mfp.IPRB & MFP68901.RegB.ACIA) == 0)
                        ASEMain._mfp.SetInterruptPending(MFP68901.RegB.ACIA, true);

                    if (IkbdRx.Count == 0)
                    {
                        _cyclesUntilNextByte = 0;
                        return;
                    }

                    _cyclesUntilNextByte -= cycles;
                    if (_cyclesUntilNextByte <= 0)
                    {
                        // Byte lost in transit (receiver overrun)
                        IkbdRx.Dequeue();
                        AciaKbdStatus |= ACIA_OVRN;
                        _cyclesUntilNextByte = CYCLES_PER_BYTE;

                        if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                            ColoredConsole.WriteLine($"[[cyan]]ACIA[[/cyan]] [[red]]overrun[[/red]] byte dropped (queue={IkbdRx.Count})");
                    }
                    return;
                }

                // If there's nothing in the queue, reset the counter so that the
                // next incoming byte is instantaneous (start bit).
                if (IkbdRx.Count == 0)
                {
                    _cyclesUntilNextByte = 0;
                    return;
                }

                _cyclesUntilNextByte -= cycles;

                if (_cyclesUntilNextByte <= 0)
                {
                    // Next register
                    _latchedData = IkbdRx.Dequeue();
                    _hasLatchedData = true;

                    // activate flags
                    AciaKbdStatus |= (ACIA_RDRF | ACIA_IRQ);

                    // and set interrupt pending
                    ASEMain._mfp.SetGPIOBit(4, false);

                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                        ColoredConsole.WriteLine(
                            $"[[cyan]]ACIA[[/cyan]] RX [[green]]${_latchedData:X2}[[/green]] -> IRQ " +
                            $"(IERB.kb={((ASEMain._mfp.IERB & 0x40) != 0)}, " +
                            $"IMRB.kb={((ASEMain._mfp.IMRB & 0x40) != 0)}, " +
                            $"IPRB.kb={((ASEMain._mfp.IPRB & 0x40) != 0)})");

                    // ready for next read
                    _cyclesUntilNextByte = CYCLES_PER_BYTE;
                }
            }
        }

        public static void WriteControl(byte v)
        {
            lock (_syncLock)
            {
                AciaKbdControl = v;

                // Master Reset of the 6850 ACIA (control bits 1-0 = %11).
                // IMPORTANT: this resets ONLY the ACIA chip (its status register and the
                // byte currently held in the receive data register). It must NOT reset the
                // IKBD (HD6301): the keyboard's mouse/joystick reporting mode, the command
                // parser and any bytes the IKBD has already queued for transmission survive.
                // Previously this called the full Reset(), which silently reverted the
                // joystick back to the power-up (mouse) mode whenever a game reset the ACIA
                // mid-session (e.g. to clear an overrun) -> the fire button stopped being
                // reported in the joystick packet.
                if ((v & 0x03) == 0x03)
                {
                    _hasLatchedData = false;
                    _latchedData = 0;
                    _cyclesUntilNextByte = 0;
                    AciaKbdStatus = ACIA_TDRE;
                    ASEMain._mfp.SetGPIOBit(4, true);

                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                        ColoredConsole.WriteLine("[[cyan]]ACIA[[/cyan]] 6850 master reset (chip only, IKBD state preserved)");
                }
                else if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                {
                    ColoredConsole.WriteLine($"[[cyan]]ACIA[[/cyan]] control = [[yellow]]${v:X2}[[/yellow]] (RX irq {((v & 0x80) != 0 ? "on" : "off")})");
                }
            }
        }

        public static byte ReadStatus()
        {
            lock (_syncLock)
            {
                return AciaKbdStatus;
            }
        }

        public static byte ReadData()
        {
            lock (_syncLock)
            {
                byte result = _latchedData;

                _hasLatchedData = false;
                // clear flags
                AciaKbdStatus &= unchecked((byte)~(ACIA_RDRF | ACIA_IRQ | ACIA_OVRN | ACIA_FE));
                ASEMain._mfp.SetGPIOBit(4, true);

                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine($"[[blue]]CPU[[/blue]] read ACIA data [[green]]${result:X2}[[/green]] (PC=${CPU._moira.PC0:X6})");

                return result;
            }
        }

        // *** IKBD Command Processing ***
        //
        // Emulates the HD6301 command parser:
        //
        // 1) Each byte received is added to the input buffer.
        // 2) If the first byte matches a known command, wait until
        //    all expected parameter bytes have arrived, then execute.
        // 3) If the first byte is NOT a known command, discard the
        //    entire buffer immediately (the real IKBD treats it as NOP).
        // 4) After execution or discard, the buffer is cleared for
        //    the next command.
        //
        // WITHOUT this logic, any unrecognized multi-byte command
        // (like 0x07 sent by TOS) will jam the buffer and prevent
        // ALL subsequent commands from being processed.
        public static void HandleCommand(byte b)
        {
            lock (_syncLock)
            {
                _commandBuffer.Add(b);

                byte cmd = _commandBuffer[0];

                // Is this a known command?
                if (!_commandLengths.TryGetValue(cmd, out int expectedLength))
                {
                    // Unknown command -> discard immediately (IKBD treats as NOP)
                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                        ColoredConsole.WriteLine($"[[cyan]]IKBD[[/cyan]] unknown command [[red]]${cmd:X2}[[/red]] discarded");
                    _commandBuffer.Clear();
                    return;
                }

                // 0x20 MEMORY LOAD is variable length: a 4-byte header
                // (0x20, ADR_MSB, ADR_LSB, NUM) followed by NUM data bytes. Without this
                // the NUM payload bytes would be parsed as if they were new commands and
                // corrupt the whole input stream.
                if (cmd == 0x20 && _commandBuffer.Count >= 4)
                    expectedLength = 4 + _commandBuffer[3];

                // ...Do we have all the bytes for this command?...
                if (_commandBuffer.Count < expectedLength)
                {
                    // Still waiting for more parameter bytes
                    return;
                }

                // ...Command is complete? execute it
                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine($"[[cyan]]IKBD[[/cyan]] cmd [[green]]${cmd:X2}[[/green]] (mouse={MouseEnabled}, joy={JoystickEnabled}, PC=${CPU._moira.PC0:X6})");

                ExecuteCommand(cmd);

                // Clear buffer for next command
                _commandBuffer.Clear();
            }
        }

        private static void ExecuteCommand(byte cmd)
        {
            // Already inside _syncLock from HandleCommand

            switch (cmd)
            {
                case 0x80: // RESET (0x80, 0x01)
                    if (_commandBuffer[1] == 0x01)
                    {
                        IkbdRx.Clear();
                        JoystickState = 0;

                        _hasLatchedData = false;
                        _cyclesUntilNextByte = 0;

                        AciaKbdStatus &= unchecked((byte)~(ACIA_RDRF | ACIA_IRQ));
                        ASEMain._mfp.SetGPIOBit(4, true);

                        // After reset, both mouse and joystick are active
                        MouseEnabled = true;
                        JoystickEnabled = true;
                        JoystickMonitoring = false;

                        // On a successful self-test the real IKBD returns a SINGLE byte
                        // (0xF0). Emitting a second byte (the old 0xF1) leaves a stray value
                        // in the receive queue that a game's packet parser reads as the first
                        // "data" byte after reset and can desynchronise it.
                        PushIkbd_Internal(0xF0);
                    }
                    // else: 0x80 followed by non-0x01 -> ignored
                    break;

                // Mouse mode commands

                case 0x08: // SET RELATIVE MOUSE POSITION REPORTING
                    MouseEnabled = true;
                    break;

                case 0x09: // SET ABSOLUTE MOUSE POSITIONING
                    MouseEnabled = true;
                    // Parameters: XMSB, XLSB, YMSB, YLSB (ignored for now)
                    break;

                case 0x0A: // SET MOUSE KEYCODE MODE
                    MouseEnabled = true;
                    break;

                case 0x07: // SET MOUSE BUTTON ACTION
                case 0x0B: // SET MOUSE THRESHOLD
                case 0x0C: // SET MOUSE SCALE
                case 0x0E: // LOAD MOUSE POSITION
                case 0x0D: // INTERROGATE MOUSE POSITION
                case 0x0F: // SET Y=0 AT BOTTOM
                case 0x10: // SET Y=0 AT TOP
                    break;
                case 0x12: // DISABLE MOUSE
                    MouseEnabled = false;
                    break;

                // Keyboard commands

                case 0x11: // RESUME (unpause output)
                    break;

                case 0x13: // PAUSE OUTPUT
                    break;

                // Joystick mode commands

                case 0x14: // SET JOYSTICK EVENT REPORTING (auto mode)
                    JoystickMonitoring = false;
                    JoystickEnabled = true;
                    // This is a joystick command, so the IKBD stops scanning port 0
                    // as a mouse: "after any joystick command, the ikbd assumes
                    // joysticks are connected to both Joystick0 and Joystick1".
                    // Mouse motion/button packets therefore stop, and the trigger is
                    // reported in the joystick packet (bit 7) instead of as a button.
                    MouseEnabled = false;
                    // NOTE: the real IKBD does NOT send a joystick report on receipt of
                    // 0x14 -- it only reports on the next change. Emitting an unsolicited
                    // 0xFF/state packet here injected a byte the game had not asked for; if
                    // it stayed unread while the program reprogrammed the MFP, the ACIA
                    // receiver dead-locked and froze all keyboard/joystick input (Nebulus).
                    break;

                case 0x15: // SET JOYSTICK INTERROGATION MODE (stop auto)
                    // Also a joystick command -> port 0 leaves mouse mode. The host
                    // then polls with 0x16; the trigger travels in bit 7 of the
                    // interrogation reply.
                    JoystickMonitoring = false;
                    JoystickEnabled = false;
                    MouseEnabled = false;
                    break;

                case 0x16: // JOYSTICK INTERROGATE
                    // Joy 0 shares the mouse port: when a mouse is plugged in
                    // its directions read as 0. If the game disabled the mouse,
                    // it's likely using port 0 as a joystick, so we mirror the
                    // emulated stick to Joy 0 too. This makes both port-0 and
                    // port-1 readers work with a single host joystick.
                    PushIkbd_Internal(0xFD);
                    PushIkbd_Internal(MouseEnabled ? (byte)0x00 : JoystickState); // Joy 0
                    PushIkbd_Internal(JoystickState);                              // Joy 1
                    break;

                case 0x17: // SET JOYSTICK MONITORING
                    {
                        int rate = _commandBuffer[1];
                        if (rate == 0) rate = 1;

                        // Like every joystick command it takes port 0 out of mouse mode; the
                        // event packets ($FF) stop too, everything travels in the fixed
                        // 2-byte monitoring packets.
                        MouseEnabled = false;
                        JoystickEnabled = false;
                        JoystickMonitoring = true;
                        _monitoringPeriodCycles = rate * CYCLES_PER_CENTISECOND;
                        _monitoringCountdown = _monitoringPeriodCycles;

                        if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                            ColoredConsole.WriteLine($"[[cyan]]IKBD[[/cyan]] joystick monitoring ON, rate={rate}/100 s");
                    }
                    break;

                case 0x18: // SET FIRE BUTTON MONITORING
                case 0x19: // SET JOYSTICK KEYCODE MODE
                    break;

                case 0x1A: // DISABLE JOYSTICKS
                    JoystickMonitoring = false;
                    JoystickEnabled = false;
                    break;

                // Clock commands

                case 0x1B: // SET TIME-OF-DAY CLOCK
                    break;

                case 0x1C: // INTERROGATE TIME-OF-DAY CLOCK, I dont know how to implement
                    PushIkbd_Internal(0xFC);
                    PushIkbd_Internal(0); // YY
                    PushIkbd_Internal(0); // MM
                    PushIkbd_Internal(0); // DD
                    PushIkbd_Internal(0); // hh
                    PushIkbd_Internal(0); // mm
                    PushIkbd_Internal(0); // ss
                    break;

                // Memory commands

                case 0x20: // MEMORY LOAD
                case 0x21: // MEMORY READ
                case 0x22: // CONTROLLER EXECUTE
                    break;

                // Status inquiry commands

                case 0x87:
                case 0x88:
                case 0x89:
                case 0x8A:
                case 0x8B:
                case 0x8C:
                case 0x8F:
                case 0x90:
                case 0x92:
                case 0x94:
                case 0x95:
                case 0x99:
                case 0x9A:
                    break;
            }
        }

        public static void SendMousePacket(int dx, int dy)
        {
            lock (_syncLock)
            {
                if (!MouseEnabled) return;

                dx = (int)(dx / ConfigOptions.RunninConfig.MouseSensitivity);
                dy = (int)(dy / ConfigOptions.RunninConfig.MouseSensitivity);
                if (dx < -127) dx = -127; if (dx > 127) dx = 127;
                if (dy < -127) dy = -127; if (dy > 127) dy = 127;

                PushIkbd_Internal((byte)(0xF8 | _mouseButtons));
                PushIkbd_Internal((byte)dx);
                PushIkbd_Internal((byte)dy);
            }
        }

        public static void PushIkbd(byte b)
        {
            lock (_syncLock)
            {
                // While monitoring, the IKBD does nothing but sample the sticks: keyboard
                // scanning stops, so scancodes must not interleave with the fixed 2-byte
                // packet stream (they would desync the program's parser).
                if (JoystickMonitoring) return;

                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine($"[[magenta]]HOST[[/magenta]] enqueue key/byte [[yellow]]${b:X2}[[/yellow]] (queue={IkbdRx.Count})");
                PushIkbd_Internal(b);
            }
        }

        private static void PushIkbd_Internal(byte b)
        {
            IkbdRx.Enqueue(b);
        }

        public static void UpdateJoystick(byte mask, bool pressed)
        {
            lock (_syncLock)
            {
                // Keep the full joystick-1 state at all times: directions in bits
                // 0-3 and the trigger in bit 7. The interrogation reply (0x16)
                // always reports this real state; the auto event packet may strip
                // the trigger depending on the port-0 mode (see below).
                byte oldState = JoystickState;
                if (pressed) JoystickState |= mask; else JoystickState &= (byte)~mask;
                if (JoystickState == oldState) return;

                // *** Fire button routing ***
                // On real ST hardware the joystick-1 fire line (port 1, pin 6) is the
                // SAME electrical signal as the RIGHT mouse button, while the
                // joystick-0 fire line (port 0, pin 6) is the LEFT mouse button. So
                // the IKBD reports the trigger on different channels depending on
                // whether port 0 is currently scanned as a mouse:
                //
                //   - Mouse mode (MouseEnabled, the power-up default): the trigger is
                //     "logically assigned to the mouse", so joystick-1 fire is sent as
                //     the RIGHT mouse button (bit 0 of the 0xF8 packet) and is NOT
                //     included in the joystick event packet.
                //   - Joystick mode (mouse off, after a joystick command): fire is
                //     sent as bit 7 of the 0xFF joystick event packet.
                if (mask == JOY_FIRE && MouseEnabled)
                {
                    if (pressed)
                        _mouseButtons |= 0x01;   // bit 0 = right mouse button
                    else
                        _mouseButtons &= ~0x01;

                    PushIkbd_Internal((byte)(0xF8 | _mouseButtons));
                    PushIkbd_Internal(0);
                    PushIkbd_Internal(0);
                    return;
                }

                // Auto joystick event packet: directions always, plus the trigger
                // only when the mouse is off. While the mouse is on, bit 7 is masked
                // out because the trigger is being reported through the mouse button.
                if (JoystickEnabled)
                {
                    byte report = MouseEnabled ? (byte)(JoystickState & ~JOY_FIRE) : JoystickState;
                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                        ColoredConsole.WriteLine($"[[magenta]]HOST[[/magenta]] joy event -> packet [[yellow]]FF ${report:X2}[[/yellow]] (fireBit={((report & JOY_FIRE) != 0)})");
                    PushIkbd_Internal(0xFF);
                    PushIkbd_Internal(report);
                }
            }
        }

    }
}