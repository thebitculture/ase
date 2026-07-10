
# ASE = Atari System Emulator

![Logo.](Images/logo.png "Logo.")

**Atari System Emulator** or **ASE** is an **Atari ST** emulator that emulates the **ST F/FM**, the **Mega ST** and the **STE**. It is written in C# and utilizes the great **Motorola 68000 Moira** emulator created by **Dr. Dirk Hoffmann**.

📖 **New to ASE? Read the [User Manual](https://github.com/thebitculture/ase/wiki).**

This emulator has been partly ported from an old emulator that I started writing in C and assembly language in the late 90s, although in this released version, there is a bit of everything. Most of it has been rewritten, and other parts have been ported from **Hatari**, one of the best **Atari ST** emulators currently available.

The initial idea behind this emulator was for it to serve as a simple example to experiment with and study how an emulator works, as I explain on my **YouTube** channel, _**The Bit Culture**_. However, with later updates it has gained more complexity and is now more than just an easy-to-understand emulator. Even so, I invite you to check out the video on YouTube and follow the project comments if you’re curious about how an emulator really works.

Check out my channel, we won't only talk about emulators there, but we'll also look at some hardware and classic games.

👉 https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn


![IK+](Images/ikplus.png "IK+")

## Using the Emulator

At the moment, the emulator is not compatible with some games and programs, and some of them will _blow up_ at some point, as the emulator is in a very early stage of development. I hope to add new features and improve compatibility over time.

Whether some games work depends largely on how they were cracked/unprotected; sometimes it might work simply by using another floppy image with a different crack. **ASE** handles disk images in **.ST**, **.MSA** and **.STX** (Pasti) format, also inside **.ZIP** archives — STX images preserve the original copy protection, so many original, protected disks work too.

To **load** a disk image, use **File ▸ Open disk image** (or just drag & drop the file onto the window), and to capture the keyboard and mouse, use **F12** or the **middle mouse button**. When you select a disk image, the emulator will ask if you want to reset the **Atari ST**.

The **ST** **Help** key is mapped to the **period/dot** key on the numeric keypad.

**ASE** supports gamepads and can emulate the joystick in a fully configurable way, allowing you to assign _"Up"_ to any gamepad button, as well as _"Space"_ or other commonly used keys in compilation game menus from that era. The keys used to emulate the joystick can be configured from the settings window, but by default, the configuration is as follows:

| Joystick | Numeric Keypad |
| :--- | :---: |
| Up | 8 |
| Down | 5 |
| Right | 6 |
| Left | 4 |
| Fire | 0 |

## Minimum Requirements

It runs on **Windows**, **Mac**, and should also work on **Linux**, although I haven't tested it. **ASE** will work with any **TOS** version from 1.00 to 1.04 on the ST/Mega models, and from 1.06 to 2.06 on the STE.

## Downloads

You can download the latest release ready for use from the releases page, or better yet, you can download the source code to compile and experiment yourself. To compile the source code, you will need to download **Moira** from **Dr. Dirk Hoffmann's** repository and copy only the 68k emulator into that directory, excluding the `MoiraConfig.h` configuration file. In the **ASE** **Moira** directory, you will find `Moira_dotnet.cpp` and `Moira_dotnet.h`, which are responsible for exposing Moira to the C# wrapper, as well as the files to compile **Moira** (`build.cmd` and `build.sh`), depending on the OS you use.

To compile **Moira** on **Windows**, you will need to have `cmake.exe` in your system path or open a **Visual Studio** developer console.

👉 ASE Releases: https://github.com/thebitculture/ase/releases/

👉 Moira Releases: https://github.com/dirkwhoffmann/Moira/releases

---

(C) _The Bit Culture_ - Public License Version 3
