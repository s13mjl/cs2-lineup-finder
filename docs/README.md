# CS2 Lineup Finder

Grenade line-up finder for CS2 practice / run-map servers, shipped as a
CounterStrikeSharp plugin.

A Chinese translation follows every section. For installation see
[INSTALL.md](INSTALL.md), for the player-facing walkthrough [USAGE.md](USAGE.md).

---

## 1. What it is / 这是什么

**EN.** The player stands on a throw spot and aims at the area the grenade should
land in. `!lf_find` asks the plugin for the view angle that actually puts the
grenade inside that area, and the plugin answers with ranked `yaw` / `pitch`
values plus an aim beam from the player's eyes and a marker on the landing point.
The beam and the markers stay for `visuals.beamDurationSeconds` (3 seconds by
default) and can be redrawn with `!lf_draw`.

**中文.** CS2 跑图／练习服务器上的"瞄点预测"插件。玩家站在投掷点，用准星指向期望的
落点范围，执行 `!lf_find`，插件会算出真正能让道具落进该范围的视角，并在聊天里给出按
优劣排序的 `yaw` / `pitch`；同时在世界里画出从眼睛出发的瞄准光束和落点标记。光束与
标记默认保留 3 秒（`visuals.beamDurationSeconds`），可用 `!lf_draw` 重画。

The plugin does not aim for the player and does not modify the throw — it measures,
solves and draws. / 插件不会自动瞄准，也不改变投掷结果，只做测量、求解与可视化。

## 2. Architecture / 架构

```
contracts/                       共享接口与 DTO（无逻辑，无引擎类型）
        ↑                    ↑                        ↑
src/CS2LineupFinder.Math   src/CS2LineupFinder.Core   plugins/CS2LineupFinder
（数学 + 逆向求解器）        （正向弹道模拟器）           （CSSharp 入口：指令/菜单/Beam/存档）
```

**Dependency direction / 依赖方向:** `contracts ← Math`, `contracts ← Core`,
`contracts ← plugin`. Each lower layer owns its directory and no layer reaches
around `contracts/`.

**EN.** The plugin only talks to the contract interfaces
(`ITrajectorySimulator`, `ILineupSolver`) — never to types in `src/`. That is what
lets the plugin shell build, run and be tested while the physics and math layers
are still in flight. The shared contracts ship as a referenced assembly
(`CS2LineupFinder.Abstractions.dll`, deployed beside the plugin), so the plugin,
Core and Math all bind to one contract type at runtime (see C-08 in
[contract-issues.md](contract-issues.md)).

**中文.** 插件只面向 contracts 里的接口（`ITrajectorySimulator`、`ILineupSolver`）
编程，**不直接引用** `src/` 的类型。因此即使弹道与数学层还在开发，插件本体也能独立
编译、运行和被测试。contracts 以引用程序集的形式发布（`CS2LineupFinder.Abstractions.dll`，
与插件一同部署），插件、Core、Math 在运行时绑定同一个契约类型（见 C-08）。

**Merge order / 合并顺序:** `feat/math-core` → `feat/physics-sim` →
`feat/plugin-shell`, which matches the dependency direction. See
[../PROJECT_LAYOUT.md](../PROJECT_LAYOUT.md).

## 3. Quick start / 快速开始

```powershell
# 1. Build everything (solution uses the .slnx format, .NET 10 SDK default)
dotnet build CS2LineupFinder.slnx -c Release

# 2. Copy the whole output directory to the server
#    bin/Release/net8.0/  ->  game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder/

# 3. Restart the server, join, and open the menu
!lf_menu
```

Or download the `CS2LineupFinder-plugin` artifact from the CI run. Full server
setup (SteamCMD, Metamod:Source, CounterStrikeSharp) and the directory layout are
in [INSTALL.md](INSTALL.md); every command is listed below and explained in
[USAGE.md](USAGE.md).

