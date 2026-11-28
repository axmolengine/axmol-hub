# Axmol v2 LTS vs v3 Architecture and Migration

Use the project's actual engine version, not training assumptions. The current version comes from `<core>/axmolver.h.in` (`core/` in v2, `axmol/` in v3), never from the top heading of `CHANGELOG.md`. Confirm the game's `CMakeLists.txt` and `cmake/modules/AXGameEngineSetup.cmake` select the matched tree before citing APIs.

Keep lookups narrow and index-first. Call `axmol_api_query` first to locate exact C++ declarations and Lua exports with `file:line`, then `fs_read` only the short line windows needed to confirm signatures or usage. Avoid root-wide `fs_list`/`fs_find`/`fs_grep` sweeps when `axmol_api_query` already identifies the file.

## Key v2 LTS vs v3 Differences (verify against the matched tree)

1. **Core layout & includes**
   - v2: `core/` (`core/2d/Node.h`, `core/2d/Scene.h`, `core/2d/Camera.h`, `core/2d/Component.h`, `core/renderer/backend/ProgramManager.h`).
   - v3: `axmol/` (`axmol/scene/Node.h`, `axmol/scene/Scene.h`, `axmol/scene/Camera.h`, `axmol/scene/Component.h`, `axmol/renderer/ProgramManager.h`, `axmol/rhi/`).
   - Game source assets/resources live in `Content/` in both v2 and v3 (never `Resources/`, which is only a packaged build output directory).
   - Always use `ax::` and `AX_*` (never legacy `cocos2d::` or `CC_*`). Never wrap `ax::Ref`/`ax::Object` scene-graph nodes in `std::shared_ptr`/`std::unique_ptr`; use `::create()` with `retain()`/`release()` when held outside the scene graph.
   - Standard: v2 targets C++17/C++20; v3 targets C++23.

2. **Graphics backend / RHI & shaders**
   - v2: `core/renderer/backend/` (`ax::backend::DriverBase`, `CommandBuffer`, `RenderPipeline`, `ProgramState`, `ProgramManager`); GLSL shaders (`Source/shaders/*.fsh`, `*.vsh`) compiled via `axslcc`.
   - v3: `axmol/rhi/` (`ax::rhi::GraphicsCore`, `GraphicsDevice`, `GraphicsContext`, `GraphicsPipeline`, `ComputePipeline`, `PipelineDesc`, `RenderPassDesc`, `ProgramState`) with `axmol/renderer/ProgramManager.h`; HLSL shaders (`Source/Shaders/*_fs.hlsl`, `*_vs.hlsl` — always read `<engine>/docs/hlsl-spec.md` and `<engine>/docs/hlsl-faq.md` before writing or migrating shaders). Experimental D3D11/D3D12/Vulkan plus Metal/GL backends.

3. **Input & event system**
   - v2: `EventTouch`, `EventMouse`, `EventListenerTouchOneByOne`, `EventListenerTouchAllAtOnce`, `EventListenerMouse`, `EventKeyboard`, `EventListenerKeyboard`.
   - v3: unified `InputSystem`, `PointerEvent`, `PointerEventListener`, `KeyboardEvent`, `KeyboardEventListener`, `ControllerEvent`, `CustomEvent`, `FocusEvent`, `AccelerationEvent`, `XRInputEvent`.
   - In v3, `EventDispatcher` hit-tests the associated `Node` before invoking `onPointerDown`; zero-size nodes (such as MVC `ViewBase`) need an explicit canvas size or custom hit test. Return `true` from `onPointerDown` to capture subsequent move/up events.

4. **Async jobs & removed APIs**
   - `AsyncTaskPool` exists in v2 (`core/base/AsyncTaskPool.h`) and is **removed** in v3. In v3 C++, use `ax::JobSystem` (`axmol/base/JobSystem.h`) or `Director::runAsync`. In v3 Lua, own callback state per operation instead of calling `ax.AsyncTaskPool`.

5. **Lua bindings**
   - v2: Python + tolua++ (`extensions/scripting/lua-bindings/auto/` and `manual/`), `registerScriptHandler`, legacy `cc`/`ccui`/`ccb`/`ccexp` aliases.
   - v3: PowerShell-hosted C# libclang generator + sol2 3.5 (`extensions/scripting/lua-bindings/generated/manifests/*.json` + `adapters/`, see `extensions/scripting/lua-bindings/MIGRATION.md`). Global `cc`/`ccui`/`ccb`/`ccexp` aliases are removed—use `ax`, `axui`, `axr`, `axexp`. Use `ax.ParticleSystemQuad:create(...)` for plist 2D particles, pass value tables (`ax.size(...)`, `ax.p(...)`) to `Node` methods like `setContentSize`, and assign callback fields (`listener.onPointerDown = ...`) instead of `registerScriptHandler`.

## Workflow

1. Confirm engine version and `core/` vs `axmol/` from the workspace digest or `axmol_skill_read`.
2. For each symbol being migrated or used, query `axmol_api_query` (set `language: "cpp"` or `"lua"` as needed). Check absence in the target version as well as the replacement signature.
3. Read only the exact `file:line` windows returned by `axmol_api_query` (and `extensions/scripting/lua-bindings/MIGRATION.md` when migrating Lua). When writing or migrating v3 shaders, `fs_read` both `<engine>/docs/hlsl-spec.md` and `<engine>/docs/hlsl-faq.md` before editing shader code.
4. Cite exact target-version `file:line` evidence and produce the minimal, idiomatic patch for the target version.

**Verify:** build the target project with the matching engine's `axmol build` (adding `-forceConfig` if files or shaders were added) and run/test the changed path; clearly separate built, runtime-verified, and not-run steps.
