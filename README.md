# CS2 Lineup Finder

A server-side plugin for Counter-Strike 2 practice servers that answers the
reverse question of trajectory preview: given a throw point and a desired
landing zone, find the view angles, stance and mouse buttons that land the
grenade inside the zone.

CS2 跑图服务器的瞄点预测插件：给定投掷点与期望落点范围，反解出瞄准角度、
投掷姿势与鼠标按键组合。纯服务端，玩家客户端无需安装任何内容。

## Features / 功能

- Reverse solver: mark a throw point and a circular or rectangular landing
  zone, get yaw/pitch (0.1 degree) plus stance and button recommendation.
- Forward ballistics simulator with adaptive sweep tracing, bounce/friction/drag
  and per-grenade detonation, calibrated against Valve-sourced constants.
- In-world visualization: aim beam, landing ring, impact cross.
- Persistence: save/load named lineups as JSON per map; share by copying files.
- Bilingual player text (English / 简体中文), full console and chat command set.

## Requirements / 前置

- CS2 dedicated server (SteamCMD app 730)
- Metamod:Source 2.x
- CounterStrikeSharp 1.0.368 (last release shipping a lib/net8.0 target)

## Build / 构建

 `{``powershell
dotnet build CS2LineupFinder.slnx -c Release
dotnet test CS2LineupFinder.slnx
 `{``

## Install / 安装

Copy the build output to the server (see
[docs/INSTALL.md](docs/INSTALL.md) for the full walk-through including
Metamod and CounterStrikeSharp setup):

 `{``text
plugins/CS2LineupFinder/bin/Release/net8.0/
  -> game/csgo/addons/counterstrikesharp/plugins/CS2LineupFinder/
 `{``

Deployed files: the plugin DLL, CS2LineupFinder.Abstractions.dll (shared
contracts, see docs/contract-issues.md C-08) and config/config.toml.

## Player quick start / 玩家快速上手

 `{``text
!lf_start                 # mark the current spot as the throw point
!lf_zone circle 100       # landing zone: radius 100 around the crosshair ground point
!lf_grenade smoke         # smoke | flash | molotov | he
!lf_throw stand           # stand | crouch | jump
!lf_button primary        # primary | secondary | both
!lf_find                  # solve; results are drawn in the world
!lf_save my-lineup        # persist the selected result (data/lineups/*.json)
 `{``

All 14 commands, arguments and messages are documented in
[docs/USAGE.md](docs/USAGE.md).

## Architecture / 架构

 `{``text
plugins/  CS2LineupFinder      commands, menus, persistence, visualization
src/      CS2LineupFinder.Core    forward simulator, physics parameters, calibration
src/      CS2LineupFinder.Math    inverse solver, analytic ballistics, Nelder-Mead
contracts/CS2LineupFinder.Abstractions    shared interface assembly
tests/    one test project per layer, 172 tests total
 `{``

Plugin, Core and Math bind to one contracts assembly at runtime; the plugin
locates Core/Math DLLs next to itself and loads them without a rebuild.
Design notes, the physics parameter table and the solver algorithm live in
[docs/README.md](docs/README.md),
[docs/PHYSICS.md](docs/PHYSICS.md) and
[docs/SOLVER.md](docs/SOLVER.md).

## Status and limitations / 状态与已知限制

- Physics constants are Valve-sourced documentation values, not yet fitted to
  in-game recordings; run docs/PHYSICS.md's calibration procedure on a real
  server before trusting long throws. Dynamic geometry (doors, weapons on the
  ground) is ignored by the simulator.
- The crosshair ground trace used by the zone command requires the managed
  trace API (CSSharp 1.0.369+, .NET 10 runtime); on 1.0.368 it falls back to a
  fixed distance along the view direction.

## License

[MIT](LICENSE)
