# Build, CMake, and Cross-Platform Axmol CLI

Use the project's actual engine checkout and locked target, never another installed version. Confirm the engine version from `<core>/axmolver.h.in` (`core/` in v2, `axmol/` in v3) and verify which tree `CMakeLists.txt` + `cmake/modules/AXGameEngineSetup.cmake` select: standard templates prefer an embedded `axmol/` directory when present, otherwise the invoking engine/environment (`AX_ROOT`).

Keep checks narrow: read the game's `CMakeLists.txt`, `cmake/modules/AXGameEngineSetup.cmake`, `cmake/modules/AXGameSourceSetup.cmake`, and targeted sections of `cmake/Modules/AXBuildHelpers.cmake` or `CMakeOptions.md`. Use `axmol_api_query` only if build errors implicate engine APIs.

## Core Build Rules (v2 LTS & v3)

1. **PowerShell CLI (never Python)**
   - The `axmol` CLI (`tools/cmdline/axmol.ps1`) is implemented in PowerShell. Do not invoke `axmol` workflows via Python.
   - Common commands (verified against `1k/1kiss.ps1` and `tools/cmdline/`):
     - `axmol new <Name> -l <cpp|lua> -p <pkg>` (optional `-d <parentDir>`)
     - `axmol build -p <osx|win32|winuwp|linux|android|ios|tvos|wasm|wasm64> [-a <arm64|x64|armv7>] [-sdk simulator] [-O0|-O1|-O2|-O3] [-c|-configOnly] [-forceConfig] [-xc '-DFOO=ON,-DBAR=OFF'] [-d <projectDir>]`
     - `axmol run -p <platform> [-a <arch>] [-O0|-O1|-O2|-O3] [-d <projectDir>]`
     - v3 Lua bindings regeneration (from engine root): `./tools/cmdline/axmol genbindings`
   - **Critical flag traps**:
     - `-c` is short for `-configOnly` (configure CMake without compiling) — **NOT** the build configuration.
     - Build configuration uses `-O<n>` (`-O0`=Debug, `-O1`=MinSizeRel, `-O2`=RelWithDebInfo, `-O3`=Release); omitting `-O` defaults to `RelWithDebInfo`.
     - `-xc` takes **one** single comma-separated string argument (e.g. `-xc '-DAX_ENABLE_VR=ON,-DAX_PREBUILT_DIR=build'`); never pass multiple space-separated tokens after `-xc`.

2. **When `-forceConfig` is mandatory**
   - Adding new C++ source files (when globbed by `AXGameSourceSetup.cmake`) or new custom shaders under `app_shaders_dir` (`Source/shaders` in v2, `Source/Shaders` in v3) requires re-running CMake configure (`-forceConfig` in `axmol build`, or explicit `cmake -S . -B <build>`).
   - Incremental `axmol build` without reconfigure skips newly added shaders, producing missing `Resources/axslc/custom/...` runtime failures even though C++ compilation succeeds.

3. **Shader build & packaging rules**
   - Trace `app_shaders_dir` in `<engine>/cmake/Modules/AXBuildHelpers.cmake` to confirm directory case (`shaders` vs `Shaders`), shader extension (`.fsh`/`.vsh` in v2 GLSL vs `_fs.hlsl`/`_vs.hlsl` in v3 HLSL), and output resource key (`custom/<name>`).
   - Verify both shader compiler (`axslcc`) exit status and that the compiled output is packaged into the app's resources directory (e.g., `.app/Contents/Resources/axslc/custom/...` on macOS).

4. **CMake options & v3 RHI backends**
   - Read `<engine>/CMakeOptions.md` directly (use `fs_grep` for the specific option) rather than guessing flag names.
   - In v3, RHI backend selection uses `-DAX_RENDER_API=auto|gl|mtl|d3d11|d3d12|vk` (or semicolon-separated multiple backends such as `-DAX_RENDER_API=vk;gl`); `-DAX_CORE_PROFILE=TRUE` strips deprecated APIs for upgrade checks; `-DAX_PREBUILT_DIR=<dir>` links prebuilt engine libraries.

5. **Common cross-platform pitfalls**
   - **Encoding**: Use LF line endings and **no UTF-8 BOM** on `.h`/`.cpp`/`.json` files; BOM breaks Linux GCC precompiled headers (`#pragma once` + `.gch`).
   - **macOS re-signing**: Modifying or rebuilding resources inside an `.app` bundle can invalidate ad-hoc signatures; check codesign output if launch fails after resource changes.
   - **Embedded vs external engine**: Never edit files inside a protected engine installation when fixing a game project; confine game fixes to the game's `CMakeLists.txt`, `cmake/modules/`, `Source/`, or `Content/`.

**Verify:** cite exact `file:line` from the project's `CMakeLists.txt` and matched engine's `cmake/Modules/AXBuildHelpers.cmake` / `CMakeOptions.md`. When execution is allowed, run the exact `axmol build` command (with `-forceConfig` if files/shaders were added) and confirm both exit code and packaged outputs; report unverified steps explicitly.
