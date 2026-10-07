# 安装与部署 / Installation

面向运维。命令与日志保留英文原文（那是服务器实际会打印的内容），说明用中文。
Commands and log lines are kept in English because that is what the server prints.

产物名称：CI artifact `CS2LineupFinder-plugin`（见
[../.github/workflows/build.yml](../.github/workflows/build.yml)）。玩家侧使用说明见
[README.md](README.md) 与 [USAGE.md](USAGE.md)。

---

## 1. 前置条件

三层依赖，从下往上装，缺一不可：

| 组件 | 装到哪里 | 说明 |
| --- | --- | --- |
| CS2 专用服务器 | `<server>/game/csgo/` | SteamCMD app 730 |
| Metamod:Source 2.x | `<server>/game/csgo/addons/metamod/` | 插件宿主的最底层 |
| CounterStrikeSharp | `<server>/game/csgo/addons/counterstrikesharp/` | 本插件直接跑在它上面 |

### 1.1 CS2 专用服务器（SteamCMD app 730）

Windows PowerShell：

```powershell
# 每次拉取/更新服务端
.\steamcmd.exe `
  +force_install_dir "D:\cs2-server" `
  +login anonymous `
  +app_update 730 validate `
  +quit
```

Linux：

```bash
./steamcmd.sh \
  +force_install_dir "$HOME/cs2-server" \
  +login anonymous \
  +app_update 730 validate \
  +quit
```

> `730` 是 CS2 专用服务器的 AppID；下载完成后安装目录里会出现 `game/csgo/`，本文用
> `<server>/game/csgo/` 指代它。

启动一个练习服需要 GSL token（在 Steam 开发者密钥页申请），否则客户端连不上：

```
+sv_setsteamaccount <GSL_TOKEN>
```

本插件是纯服务端插件，客户端不需要任何文件、`autoexec` 或 mod。

### 1.2 Metamod:Source 2.x

1. 下载 Metamod:Source 2.x 对应平台（Windows 或 Linux）的压缩包。
2. 解压得到 `addons/` 目录，把它放进 `<server>/game/csgo/`，即得到
   `game/csgo/addons/metamod/`。
3. 编辑 `<server>/game/csgo/gameinfo.gi`，在 `SearchPaths` 块的**最前面**插入一行：

```
        Game    csgo/addons/metamod
```

例如：

```
"SearchPaths"
{
        Game    csgo/addons/metamod     // <-- 新增这一行
        Game    csgo_gamemod
        ...
}
```

4. 其余启动参数按 Metamod:Source 文档配置。服务器日志出现
   `Metamod:Source version 2.x` 即加载成功，再往上装 CSSharp。

### 1.3 CounterStrikeSharp

1. 从 CounterStrikeSharp 的官方 release 页下载 `CounterStrikeSharp.zip`。
2. 解压到 `<server>/game/csgo/`，得到
   `game/csgo/addons/counterstrikesharp/`。
3. 首次启动服务器后，CSSharp 会在
   `game/csgo/addons/counterstrikesharp/configs/` 下生成自身配置。
4. 日志出现 `CounterStrikeSharp` 的版本行并显示已加载插件列表，即成功。

> **版本很关键**，见第 4 节：本插件按 .NET 8 编译，而射线检测需要 1.0.369+。

---

## 2. 部署本插件

### 2.1 取产物

**方式 A：CI artifact。** 下载 `CS2LineupFinder-plugin`，解压。

**方式 B：本地构建**（仓库没有 `.sln`，必须指名工程）：

```powershell
# Windows，仓库根目录
dotnet build plugins\CS2LineupFinder\CS2LineupFinder.csproj -c Release
```

```bash
# Linux / macOS
dotnet build plugins/CS2LineupFinder/CS2LineupFinder.csproj -c Release
```

产物目录（**整个目录都要拷走**，不要只拷 DLL）：

```
plugins/CS2LineupFinder/bin/Release/net8.0/
```

### 2.2 放置

把上表目录内容放到服务器的插件目录
`game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder/`：

