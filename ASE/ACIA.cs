/*
 * 
 * Atari ST ACIA emulation functions.
 * Some parts ported from Hatari emulator by Thomas Huth and others.
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

        // IKBD command length table (from Hatari ikbd.c KeyboardCommands[])
        // Key = first byte (command), Value = total bytes expected (including command byte)
        // Commands not in this table are unknown -> discard immediately.
        // I copied this behaviour from hatari...
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

                // MFP IRQ On
                ASEMain._mfp.SetGPIOBit(4, true);
            }
        }

        public static void Sync(int cycles)
        {
            lock (_syncLock)
            {
                // If there’s already a byte waiting for the CPU to read,
                // PAUSE TIME. I don’t pull anything else from the queue.
                // This protects the st from receiving data faster than it can read it.
                if (_hasLatchedData) return;

                // If there’s nothing in the queue, reset the counter so that the
                // next incoming byte is instantaneous (start bit).
                if (IkbdRx.Count == 0)
                {
                    _cyclesUntilNextByte = 0;
                    return;
                }

                // Hay datos en la cola y el registro está libre. Avanzamos el tiempo.
                _cyclesUntilNextByte -= cycles;

                if (_cyclesUntilNextByte <= 0)
                {
                    // Movemos el dato al registro visible
                    _latchedData = IkbdRx.Dequeue();
                    _hasLatchedData = true;

                    // Activamos flags
                    AciaKbdStatus |= (ACIA_RDRF | ACIA_IRQ);

                    // Disparamos interrupción (Línea Baja = Activa)
                    ASEMain._mfp.SetGPIOBit(4, false);

                    // Reiniciamos el temporizador para el SIGUIENTE byte
                    _cyclesUntilNextByte = CYCLES_PER_BYTE;
                }
            }
        }

        public static void WriteControl(byte v)
        {
            lock (_syncLock)
            {
                AciaKbdControl = v;

                // Master Reset del ACIA (Bits 0 y 1 a '1')
                // Muchos juegos hacen esto para limpiar el estado antes de empezar.
                if ((v & 0x03) == 0x03)
                {
                    Reset();
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

                // La CPU ha leído. Liberamos el registro.
                _hasLatchedData = false;

                // Limpiamos flags
                AciaKbdStatus &= unchecked((byte)~(ACIA_RDRF | ACIA_IRQ | ACIA_OVRN | ACIA_FE));

                // IMPORTANTE: Subimos la línea de interrupción (Inactiva)
                ASEMain._mfp.SetGPIOBit(4, true);

                // No tocamos _cyclesUntilNextByte. El método Sync() empezará a contar
                // en la siguiente iteración del emulador.

                return result;
            }
        }

        // *** IKBD Command Processing ***
        //
        // Emulates the HD6301 command parser as documented in Hatari
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
                    _commandBuffer.Clear();
                    return;
                }

                // ...Do we have all the bytes for this command?...
                if (_commandBuffer.Count < expectedLength)
                {
                    // Still waiting for more parameter bytes
                    return;
                }

                // ...Command is complete? execute it
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
                        // (matches Hatari's IKBD_Boot_ROM)
                        MouseEnabled = true;
                        JoystickEnabled = true;

                        PushIkbd_Internal(0xF0);
                        PushIkbd_Internal(0xF1);
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
                    JoystickEnabled = true;
                    MouseEnabled = false;
                    // Send immediate joystick
                    PushIkbd_Internal(0xFF);
                    PushIkbd_Internal(JoystickState);
                    break;

                case 0x15: // SET JOYSTICK INTERROGATION MODE (stop auto)
                    JoystickEnabled = false;
                    break;

                case 0x16: // JOYSTICK INTERROGATE
                    PushIkbd_Internal(0xFD);
                    PushIkbd_Internal(0x00);           // Joy 0 (mouse port, normally 0)
                    PushIkbd_Internal(JoystickState);  // Joy 1
                    break;

                case 0x17: // SET JOYSTICK MONITORING
                case 0x18: // SET FIRE BUTTON MONITORING
                case 0x19: // SET JOYSTICK KEYCODE MODE
                    break;

                case 0x1A: // DISABLE JOYSTICKS
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

                dx = dx / ConfigOptions.RunninConfig.MouseXSensitivity;
                dy = dy / ConfigOptions.RunninConfig.MouseYSensitivity;
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
                // *** Fire button ***
                // IKBD routes fire differently depending on mode:
                //
                // - Mouse active: fire -> right mouse button (in mouse packet header)
                //                 The joystick packet does NOT include fire.
                //
                // - Mouse OFF, joystick ON: fire -> bit 7 in joystick packet
                //
                // I’m keeping both modes because some games don’t work depending on
                // the method they use to read the joystick fire button.
                //
                if (mask == JOY_FIRE)
                {
                    // Fire -> mouse right button (this is what the desktop and most
                    // games that use mouse expect)
                    if (pressed) 
                        _mouseButtons |= 0x01; 
                    else 
                        _mouseButtons &= ~0x01;

                    PushIkbd_Internal((byte)(0xF8 | _mouseButtons));
                    PushIkbd_Internal(0);
                    PushIkbd_Internal(0);
                }

                // Directions (and fire when mouse is off)
                byte oldState = JoystickState;
                if (pressed) JoystickState |= mask; else JoystickState &= (byte)~mask;

                if (JoystickEnabled && JoystickState != oldState)
                {
                    PushIkbd_Internal(0xFF);
                    PushIkbd_Internal(JoystickState);
                }
            }
        }

    }
}