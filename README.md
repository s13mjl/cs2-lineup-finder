# CS2 Lineup Finder

> CS2 跑图服务器的**瞄点预测**插件：给定投掷点与期望落点范围，反解出应该朝哪个角度瞄准（yaw/pitch）、用哪种投掷姿势和鼠标按键。
> 纯服务端实现，玩家客户端无需安装任何内容。
>
> English readers: 本文件为中文版说明，[docs/README.md](docs/README.md) 提供双语设计文档。

## 功能特性

- **逆向求解**：在地面标记投掷点，画一个圆形或矩形落点区，插件返回精确到 0.1 度的 yaw/pitch，以及推荐的姿势（站立 / 下蹲 / 跳跃）与按键（左键 / 右键 / 双键）。
- **正向弹道模拟**：自适应扫掠射线检测、碰撞反弹、地面摩擦、空气阻力、分道具引爆逻辑，常量取自 Valve 公开资料。
- **世界内可视化**：瞄准光束、落点圈、落点十字。
- **点位持久化**：按地图把命名点位存成 JSON，可加载、可复制文件分享。
- **双语玩家文案**：简体中文 / 英文，由配置文件切换。
- **完整指令集**：14 条指令，聊天前缀 `!lf_`，控制台前缀 `css_lf_`。

## 环境要求

- CS2 专用服务器（SteamCMD app 730）
- Metamod:Source 2.x
- CounterStrikeSharp 1.0.368（最后一个带 `lib/net8.0` 目标框架的版本）

## 构建与测试

```powershell
dotnet build CS2LineupFinder.slnx -c Release
dotnet test  CS2LineupFinder.slnx
```

注意仓库使用 .NET 10 的 `.slnx` 解决方案格式，没有 `.sln` 文件。三个测试工程共 172 个用例。

## 安装

把构建产物复制到服务器插件目录（Metamod 与 CounterStrikeSharp 的完整安装步骤见 [docs/INSTALL.md](docs/INSTALL.md)）：

```text
plugins/CS2LineupFinder/bin/Release/net8.0/
  -> game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder/
```

需要一并部署的文件：插件 DLL、`CS2LineupFinder.Abstractions.dll`（共享契约程序集，见 docs/contract-issues.md C-08）、`config/config.toml`。

## 玩家快速上手

```text
!lf_start              # 把当前位置标记为投掷点
!lf_zone circle 100    # 以准星地面点为中心、半径 100 的圆形落点区
!lf_grenade smoke      # 道具：smoke | flash | molotov | he
!lf_throw stand        # 姿势：stand | crouch | jump
!lf_button primary     # 按键：primary | secondary | both
!lf_find               # 求解，结果直接画在世界里
!lf_save my-lineup     # 保存选中结果到 data/lineups/*.json
```

其余指令：`!lf_list`、`!lf_load`、`!lf_set`、`!lf_draw`、`!lf_menu`、`!lf_clear`、`!lf_reload`。全部参数与提示文案见 [docs/USAGE.md](docs/USAGE.md)。
玩家看到的语言由 `config/config.toml` 里的 `general.language`（`zh-CN` / `en`）决定。

## 架构

```text
contracts/  CS2LineupFinder.Abstractions   共享契约程序集（Vec3、IWorldGeometry、GrenadeType…）
src/        CS2LineupFinder.Core           正向模拟、物理参数、校准工具
src/        CS2LineupFinder.Math           逆向求解、解析弹道、Nelder-Mead 优化
plugins/    CS2LineupFinder                指令、菜单、持久化、世界内可视化
tests/      每层一个测试工程，共 172 个用例
```

插件在运行时从自身目录旁加载 Core / Math 的 DLL，因此升级这两层不需要重新编译插件。
设计说明、物理参数表、求解算法分别见 [docs/README.md](docs/README.md)、[docs/PHYSICS.md](docs/PHYSICS.md)、[docs/SOLVER.md](docs/SOLVER.md)。

## 文档

- [docs/README.md](docs/README.md)：设计说明（双语）
- [docs/INSTALL.md](docs/INSTALL.md)：部署指南（以中文为主）
- [docs/USAGE.md](docs/USAGE.md)：14 条指令详解（双语）
- [docs/PHYSICS.md](docs/PHYSICS.md)：物理参数来源与校准流程
- [docs/SOLVER.md](docs/SOLVER.md)：逆向求解算法
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md)：开发约定
- [docs/contract-issues.md](docs/contract-issues.md)：跨模块契约问题记录

## 状态与已知限制

- 物理常量是**Valve 公开资料中的文档值**，尚未用游戏内实测轨迹拟合。信任长距离投掷前，请先按 [docs/PHYSICS.md](docs/PHYSICS.md) 的校准流程在真实服务器上采集数据并拟合。
- 模拟器只处理静态几何，忽略动态物体（门、地上的武器）。
- `css_lf_zone` 的准星地面取点依赖托管 trace API（CSSharp 1.0.369+ / .NET 10）；在 1.0.368 上退化为沿视线固定距离（默认 1024 单位）。

## 许可证

[MIT](LICENSE) © 2026 s13mjl

