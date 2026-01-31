/*
 * 
 * An incomplete Atari ST ACIA emulation functions.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using static ASE.Config;
using static ASE.MFP68901;
using static SDL2.SDL;

namespace ASE
{
    /// <summary>
    /// Provides constants, state, and methods for emulating the Atari ST's Asynchronous Communication Interface Adapter
    /// (ACIA), including keyboard, mouse, and joystick input handling.
    /// </summary>
    /// <remarks>The ACIA class is intended for use in emulation scenarios where accurate simulation of input
    /// devices is required. It exposes methods to reset device state, send mouse movement packets, and update joystick
    /// status, as well as constants and fields representing hardware registers and input mappings. All members are
    /// static, reflecting the hardware's global nature. Thread safety is not guaranteed; callers should ensure
    /// appropriate synchronization if accessed from multiple threads.</remarks>
    public static class ACIA
    {
        public const byte ACIA_RDRF = 1 << 0;
        public const byte ACIA_IRQ = 1 << 7;  // interrupt request (status)

        public static Queue<byte> IkbdRx = new();

        public static byte AciaKbdStatus; // TDRE=1
        public static byte AciaKbdControl; // ACIA Control register, I'm not using in emulation by now

        public static byte JoystickState = 0;

        // Bit 0 up, 1: down, 2: left, 3: right, 7: fire
        public const byte JOY_UP = 0x01;
        public const byte JOY_DOWN = 0x02;
        public const byte JOY_LEFT = 0x04;
        public const byte JOY_RIGHT = 0x08;
        public const byte JOY_FIRE = 0x80;

        // Bit 0 = No button, 1 = right, 2 = left
        public static int _mouseButtons = 0;

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
            IkbdRx.Clear();

            _mouseButtons = 0;
            AciaKbdStatus = 0x02;
            AciaKbdControl = 0;
        }

        public static void SendMousePacket(int dx, int dy)
        {
            dx = dx / ConfigOptions.RunninConfig.MouseXSensitivity;
            dy = dy / ConfigOptions.RunninConfig.MouseYSensitivity;

            // El protocolo IKBD espera valores entre -128 y 127
            if (dx < -127) dx = -127;
            if (dx > 127) dx = 127;
            if (dy < -127) dy = -127;
            if (dy > 127) dy = 127;

            byte header = (byte)(0xF8 | _mouseButtons); // 0xF8 mouse button

            // Transmit
            PushIkbd(header);
            PushIkbd((byte)dx);
            PushIkbd((byte)dy);
        }

        public static void PushIkbd(byte b)
        {
            IkbdRx.Enqueue(b);

            // $FFFC02 should holds the last scancode
            Program._mem.Write8(0xFFFC02, (byte)((b >> 7 == 1) ? 0 : b));

            // Status ACIA: data ready
            AciaKbdStatus |= (ACIA_RDRF | ACIA_IRQ);
            Program._mfp.SetGPIOBit(4, false);
        }

        public static void UpdateJoystick(byte mask, bool pressed)
        {
            byte oldState = JoystickState;

            // HACK !!!
            // Some games are not reading the trigger on the joystick in the standard way,
            // and I have to remap it as the right mouse button.
            // I assume this isn’t correct and that I should look into it further.

            if (mask == JOY_FIRE)
            {
                if (pressed)
                    _mouseButtons |= 0x01; // Bit 0
                else
                    _mouseButtons &= ~0x01; // Apagar Bit 0

                SendMousePacket(0, 0);
            }
            
            if (pressed)
                JoystickState |= mask;
            else
                JoystickState &= (byte)~mask;
            
            // Send only if the state changed
            if (JoystickState != oldState)
            {
                // Paquete Joystick 1: 0xFF + Estado
                PushIkbd(0xFF);
                PushIkbd(JoystickState);
            }
        }

    }
}