```
addons/counterstrikesharp/plugins/CS2LineupFinder/
├── CS2LineupFinder.dll
├── CS2LineupFinder.pdb          (可选，便于崩溃栈显示行号)
├── CS2LineupFinder.xml          (可选，文档注释)
├── CS2LineupFinder.deps.json    (可选)
├── config/config.toml
└── data/
    ├── maps.json
    └── points.json
```

一条命令完成（Windows，在仓库根目录，`<server>` 换成你的服务器路径）：

```powershell
$dst = "<server>\game\csgo\addons\counterstrikesharp\plugins\CS2LineupFinder"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Recurse -Force "plugins\CS2LineupFinder\bin\Release\net8.0\*" $dst
```

Linux：

```bash
dst="<server>/game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder"
mkdir -p "$dst"
cp -rf plugins/CS2LineupFinder/bin/Release/net8.0/. "$dst/"
```

要点：

- **文件名必须是 `CS2LineupFinder.dll`**，且直接位于 `CS2LineupFinder/` 目录里（不是
  再套一层 `net8.0/`）。CSSharp 在 `plugins/` 的每个子目录中寻找插件 DLL 并加载其中的
  `BasePlugin` 实现。
- `config/config.toml` 与 `data/*.json` 必须在**同一个插件目录**下。插件读的是自己
  模块目录里的 `config/config.toml`；CSSharp 的
  `configs/plugins/CS2LineupFinder/` 不是本插件的配置来源，放进那里的文件不会被读取。
- `data/lineups/`（存档目录）**不需要预先创建**，首次 `!lf_save` 时插件会自动建
  对应地图的子目录。
- 少一个文件不会导致加载失败：缺 `config.toml` 用内置默认值，缺 `maps.json` 视为"所有
  地图可用"，缺 `points.json` 则命名点位为空。

### 2.3 验证

1. 重启服务器。控制台/日志应出现（形如）：

```
CS2LineupFinder: map tracing is available through the managed Trace API.
CS2LineupFinder: CS2LineupFinder.Core.dll is not installed, keeping the built-in simulator.
CS2LineupFinder: CS2LineupFinder.Math.dll is not installed, keeping the built-in solver.
CS2LineupFinder loaded: timeout=3s maxResults=5 visuals=True lang=en reflection simulator=StubTrajectorySimulator solver=StubLineupSolver stub=True
```

2. 换图时还有一条：

```
CS2LineupFinder on de_mirage: ... points=0 enabled=True
```

3. 进服，聊天输入 `!lf_menu`（或控制台 `css_lf_menu`）应弹出菜单。
4. 完整跑一遍：站到投掷点 `!lf_start` → 准星指向落点 `!lf_zone circle 128` →
   `!lf_grenade smoke` → `!lf_find`。桩求解器也会画出光束，因此这一步只验证"装好了"，
   不代表瞄点可用（见第 5 节）。

---

## 3. 部署真实弹道层（可选）

插件内置 `StubTrajectorySimulator` / `StubLineupSolver`，不装 Core/Math 也能跑通全流程，
但结果不是真实弹道。要拿到可用瞄点，把两个 DLL 放到 `CS2LineupFinder.dll` **旁边**并重启：

```
addons/counterstrikesharp/plugins/CS2LineupFinder/
├── CS2LineupFinder.dll
├── CS2LineupFinder.Core.dll     <-- 新增
└── CS2LineupFinder.Math.dll     <-- 新增
```

插件在载入时扫描这两个文件名，反射出其中实现契约接口的类型，并在日志里报告实际选中的
simulator / solver 名字。加载成功时那两行 `is not installed` 会消失。

要求（这两条不满足时**不会崩**，只是继续用内置桩）：

- 目标类型必须实现插件自己的 `CS2LineupFinder.Contracts.ITrajectorySimulator` /
  `ILineupSolver`，并且有public 无参构造函数。
- 契约必须是**编译进各自 DLL** 的那一份，见第 6 节"两个接口类型不可互换"。

---

## 4. 运行时版本（务必读完，这是最容易踩的坑）

- 本插件的编译目标是 **.NET 8**。CounterStrikeSharp **1.0.368 是最后一个提供
  `lib/net8.0` 的版本**，1.0.369 起只提供 `lib/net10.0`。