**中文.** 最短路径：`dotnet build -c Release` → 把
`plugins/CS2LineupFinder/bin/Release/net8.0/` 整个目录拷到服务器的
`game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder/` → 重启服务器 →
进服执行 `!lf_menu`。也可以直接下载 CI 产物 `CS2LineupFinder-plugin`。服务器环境
搭建见 [INSTALL.md](INSTALL.md)，逐条指令说明见 [USAGE.md](USAGE.md)。

## 4. Commands / 指令一览

The table is the plugin's own command registry
([`Commands/CommandSpec.cs`](../plugins/CS2LineupFinder/Commands/CommandSpec.cs)),
so it matches the shipped binary. / 下表直接来自插件的指令登记表，与实际发布的 DLL
一致。

| Console command | Chat alias | Arguments / 参数 | What it does / 作用 |
| --- | --- | --- | --- |
| `css_lf_menu` | `!lf_menu` | none | Opens the main menu. 打开主菜单。 |
| `css_lf_start` | `!lf_start` | none | Marks your current position as the throw point. 把当前位置标记为投掷点。 |
| `css_lf_zone` | `!lf_zone` | `circle <radius> \| rect <width> <height>` | Creates the landing zone around the point you are looking at. 以准星指向的地面点为中心创建落点范围。 |
| `css_lf_grenade` | `!lf_grenade` | `smoke \| flash \| molotov \| he` | Selects the grenade. 选择道具。 |
| `css_lf_throw` | `!lf_throw` | `stand \| crouch \| jump` | Selects the throw stance. 选择投掷方式。 |
| `css_lf_button` | `!lf_button` | `primary \| secondary \| both` | Selects the mouse button combination. 选择鼠标按键组合。 |
| `css_lf_find` | `!lf_find` | none | Searches for a line-up and draws the result. 求解并显示结果。 |
| `css_lf_save` | `!lf_save` | `<name>` | Saves the current result under a name. 保存当前结果为 JSON。 |
| `css_lf_load` | `!lf_load` | `<name>` | Loads a saved line-up. 读回已保存的点位。 |
| `css_lf_list` | `!lf_list` | none | Lists the line-ups saved on this map. 列出本图已保存点位。 |
| `css_lf_draw` | `!lf_draw` | none | Redraws the latest result in the world. 重画上一次结果的光束与标记。 |
| `css_lf_set` | `!lf_set` | `<index>` | Picks one of the results of the last search. 切换上一条结果中的某一条。 |
| `css_lf_clear` | `!lf_clear` | none | Clears the throw point, the landing zone and the results. 清空投掷点/落点范围/结果。 |
| `css_lf_reload` | `!lf_reload` | none | Reloads `config.toml`. 重新载入 config.toml。 |

14 commands. **EN:** type `css_lf_*` in the console and `!lf_*` in chat —
CounterStrikeSharp rewrites a `!` trigger word into the `css_` form and dispatches
it to the same handler, so the aliases need no extra code. Passing a command a
missing argument prints its usage line. **中文:** 控制台写 `css_lf_*`，聊天写
`!lf_*`（CSSharp 会把 `!` 触发词自动重写为 `css_` 形式并派发给同一个处理函数），
缺参数时插件会打印该指令的用法。

The menu is live: selecting an entry keeps it open, and the grenade / stance /
button entries cycle to the next value instead of opening a submenu.
菜单点击后不会关闭，道具/姿势/按键三项是循环切换。

## 5. Configuration / 配置

`config/config.toml`, next to the DLL
([template](../plugins/CS2LineupFinder/config/config.toml)). It is read on plugin
load and again on every `!lf_reload`. Missing keys keep
their built-in default, so trimming the file is safe; out-of-range values are
clamped, and unknown keys are logged as a warning at load instead of failing.

**中文.** 配置文件与 DLL 同目录下的 `config/config.toml`。插件载入时读一次，之后每次
`!lf_reload` 再读一次。缺少的键使用内置默认值，因此删减文件是安全
的；超出范围的值会被夹紧；出现插件不认识的键会在控制台告警但不影响加载。

