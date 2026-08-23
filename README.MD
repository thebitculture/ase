
# ASE = Atari System Emulator

![Logo.](Images/logo.png "Logo.")

**Atari System Emulator** or **ASE** is an **Atari ST** emulator that emulates the **ST F/FM**, the **Mega ST** and the **STE**. It is written in C# and utilizes the great **Motorola 68000 Moira** emulator created by **Dr. Dirk Hoffmann**.

📖 **New to ASE? Read the [User Manual](https://github.com/thebitculture/ase/wiki).**

This emulator has been partly ported from an old emulator that I started writing in C and assembly language in the late 90s, although in this released version, there is a bit of everything. Most of it has been rewritten, and other parts have been ported from **Hatari**, one of the best **Atari ST** emulators currently available.

The initial idea behind this emulator was for it to serve as a simple example to experiment with and study how an emulator works, as I explain on my **YouTube** channel, _**The Bit Culture**_. However, with later updates it has gained more complexity and is now more than just an easy-to-understand emulator. Even so, I invite you to check out the video on YouTube and follow the project comments if you’re curious about how an emulator really works.

Check out my channel, we won't only talk about emulators there, but we'll also look at some hardware and classic games.

👉 https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn


## Minimum Requirements

It runs on **Windows**, **Mac**, and should also work on **Linux**, although I haven't tested it. **ASE** will work with any **TOS** version from 1.00 to 1.04 on the ST/Mega models, and from 1.06 to 2.06 on the STE.

## Downloads

You can download the latest release ready for use from the releases page, or better yet, you can download the source code to compile and experiment yourself. To compile the source code, you will need to download **Moira** from **Dr. Dirk Hoffmann's** repository and copy only the 68k emulator into that directory, excluding the `MoiraConfig.h` configuration file. In the **ASE** **Moira** directory, you will find `Moira_dotnet.cpp` and `Moira_dotnet.h`, which are responsible for exposing Moira to the C# wrapper, as well as the files to compile **Moira** (`build.cmd` and `build.sh`), depending on the OS you use.

To compile **Moira** on **Windows**, you will need to have `cmake.exe` in your system path or open a **Visual Studio** developer console.

### Native libraries layout

The native libraries live under `ASE/native/`, organized **per .NET RID** (operating system + architecture). When publishing, `ASE.csproj` copies **only** the library matching the target RID next to the executable:

```
ASE/native/
  win-x64/      moira.dll, SDL2.dll, mt32emu-2.dll
  osx-x64/      moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  osx-arm64/    moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  linux-x64/    moira.so, libmt32emu.so.2
  linux-arm64/  moira.so, libmt32emu.so.2, libtinyfiledialogs.so
```

`SDL2` is bundled only on Windows and macOS; on **Linux** the distribution's own SDL2 is used. `libmt32emu` (see below) is committed **already compiled** for every RID, so it never has to be built as part of ASE. The build scripts place the compiled **Moira** library straight into the matching `native/<rid>/` folder:

- `build.cmd` / `build.sh` with no argument build for the current machine.
- To target another platform, pass its RID: `./build.sh osx-x64`, `./build.sh linux-arm64`, or `build.cmd ARM64` (cross-compiling requires the corresponding toolchain). See `ASE/native/README.md` for details.

Then publish for that RID, e.g. `dotnet publish -c Release -r linux-arm64 --self-contained`.

### Roland MT-32 emulation

The **Roland MT-32** (and its CM-32L / LAPC-I relatives) is the module a good number of Atari ST games and demos were composed for, through the MIDI ports every ST shipped with. ASE emulates the ST's MIDI interface and can either bridge it to the host's real MIDI ports (*System managed MIDI*) or play it through a built-in MT-32, whose synthesis comes from **Munt**'s `libmt32emu`:

👉 https://github.com/munt/munt

You don’t need to build this library; the binary is already included in this repository. However, if you still want to build it yourself, take a look at `ASE/native/README.md`.

`libmt32emu` is distributed under the **LGPL v2.1 or later**. The **MT-32 ROM images are not included**: just like TOS, they are copyrighted (by Roland) and have to be supplied by the user.

👉 ASE Releases: https://github.com/thebitculture/ase/releases/

👉 Moira Releases: https://github.com/dirkwhoffmann/Moira/releases

👉 Munt (libmt32emu): https://github.com/munt/munt

---

(C) _The Bit Culture_ - Public License Version 3