- 这不是阻碍：**按 net8 编译的插件 DLL 在 .NET 10 的宿主里可以正常加载**，运行时向后
  兼容旧目标框架的程序集。仓库里的 `plugins/CS2LineupFinder/CS2LineupFinder.csproj`
  正是钉在 `CounterStrikeSharp.API` 1.0.368 上做编译期引用的。
- 但**地图射线检测**（准星取落点、道具弹跳碰撞）依赖 CSSharp **1.0.369+** 的托管
  `Trace` API。插件在 1.0.368 上编译不到这个 API，因此在运行时用反射绑定它。
  - CSSharp ≥ 1.0.369（宿主为 .NET 10）：绑定成功，日志
    `map tracing is available through the managed Trace API.`，准星取点与碰撞都是真的。
  - CSSharp < 1.0.369：绑定失败，插件**不会崩**，落点范围被放在玩家视线前方固定距离
    （`zone.fallbackAimDistance`，默认 `1024.0` u），道具被当作无障碍飞行，并在载入时
    打印一次警告：

```
CS2LineupFinder: the managed Trace API is unavailable on this CounterStrikeSharp build (...).
Map tracing is unavailable on this server, so crosshair based zones fall back to the aim distance and thrown grenades are assumed to fly unimpeded. Install CounterStrikeSharp 1.0.369 or newer (which requires the .NET 10 runtime) to enable traced line-ups.
```

**建议：** 除非服务器被锁死在 .NET 8，就把 CSSharp 升到 1.0.369 以上。用哪个版本，
第 2.3 节日志里的 `reflection` / `none` 就是答案。

---

## 5. 升级与卸载

**升级。** 覆盖 DLL 即可：停服 → 用新产物覆盖
`plugins/CS2LineupFinder/` 下的文件（至少 `CS2LineupFinder.dll`）→ 启服。
`config/config.toml` 与 `data/` 是你的服务器状态，**不要覆盖**：新版本模板里若增加了
键，把它们逐个补进现有文件。开启 CSSharp 热重载时也可换 DLL 后热载，但涉及反射绑定
射线 API，稳妥做法仍是重启。

**升级 CS2 服务端本体后必查两处。** SteamCMD 更新 `app 730` 会重置
`<server>/game/csgo/gameinfo.gi`（丢掉 `Game csgo/addons/metamod` 挂载行），并可能删除
`addons/metamod/metamod.vdf`。症状是启动后 Metamod/CSSharp 完全不加载。每次更新服务端后
先检查这两处，缺了就补回：

```
# gameinfo.gi 的 SearchPaths 块内，Game_LowViolence 一行之后：
			Game		csgo/addons/metamod
```

`metamod.vdf`（UTF-8 无 BOM，文件名任意，放 `addons/metamod/` 下即可）：

```
"Metamod Plugin"
{
	"file"	"addons/metamod/bin/win64/metamod.2.cs2"
}
```

注意编辑 `gameinfo.gi` 必须以 **UTF-8 无 BOM** 保存，带 BOM 会让 KeyValues 解析整体失败。

改完配置不必重启：进服执行 `!lf_reload`（它必须在玩家会话里跑，服务器控制台执行会被
拒绝）立刻生效，并在聊天回显当前配置。

**卸载。** 删除 `addons/counterstrikesharp/plugins/CS2LineupFinder/` 整个目录，重启
服务器。没有其它写入点：插件只在 CSSharp 的插件目录下活动。

**清空点位。** 存档都在
`plugins/CS2LineupFinder/data/lineups/<地图>/<名称>.json`，删除 `data/lineups/` 即清空
全部；删除其中某个地图目录只清那张图。命名点位是
`data/points.json`（人工维护的输入文件，不是存档）。

---

## 6. 故障排查

先看第 2.3 节的四条日志，它们已经回答了大半问题。