| Key | Default | Meaning / 含义 |
| --- | --- | --- |
| `solver.timeoutSeconds` | `3.0` | Wall clock budget for `css_lf_find`; the search is cancelled and reported as a timeout when it expires. 求解超时（秒）。 |
| `solver.maxResults` | `5` | How many solutions are printed and stored per search. 单次求解最多展示/保存的结果数。 |
| `solver.maxCandidates` | `64` | Upper bound on candidate view angles handed to the simulator per search. 单次求解的候选视角上限。 |
| `visuals.enabled` | `true` | Master switch for every in-world visual. 所有可视化效果的总开关。 |
| `visuals.beamDurationSeconds` | `3.0` | Lifetime of the aim beam and landing markers. 光束与落点标记的存活时间（秒）。 |
| `visuals.beamLength` | `1024.0` | Length of the aim beam drawn from the player's eyes, in world units. 瞄准光束长度。 |
| `visuals.beamSprite` | `materials/sprites/laserbeam.vmat` | Sprite used for beams; it ships with the game, so nothing has to be precached. 光束贴图（游戏自带，无需预加载）。 |
| `visuals.ringSegments` | `24` | Segments used to approximate a circular landing zone. 圆形落点范围的近似段数。 |
| `zone.defaultRadius` | `128.0` | Radius used by `css_lf_zone` when no radius is passed. `!lf_zone` 未给半径时使用的默认半径。 |
| `zone.fallbackAimDistance` | `1024.0` | How far in front of the player the zone is placed when the server cannot trace the crosshair. 服务端无法做射线检测时，落点范围放在玩家视线前方的距离。 |
| `general.language` | `"en"` | Player-facing text: `"en"` or `"zh-CN"`. 玩家可见文案语言。 |
| `general.verboseMenu` | `false` | Appends the search settings and the data directory to the bottom of `!lf_menu`, plus a line for the stub simulator or the missing trace backend while either is in use. 在菜单底部追加求解参数与存档目录，并在使用内置桩或射线后端不可用时各加一行提示。 |

`solver.maxTracesPerSolve` (default `200000`) caps the collision sweeps one
search may issue, so a broken candidate cannot bounce until the timeout fires.
It ships in the template and can be lowered on a busy server.
`solver.maxTracesPerSolve`（默认 `200000`）限制单次求解的碰撞检测次数上限，
模板中已包含，服务器负载高时可调低。

## 6. Data files / 数据文件

Under `data/`, read at map start
([data/README.md](../plugins/CS2LineupFinder/data/README.md)):

| Path | Purpose / 用途 |
| --- | --- |
| `data/maps.json` | Restricts which maps the plugin solves on. An empty `maps` list — the shipped default — means **every** map. 限制可用地图，空列表表示全部地图可用。 |
| `data/points.json` | Named positions an operator hands out (`kind` is `throw` or `land`). 命名点位。 |
| `data/lineups/<map>/<name>.json` | Line-ups written by `css_lf_save` and read by `css_lf_list` / `css_lf_load`. Created at runtime, git-ignored. 存档，运行时生成。 |

When `maps.json` restricts a map, only `css_lf_find` refuses to run there and says
so — the setup, drawing and saving commands keep working, so you can prepare a map
you have not enabled yet. / 当地图未被启用时，只有 `!lf_find` 会拒绝并给出提示，其余
指令（标记、画范围、存档）照常工作。

`data/lineups/` is not part of the build output; the plugin creates the map
directory on the first `!lf_save`. / `data/lineups/` 不在编译产物里，首次
`!lf_save` 时由插件自动创建。

## 7. Angle conventions / 角度约定

Worth reading before you compare a number in chat with `cl_showpos`.
在把聊天里的数值和 `cl_showpos` 对照之前请先读这一段。

**EN.** Inside `contracts/` **positive pitch looks up** (`yaw = 0` faces `+X`,
positive `yaw` turns towards `+Y`, yaw returned to players is normalized to
`-180..180`). The pitch CS2 shows you — in `setang` and in `cl_showpos` — is the
opposite sign: there **positive pitch looks down**. The plugin converts at the
boundary, so the numbers printed in chat are already in the **player convention**
and can be typed straight into `setang`.

