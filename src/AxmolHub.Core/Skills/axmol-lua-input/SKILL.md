# Lua Bindings and Click/Touch Input in an Axmol Game

Use the project's actual engine and Lua bindings. The current version comes from `<core>/axmolver.h.in` (`core/` in v2, `axmol/` in v3), never from the top heading of `CHANGELOG.md`. Confirm the game's `CMakeLists.txt` and `cmake/modules/AXGameEngineSetup.cmake` select the matched tree before citing exports. Do not infer Lua API names from C++ declarations without confirming the Lua binding.

Keep the pass narrow and index-first. Reuse the matched engine version and core directory from the workspace digest or `axmol_skill_read`. Call `axmol_api_query(language="lua", ...)` FIRST to inspect Lua exports, methods, callback fields, and `file:line`—do not scan 1MB manifest JSONs or dozens of `.cpp` files manually. Batch independent reads of the game's `Content/src/app/views/MainScene.lua` and `Content/src/packages/mvc/ViewBase.lua` (or the game's actual entry script), verify the same-version `ClickAndMoveTest.lua` sample, and stop expanding once the binding, sample, hit region, and minimal patch agree.

1. **Lua export & callback shape (via `axmol_api_query`)**
   - v3 (sol2): call `axmol_api_query(query="PointerEventListener", language="lua")` and `axmol_api_query(query="PointerEvent", language="lua")` (indexed from `extensions/scripting/lua-bindings/generated/manifests/*.json` + `adapters/`). Read only the reported lines if extra detail is needed, and check `extensions/scripting/lua-bindings/MIGRATION.md` for pointer capture and breaking changes:
     - Assign callback fields (`listener.onPointerDown = ...`, `listener.onPointerUp = ...`); `onPointerDown` must return `true` to capture subsequent move/up events.
     - Legacy globals `cc`, `ccui`, `ccb`, `ccexp` are removed—use `ax`, `axui`, `axr`, `axexp`.
     - Pass native value tables (`ax.size(...)`, `ax.p(...)`) to `Node` methods such as `setContentSize`, and use `ax.ParticleSystemQuad:create(...)` for plist 2D particles.
   - v2 (tolua++): call `axmol_api_query(query="EventListenerTouchOneByOne", language="lua")` (indexed from `extensions/scripting/lua-bindings/auto/` + `manual/`). Read only the reported `axlua_base_manual.cpp` lines to verify `create`, `registerScriptHandler`, and `ax.Handler.EVENT_TOUCH_BEGAN`/`ENDED`.

2. **Same-version example**
   - Open `tests/lua-tests/Content/src/ClickAndMoveTest/ClickAndMoveTest.lua`. Trace the down/up pair, return value claiming the pointer/touch, coordinate extraction, action call (`ax.MoveTo:create`), and scene-graph dispatcher registration (`addEventListenerWithSceneGraphPriority`). Never retain a borrowed event object past the callback return.

3. **Game scene attachment & v3 Node hit region**
   - Read the game's entry scene (`Content/src/app/views/MainScene.lua` + `Content/src/packages/mvc/ViewBase.lua`). Attach the listener once to a node in that scene.
   - **Critical for v3**: `axmol/base/EventDispatcher.cpp` and `axmol/scene/Node.cpp` hit-test the listener's target node before calling `onPointerDown`. An MVC `ViewBase` is a plain `Node` with zero content size, and visible children do not expand its bounds. Set an explicit canvas size on the input container node (e.g. `self:setContentSize(ax.size(display.width, display.height))` or `Director:getVisibleSize()`) or define an explicit hit test.
   - Move the existing visible sprite on release (converting coordinates to the sprite's parent space if transformed). Cite exact `file:line` for the version header, Lua binding, example, and hit-test check when applicable.

**Verify:** build the Lua game with its matching engine (`axmol build -p osx -a arm64`) and run it (`axmol run` with the same target). Click/tap and release at two separated points and confirm the same sprite moves to both points with no Lua runtime errors in the log. Only execute when mode and task permit; explicitly report any unverified steps.
