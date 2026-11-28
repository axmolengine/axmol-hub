# Blur or Custom Shader on a Sprite in an Axmol Game

Use the project's actual engine, not the newest install. The current version comes from `<core>/axmolver.h.in` (`core/` in v2, `axmol/` in v3), never from the top heading of `CHANGELOG.md`. First confirm the game's `CMakeLists.txt` and `cmake/modules/AXGameEngineSetup.cmake` select the matched tree (templates prefer embedded `axmol/` when present). If an embedded or isolated engine omits `tests/`, state what is missing instead of silently borrowing another version.

Keep the pass narrow and index-first. Reuse the matched engine version and core directory from the workspace digest or `axmol_skill_read`. Call `axmol_api_query` FIRST for `Sprite::setProgramState`, `ProgramManager::loadProgram`, and `ProgramState` instead of scanning the engine tree with `fs_list`/`fs_find`/`fs_grep`. Open only the returned declaration windows, the single example method, the shader source, and the CMake `app_shaders_dir` rule. Once those agree with the game's `MainScene`, stop exploring and deliver the minimal patch—do not audit unrelated renderer internals.

1. **Declarations (via `axmol_api_query` + short `fs_read`)**
   - Query `axmol_api_query(query="Sprite::setProgramState", language="cpp")` and `axmol_api_query(query="ProgramManager::loadProgram", language="cpp")`.
   - Expect `core/2d/Sprite.h` + `core/renderer/backend/ProgramManager.h` (`ax::backend::ProgramState`) in v2, or `axmol/2d/Sprite.h` + `axmol/renderer/ProgramManager.h` (`ax::rhi::ProgramState`) in v3.
   - Read only the short windows around the reported lines to confirm overloads, vertex layout, and `ProgramState` ownership (`ownPS` / `retain` / `release`).

2. **Same-version example**
   - In `tests/cpp-tests/Source/ShaderTest/ShaderTest.cpp`, `fs_grep` for `SpriteBlur` and `fs_read` just `SpriteBlur::initProgram` and its uniform/update code.
   - Follow the actual vertex shader key, custom fragment key, uniform names (`u_resolution` / `blurRadius` / `sampleNum`, or exact struct/cbuffer layout in v3), texture size, and ownership pattern. Apply it to the Sprite in the game's own scene (`Source/MainScene.cpp`).

3. **Shader source & CMake build rule**
   - Trace the custom shader key (e.g. `custom/...`) to the matched version's shader format:
     - v2: GLSL under `Source/shaders/*.fsh` (reference: `tests/cpp-tests/Source/shaders/example_Blur.fsh`).
     - v3: HLSL 2021 (SM 5.1 portable subset) under `Source/Shaders/*_fs.hlsl` (reference: `tests/cpp-tests/Source/Shaders/example_Blur_fs.hlsl`). **Before writing or migrating any v3 shader, `fs_read` both `<engine>/docs/hlsl-spec.md` and `<engine>/docs/hlsl-faq.md`** to follow Axmol's exact HLSL/axslcc conventions (including `#include "base.hlsli"` for built-in samplers `LinearClamp`/`PointClamp`, declaring `Texture2D u_tex0;` **without** manual `: register(...)` annotations rejected by `axslcc`, `cbuffer` layout, and `main` as entry point).
   - Use `fs_grep` for `app_shaders_dir` in `cmake/Modules/AXBuildHelpers.cmake` to confirm exact folder case, file suffix, and compiled resource name (`custom/<stem>`). Inspect `AXSLCC.cmake` only if `AXBuildHelpers.cmake` leaves the mapping ambiguous.
   - Game source assets/resources live in `Content/` (not `Resources/`; `Resources/` is only the packaged bundle output directory).

4. **Minimal game-only patch & citations**
   - Cite this version's `file:line` for `Sprite`, `ProgramManager`, the example, the shader source, and `AXBuildHelpers.cmake`.
   - In Agent mode when asked to implement, add one custom shader in the game's shader directory and apply it to one Sprite with nonzero blur radius and sample count. Do not edit the engine or copy a whole test scene.

**Verify:** run the matching engine's `axmol build` for the host (e.g. `axmol build -p osx -a arm64`) with **`-forceConfig`** so CMake discovers the newly added shader. Confirm shader compilation succeeds and the compiled `axslc/custom/<name>` artifact exists in the built bundle's packaged resources directory (e.g. `.app/Contents/Resources/axslc/custom/<name>` on macOS). Launch the game and verify the sprite's blurred edges against an unblurred control. Clearly distinguish built, visually verified, and not run.