| 症状 | 原因 | 处理 |
| --- | --- | --- |
| 服务器控制台完全没有 CS2LineupFinder 日志 | DLL 路径不对，或文件名不是 `CS2LineupFinder.dll` | 确认它在 `game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder/CS2LineupFinder.dll`；不要多套一层 `net8.0/`，也不要改名 |
| 日志有加载记录但 `!lf_menu` 没反应 | CSSharp 没装好，或聊天触发词没有改写成功 | 在**服务器控制台**输入 `css_lf_menu` 做一次判别：回复"This command runs on a player, not the server console."说明指令已注册（问题在聊天触发，见下一行）；毫无反应说明插件其实没加载起来，回到本表第一行 |
| 控制台执行任何 `css_lf_*` 都回"This command runs on a player, not the server console." | 属预期行为：这些指令需要一个真实玩家会话 | 进服由玩家执行，或在聊天里用 `!lf_*` |
| `CS2LineupFinder.Core.dll is not installed, keeping the built-in simulator.` | **正常**——未部署 Core | 想要真实弹道就按第 3 节部署；否则忽略，全流程仍可用 |
| 日志末尾是 `simulator=StubTrajectorySimulator solver=StubLineupSolver stub=True` | 正在用内置桩 | 同上：桩的数值是几何占位，不能当瞄点用 |
| `... its simulator does not implement ... from this plugin ... the two interface types are not interchangeable` | Core/Math 是引用 `CS2LineupFinder.Abstractions.dll` 构建的，而插件把 contracts 编译进了自己这一个 DLL，于是两边各有一个同名但不同的接口类型 | 让 Core / Math **把 `contracts/` 的源文件编译进自己的 DLL**（和插件同样的做法），别引用 `Abstractions.dll`；重新构建后再放进来 |
| `... Core.dll could not be loaded: ...` / `BadImageFormatException` | 目标框架或平台不匹配（例如拿了别的运行时的构建） | 用与本服务器运行时一致的构建；net8 的 DLL 在 .NET 10 宿主里可用 |
| 落点范围总在同一距离，且日志出现 `Map tracing is unavailable` / 末尾是 `none` | 服务端 CSSharp 版本低于 1.0.369，没有托管 `Trace` API | 升级 CSSharp 到 1.0.369+（需要 .NET 10 运行时），见第 4 节；确实不能升就调 `zone.fallbackAimDistance` 凑合 |
| 聊天里仍是英文 | `config/config.toml` 的 `general.language` 是 `"en"` | 改成 `language = "zh-CN"`（写 `zh` 开头的值也会被规范化为 `zh-CN`），然后 `!lf_reload` |
| 控制台出现 `config.toml has an unknown key '...'` | 配置文件里有插件不认识的键（拼写错误或旧键名） | 按第 5 节键表更正；这只是一条警告，不影响加载 |
| 改了 `config/config.toml` 但行为没变 | 编辑的不是插件读取的那份 | 确认改的是插件目录下 `config/config.toml`，然后执行 `!lf_reload` |
| `!lf_find` 回答 "The line-up finder is disabled on <map>." | `data/maps.json` 里限制了地图 | 把该地图加进 `maps` 列表，或让列表为空表示全部地图可用 |
| 求解总是超时 | 求解预算太小，或地图碰撞开销大 | 调大 `solver.timeoutSeconds`（默认 3.0），或调小 `solver.maxCandidates`（默认 64）减少候选量 |
| 菜单最后一行显示求解参数、数据目录等 | `general.verboseMenu = true` | 对玩家嘈杂，正式服建议改回 `false` |

还有问题：先跑 `!lf_menu` 并打开 `general.verboseMenu`，它会在菜单底部补上求解参数
（候选数/超时/结果上限）与当前存档目录，并且在仍在使用内置桩、或射线后端不可用时各加
一行提示。这几行就是定位问题的全部线索。

---

## 7. 相关文件

- 插件配置模板：[../plugins/CS2LineupFinder/config/config.toml](../plugins/CS2LineupFinder/config/config.toml)
- 数据文件说明：[../plugins/CS2LineupFinder/data/README.md](../plugins/CS2LineupFinder/data/README.md)
- 项目分层与归属：[../PROJECT_LAYOUT.md](../PROJECT_LAYOUT.md)
- 功能与指令总览：[README.md](README.md)
- 玩家使用说明：[USAGE.md](USAGE.md)
