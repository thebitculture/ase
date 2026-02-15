
# ASE = Atari System Emulator

![Logo.](Images/logo.png "Logo.")

**Atari System Emulator** or **ASE** is an **Atari ST** emulator that, for the moment, only emulates the F/FM versions. It is written in C# and utilizes the great **Motorola 68000 Moira** emulator created by **Dr. Dirk Hoffmann**.

This emulator has been partly ported from an old emulator that I started writing in C and assembly language in the late 90s, although in this released version, there is a bit of everything. Most of it has been rewritten, and other parts have been ported from **Hatari**, one of the best **Atari ST** emulators currently available.

The idea behind writing this emulator was mainly to demonstrate how an emulator works on my **YouTube** channel, _**The Bit Culture**_. There is no ambition beyond that, although I hope to make the emulator somewhat more functional, such as adding STE elements like the Blitter, and above all, improving compatibility.

Check out my channel, we won't only talk about emulators there, but we'll also look at some hardware and classic games.

👉 https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn

![Xenon](Images/xenon.png "Xenon")
![Gods](Images/gods.png "Gods")
![The Secret of Monkey Island](Images/monkey.png "The Secret of Monkey Island")

## Using the Emulator

At the moment, the emulator is not compatible with some games and programs, and some of them will _blow up_ at some point, as the emulator is in a very early stage of development. I hope to add new features and improve compatibility over time.

Whether some games work depends largely on how they were cracked/unprotected; sometimes it might work simply by using another floppy image with a different crack. By the way, **ASE** currently only handles disk images in **.ST** or **.MSA** format.

To **load** a disk image, use **F11**, and to capture the mouse, use **F12**. When you select a disk image, the emulator will ask if you want to reset the **Atari ST**.

The **ST** **Help** key is mapped to the **period/dot** key on the numeric keypad.

The emulator supports _gamepads_ and emulates the _joystick_ using the following keys on the numeric keypad:

| Joystick | Numeric Keypad |
| :--- | :---: |
| Up | 8 |
| Down | 5 |
| Right | 6 |
| Left | 4 |
| Fire | 0 |

## Minimum Requirements

It runs on **Windows**, **Mac**, and should also work on **Linux**, although I haven't tested it. **ASE** will work with any **TOS** version up to 1.04.

## Downloads

You can download the latest release ready for use from the releases page, or better yet, you can download the source code to compile and experiment yourself. To compile the source code, you will need to download **Moira** from **Dr. Dirk Hoffmann's** repository and copy only the 68k emulator into that directory, excluding the `MoiraConfig.h` configuration file. In the **ASE** **Moira** directory, you will find `Moira_dotnet.cpp` and `Moira_dotnet.h`, which are responsible for exposing Moira to the C# wrapper, as well as the files to compile **Moira** (`build.cmd` and `build.sh`), depending on the OS you use.

To compile **Moira** on **Windows**, you will need to have `cmake.exe` in your system path or open a **Visual Studio** developer console.

👉 ASE Releases: https://github.com/thebitculture/ase/releases/
👉 Moira Releases: https://github.com/dirkwhoffmann/Moira/releases

---

(C) _The Bit Culture_ - Public License Version 3
