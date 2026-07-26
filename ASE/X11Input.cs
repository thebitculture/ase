/*
 *
 * ASE — X11 input glue (Linux only)
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System;
using System.Runtime.InteropServices;
using SDL2;

namespace ASE
{
    /// <summary>
    /// Makes SDL actually receive the host input of the Avalonia window on Linux/X11.
    ///
    /// <para>The emulator hands Avalonia's native window to <c>SDL_CreateWindowFrom</c>. On
    /// Windows that subclasses the HWND and on macOS it wraps the NSWindow, so in both cases SDL
    /// sees the keyboard and the mouse. The X11 backend does neither: it only registers the
    /// foreign window and never calls <c>XSelectInput</c> on it, so SDL's own X connection keeps
    /// an *empty* event mask for that window and no event of it ever reaches SDL's queue. The
    /// three symptoms this produced on Linux all come from that single fact:</para>
    /// <list type="bullet">
    ///   <item>no keyboard — key events are only delivered to clients that selected them;</item>
    ///   <item>no mouse capture — without EnterNotify/FocusIn, SDL has no focused window to grab
    ///   the pointer for, so <c>SDL_SetRelativeMouseMode</c> succeeds but confines nothing;</item>
    ///   <item>no gamepad — SDL discards every joystick/controller event while the process has
    ///   windows but none of them holds the keyboard focus.</item>
    /// </list>
    ///
    /// <para>Selecting the input ourselves on SDL's connection fixes the three at once.
    /// <c>ButtonPressMask</c> is deliberately left out: X11 lets a single client per window
    /// select it, Avalonia already owns it, and asking for it fails with BadAccess — which
    /// discards the *whole* request, mask included. Button presses reach the emulator anyway:
    /// through Avalonia's overlay handlers while the mouse is free, and through SDL's own
    /// pointer grab (whose event mask is not subject to that rule) while it is captured.</para>
    /// </summary>
    internal static class X11Input
    {
        const string libX11 = "libX11.so.6";

        // Event mask bits, from X11/X.h
        const long KeyPressMask       = 1L << 0;
        const long KeyReleaseMask     = 1L << 1;
        const long ButtonReleaseMask  = 1L << 3;
        const long EnterWindowMask    = 1L << 4;
        const long LeaveWindowMask    = 1L << 5;
        const long PointerMotionMask  = 1L << 6;
        const long KeymapStateMask    = 1L << 14;
        const long StructureNotifyMask = 1L << 17;
        const long FocusChangeMask    = 1L << 21;

        // What SDL needs to track input on the window: keys, pointer motion, and the
        // enter/leave + focus transitions it uses to decide which window owns the input.
        // KeymapState lets it resync held keys when the window regains focus (no stuck keys
        // in the ST after alt-tab). See the class remarks for the missing ButtonPressMask.
        const long InputEventMask = KeyPressMask | KeyReleaseMask | ButtonReleaseMask |
                                    EnterWindowMask | LeaveWindowMask | PointerMotionMask |
                                    KeymapStateMask | StructureNotifyMask | FocusChangeMask;

        [DllImport(libX11)]
        static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);

        [DllImport(libX11)]
        static extern int XSync(IntPtr display, int discard);

        /// <summary>Selects the input events of the SDL window on SDL's X connection. Must run
        /// on the thread that initialised SDL's video subsystem (the UI thread).</summary>
        /// <returns>false — with a reason in <paramref name="error"/> — when the window is not
        /// an X11 one or Xlib is not reachable; the emulator still runs, without host input.</returns>
        public static bool AttachInput(IntPtr sdlWindow, out string error)
        {
            error = null;

            var info = new SDL.SDL_SysWMinfo();
            SDL.SDL_GetVersion(out info.version);   // SDL rejects the query if the major version doesn't match

            if (SDL.SDL_GetWindowWMInfo(sdlWindow, ref info) == SDL.SDL_bool.SDL_FALSE)
            {
                error = SDL.SDL_GetError();
                return false;
            }

            if (info.subsystem != SDL.SDL_SYSWM_TYPE.SDL_SYSWM_X11)
            {
                error = $"the window is not an X11 one (SDL video driver '{SDL.SDL_GetCurrentVideoDriver()}')";
                return false;
            }

            try
            {
                XSelectInput(info.info.x11.display, info.info.x11.window, InputEventMask);
                XSync(info.info.x11.display, 0);    // apply now, and surface any X error at once
            }
            catch (Exception ex)                    // no libX11 at runtime: never fatal
            {
                error = ex.Message;
                return false;
            }

            return true;
        }
    }
}