**中文.** contracts 约定 **pitch 为正表示抬头**；而 CS2 玩家看到的
`setang` / `cl_showpos` 里 **pitch 为正表示低头**，两者互为相反数。插件在输出边界上做
转换，聊天里显示的是**玩家约定**的值，可以直接抄进 `setang`。

Units are Source world units (1 unit = 1 inch), seconds and degrees, right-handed
with `+Z` up. / 单位是 Source 世界单位（1 单位 = 1 英寸）、秒、度，右手坐标系，`+Z`
朝上。

## 8. Current status and known limitations / 当前状态与已知限制

- **Stubs make the shell runnable on its own.** The plugin ships
  `StubTrajectorySimulator` and `StubLineupSolver`, so the whole
  mark → zone → find → save flow works even with no physics layer deployed. The
  stub answers are geometric placeholders, not real CS2 ballistics — do not trust
  them as line-ups.
  插件内置桩模拟器与桩求解器，未部署 Core/Math 时也能跑通全流程；但桩给出的结果是几何
  占位值，不是真实弹道，不能当作可用瞄点。
- **Real ballistics needs two more DLLs.** Drop `CS2LineupFinder.Core.dll` and
  `CS2LineupFinder.Math.dll` next to `CS2LineupFinder.dll` and restart; the plugin
  discovers them at load and logs which simulator and solver it picked. Without
  them it logs `... is not installed, keeping the built-in simulator.` and carries
  on. `src/CS2LineupFinder.Math` is still on its own branch
  (`feat/math-core`), so it is absent from a `feat/plugin-shell` checkout.
  真实弹道需要把 `CS2LineupFinder.Core.dll` / `CS2LineupFinder.Math.dll` 放到插件
  目录旁边并重启；缺失时插件会打日志说明并继续使用内置桩。Math 层目前还在
  `feat/math-core` 分支上，所以在 `feat/plugin-shell` 上找不到该目录。
- **Map tracing needs CounterStrikeSharp ≥ 1.0.369 (.NET 10).** Crosshair aiming
  and bounce collisions use the managed `Trace` API, which the plugin binds by
  reflection because it is compiled for .NET 8. On an older host the plugin does
  not crash: the landing zone is placed a fixed distance in front of the player's
  view (`zone.fallbackAimDistance`, 1024u by default), thrown grenades are assumed
  to fly unimpeded, and a warning is printed once at load.
  射线检测依赖 CSSharp ≥ 1.0.369（.NET 10）。旧版本上插件不会崩，落点范围会退化为
  "玩家视线前方固定距离"（默认 1024u，见 `zone.fallbackAimDistance`），并在载入时打印
  一次警告。
- **Per-map state is reset on map change.** Sessions, selected results and beams
  are positions on a map, so a map start clears every session and reloads the
  catalogue. Saved line-ups on disk are unaffected.
  换图会清空玩家会话与光束并重读目录文件；磁盘上的存档不受影响。
- Server-side only; no client mod, no `autoexec` needed. The plugin is version
  `0.1.0` and the command surface may still grow.
  纯服务端插件，客户端无需任何改动；当前版本 0.1.0，指令面仍可能增加。

## 9. Links / 链接

- [INSTALL.md](INSTALL.md) — server setup and deployment / 服务器部署
- [USAGE.md](USAGE.md) — every command, worked examples / 逐条指令与示例
- [CONTRIBUTING.md](CONTRIBUTING.md) — how the four layers are built and merged / 分层协作与合并
- [PHYSICS.md](PHYSICS.md) — the ballistic model behind `Core` / 弹道模型
- [../PROJECT_LAYOUT.md](../PROJECT_LAYOUT.md) — repository layout and ownership rules
- [../plugins/CS2LineupFinder/config/config.toml](../plugins/CS2LineupFinder/config/config.toml) — shipped configuration template
- [../plugins/CS2LineupFinder/data/README.md](../plugins/CS2LineupFinder/data/README.md) — data files
- [../.github/workflows/build.yml](../.github/workflows/build.yml) — CI
