# native/ — Native Libraries by RID

Native libraries are separated by **.NET RID** (`<os>-<architecture>`).
The `.csproj` file copies to the output **only** the folder corresponding to the RID targeted for compilation/publishing (or, in a `dotnet build`/`run` without `-r`, the host machine's RID).

```
native/
  win-x64/      moira.dll, SDL2.dll, mt32emu-2.dll   (+ moira.lib, moira.exp — linking artifacts, not copied)
  osx-x64/      moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  osx-arm64/    moira.dylib, libSDL2.dylib, libmt32emu.2.dylib
  linux-x64/    moira.so, libmt32emu.so.2
  linux-arm64/  moira.so, libmt32emu.so.2, libtinyfiledialogs.so
```

## Folder Contents Breakdown

| RID           | Moira        | SDL2            | libmt32emu             | Notes                                                         |
|---------------|--------------|-----------------|------------------------|---------------------------------------------------------------|
| `win-x64`     | `moira.dll`  | `SDL2.dll`      | `mt32emu-2.dll`      | Moira built with static MSVC runtime (see `Moira/CMakeLists.txt`) |
| `osx-x64`     | `moira.dylib`| `libSDL2.dylib` | `libmt32emu.2.dylib` | Intel Mac (Not officially supported)                          |
| `osx-arm64`   | `moira.dylib`| `libSDL2.dylib` | `libmt32emu.2.dylib` | Apple Silicon Mac                                             |
| `linux-x64`   | `moira.so`   | —               | `libmt32emu.so.2`    | SDL2 is provided by the distribution (not packaged)           |
| `linux-arm64` | `moira.so`   | —               | `libmt32emu.so.2`    | Raspberry Pi / ARM; SDL2 from the distribution                |

## Building Moira for Each Architecture

You do not need to rebuild **Moira**, as it is already precompiled in the `native` directory. If you still wish to compile it manually, clone the Moira repository and copy only the emulator core into ASE's Moira folder, leaving ASE's preconfigured `CMakeLists.txt` and `MoiraConfig.h` untouched.

On Linux/macOS, `build.sh` targets the host architecture. For *cross-compilation* (e.g., building `linux-arm64` from x64, or `osx-x64` from Apple Silicon), pass the appropriate toolchain/architecture flag to the build generator. For Windows, the
script to make this magic is `build.cmd` (requires a developer environment with CMake an C++ compiler such as the Visual Studio Developer Command Prompt). Copy the resulting binary into the respective RID folder above, and you're done.

## libmt32emu (Munt)

**Roland MT-32** emulation relies on `libmt32emu`, from the **Munt** project: <https://github.com/munt/munt>. Unlike Moira, it is **not** built as part of ASE: precompiled binaries are committed directly to the corresponding RID folder and ship alongside the emulator across all three platforms (Linux builds do not rely on system package managers either). MT-32 ROMs are not distributed: they are the proprietary property of Roland and must be provided by the user.

To build the library yourself, clone the Munt repository and run:

```
cd munt/mt32emu

cmake -B build -DCMAKE_BUILD_TYPE=Release \
  -Dlibmt32emu_SHARED=ON \
  -Dlibmt32emu_C_INTERFACE=ON \
  -Dlibmt32emu_CPP_INTERFACE=OFF \
  -Dlibmt32emu_WITH_INTERNAL_RESAMPLER=ON

cmake --build build -j$(sysctl -n hw.ncpu)
```

Copy the built library into `native` using the naming convention outlined in the table above.

## TinyFileDialogs

The NuGet package does not include a precompiled binary for ARM. This repository already provides the ARM build. If you need to recompile it from source, follow the instructions at <https://sourceforge.net/projects/tinyfiledialogs/>.