# USAGE.md - Player and operator guide / 使用说明

Every command the plugin registers: what it does, what it answers, and what can
go wrong. Player-facing text follows `general.language` in `config/config.toml`
(`en` or `zh-CN`); every example below shows the English form.

插件注册的全部指令。每个指令都同时提供玩家聊天别名（`!lf_`）与控制台名称
（`css_lf_`），两者行为完全一致。玩家可见文案由 `config/config.toml` 中的
`general.language` 决定（`en` 或 `zh-CN`）。

## Conventions / 通用约定

| Term | Meaning |
| --- | --- |
| Throw point (投掷点) | The spot the player stands on when throwing. Marked with `css_lf_start`. |
| Landing zone (落点范围) | The area the grenade has to land inside. Created with `css_lf_zone`. |
| Result / line-up (瞄点) | A yaw/pitch pair plus the throw settings that lands the grenade in the zone. |
| `u` | Source world units. 1 unit = 1 inch. |

Angles are printed the way the game shows them, so a yaw/pitch pair can be typed
straight into `setang`. Yaw is normalized to `-180..180` and displayed
counter-clockwise from `+X`; **pitch is displayed positive looking down**, which is
the engine convention and the mirror image of the internal contract convention.
Both are rounded to 0.1 degree.

角度按游戏内约定显示（俯仰正=向下），可直接填入 `setang`；均为 0.1 度精度。

Players at the world origin `(0, 0, 0)` are a legal, supported position: a line-up
saved there loads back exactly like any other.

## Command overview / 指令总览

| Console | Chat | Arguments | What it does |
| --- | --- | --- | --- |
| `css_lf_menu` | `!lf_menu` | none | Opens the interactive menu. |
| `css_lf_start` | `!lf_start` | none | Marks the current position as the throw point. |
| `css_lf_zone` | `!lf_zone` | `circle <radius>` \| `rect <width> <height>` | Creates the landing zone around the point you are looking at. |
| `css_lf_grenade` | `!lf_grenade` | `smoke` \| `flash` \| `molotov` \| `he` | Selects the grenade. |
| `css_lf_throw` | `!lf_throw` | `stand` \| `crouch` \| `jump` | Selects the throw stance. |
| `css_lf_button` | `!lf_button` | `primary` \| `secondary` \| `both` | Selects the mouse button combination. |
| `css_lf_find` | `!lf_find` | none | Searches for a line-up and draws the result. |
| `css_lf_save` | `!lf_save` | `<name>` | Saves the selected result under a name. |
| `css_lf_load` | `!lf_load` | `<name>` | Loads a saved line-up into your session. |
| `css_lf_list` | `!lf_list` | none | Lists the line-ups saved on this map. |
| `css_lf_draw` | `!lf_draw` | none | Redraws the latest result in the world. |
| `css_lf_set` | `!lf_set` | `<index>` | Picks one of the results of the last search. |
| `css_lf_clear` | `!lf_clear` | none | Clears the throw point, the landing zone and the results. |
| `css_lf_reload` | `!lf_reload` | none | Reloads `config/config.toml`. |

Every command acts on a *player's* session, so all fourteen have to come from a
player: typed at the server console they answer
`This command runs on a player, not the server console.`

## css_lf_menu

```
!lf_menu
```

Opens a chat menu titled with the current session, e.g.
`Line-up finder - origin set | zone circle r=128u @ (500, 0, 0) | smoke | stand | primary`.
The menu stays open after a click, so the three settings can be cycled in a row.

`css_lf_set` and `css_lf_save` appear greyed out until a search has produced a
result, and they carry the exact syntax to type instead of being clickable.

> **[Screenshot: `docs/images/menu.png` - the `!lf_menu` list in chat]**

## css_lf_start

```
!lf_start
!lf_start          (while crouched, to record the crouched release height)
```

Records the player's feet, eyes and current velocity as the throw point. The
stance selected with `css_lf_throw` decides which eye height is stored: 64 units
standing, 46 units crouched. A jump throw additionally uses the player's velocity
at the moment of marking, so mark *while* running if the line-up is a run-up.

Answer: `Marked throw point at (1234, -567, 64).`

The player must be alive and spawned; otherwise the plugin answers
`You need to be alive to use that command.`

> **[Screenshot: `docs/images/start.png` - the throw-origin marker on the ground]**

## css_lf_zone

```
!lf_zone circle 128
!lf_zone circle 64
!lf_zone rect 200 120
```

Creates the landing zone centred on the ground point the crosshair is pointing at.
You aim at the target, then pick the shape and size. `circle` takes a radius,
`rect` takes a width and a height (along the world axes, not the player's facing).
Omitting the numbers uses `zone.defaultRadius` from the configuration, 128 units
by default:

```
!lf_zone
Answer: Landing zone: circle r=128u @ (500, 0, 0).
```

A non-positive radius is rejected with `Radius must be a positive number.`; a
non-positive width or height with `Width and height must be positive numbers.`

On a server whose CSSharp build cannot trace the map, the zone cannot be placed on
the surface you are looking at. The plugin then places it a fixed distance along
your view direction (`zone.fallbackAimDistance`, 1024 units) and says so:

```
Answer: This server cannot trace the map, so the zone was placed 1024u along your view.
```

> **[Screenshot: `docs/images/zone.png` - the ring drawn around the target]**

## css_lf_grenade

```
!lf_grenade smoke
!lf_grenade flash
!lf_grenade molotov
!lf_grenade he
```

Selects the grenade family. Forgiving aliases are accepted: `flashbang`,
`molo`/`fire` and `frag`. Answer: `Grenade set to smoke.`

## css_lf_throw

```
!lf_throw stand
!lf_throw crouch
!lf_throw jump
```

Selects the stance the line-up is thrown from. This changes both the recorded
release height and the physics the solver uses, so it has to match how you will
actually throw. `standing`, `crouching` and `jumping` are accepted too. Answer:
`Throw mode set to crouch.`

## css_lf_button

```
!lf_button primary
!lf_button secondary
!lf_button both
```

Selects the mouse button combination, which is what decides the release speed:
`primary` is a full throw (left mouse held), `secondary` is the short underhand
throw (right mouse), `both` is the medium throw (both buttons). The game's names
are `attack`, `attack2` and `attack3`; `left`/`lmb`, `right`/`rmb` and
`middle`/`mmb` are accepted as aliases. Answer: `Mouse button set to both.`

## css_lf_find

```
!lf_find
```

Searches for a throw from the marked point into the marked zone and reports the
best angles. Requires both a throw point and a landing zone; otherwise the plugin
answers `Set a throw point first: css_lf_start.` or
`Set a landing zone first: css_lf_zone circle <radius>.`

The search runs off the game thread with a wall-clock budget
(`solver.timeoutSeconds`, 3 seconds by default). A typical answer:

```
Searching smoke line-up...
Found 3 line-up(s) in 0.4s (solver: stub-coarse-grid-v0).
  #1  yaw -35.0  pitch -12.5  miss 3.0u
  #2  yaw -33.5  pitch -11.0  miss 9.0u
  #3  yaw -36.5  pitch -14.0  miss 15.5u
```

`yaw` and `pitch` are the angles to aim with, `miss` is the planar distance from
the impact point to the centre of the zone. Results are sorted best first, and the
first one is drawn in the world: a beam from your eyes along the aim direction and
a cross at the impact point, both for `visuals.beamDurationSeconds` (3 seconds).

If nothing lands inside the zone you get `No line-up found: ...`, and if the
budget expires, `Search timed out after 3.0s.` A player can only have one search in
flight; a second `!lf_find` answers `A search is already running.`

> **[Screenshot: `docs/images/find.png` - the beam and the impact cross]**

## css_lf_set

```
!lf_set 2
```

Selects a different result of the last search by its index, and redraws the beam
and the impact cross for it. Without a previous search, or with an index outside
the result list, the plugin answers with the syntax to type.

## css_lf_save

```
!lf_save a_site_smoke
!lf_save window from T spawn
```

Stores the currently selected result as a JSON file under
`data/lineups/<map>/<name>.json`. Names may contain letters, digits, `-` and `_`;
everything else is replaced, and a name whose only characters are separators is
rejected with `Invalid name: use letters, digits, '-' or '_'.` The file name stem
is the sanitized name.

Answer: `Saved line-up 'a_site_smoke' to .../data/lineups/de_mirage/a_site_smoke.json.`

Nothing is saved before a search has produced a result:
`Nothing to save yet: run css_lf_find first.`

> **[Screenshot: `docs/images/save.png` - the saved confirmation and the file]**

## css_lf_load

```
!lf_load a_site_smoke
```

Restores a saved line-up: the throw point, the landing zone, the grenade, the
stance, the button, and the stored angles as the current result. The zone and the
beam are drawn straight away, so a loaded line-up can be walked through without a
new search. `css_lf_save` followed by `css_lf_load` returns exactly the values
that were stored.

Answer: `Loaded line-up 'a_site_smoke': yaw -35.0 pitch -12.5.` An unknown name
answers `Line-up 'a_site_smoke' not found on this map.`, and a file that cannot be
parsed reports `Could not read the line-up: ...` while the rest of the plugin keeps
working.

## css_lf_list

```
!lf_list
```

Lists the line-ups saved for the current map, ordered by name:

```
2 line-up(s) saved on de_mirage.
  a_site_smoke  (smoke stand/primary yaw -35.0 pitch -12.5 by player_one)
  window       (flash jump/both yaw 12.5 pitch -8.0 by player_two)
```

## css_lf_draw

```
!lf_draw
```

Redraws the beam and the impact marker for the selected result without running a
new search. Answer: `Beam drawn for 3.0s.` With visualization switched off the
plugin answers `Visuals are disabled in config.toml.`

> **[Screenshot: `docs/images/draw.png` - the redrawn beam from a loaded line-up]**

## css_lf_clear

```
!lf_clear
```

Forgets the throw point, the landing zone and the results, and removes everything
the plugin has drawn for that player. Answer: `Selection cleared.`

## css_lf_reload

```
!lf_reload
```

Re-reads `config/config.toml` from the plugin directory. Answer:
`Config reloaded: <settings>`, and any key the plugin does not recognize is logged
as a warning in the server console instead of being ignored silently.

This reloads the configuration only. `data/maps.json` and `data/points.json` are
read at map start, so a change to those takes effect on the next map change or
plugin reload.

## Saved file schema

<a id="saved-file-schema"></a>

One file per line-up at `data/lineups/<map>/<name>.json`.

```json
{
  "name": "a_site_smoke",
  "map": "de_mirage",
  "grenadeType": "smoke",
  "throwMode": "stand",
  "button": "primary",
  "origin": { "x": 1234.0, "y": -567.0, "z": 64.0 },
  "zone": { "type": "circle", "radius": 128.0 },
  "yaw": -35.0,
  "pitch": 12.5,
  "createdAt": "2026-09-30T12:00:00.0000000+00:00",
  "author": "player_one"
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `name` | string | Sanitized line-up name; equals the file name stem. |
| `map` | string | Map the line-up belongs to. |
| `grenadeType` | string | `smoke`, `flash`, `molotov` or `he`. |
| `throwMode` | string | `stand`, `crouch` or `jump`. |
| `button` | string | `primary`, `secondary` or `both`. |
| `origin` | object | `x`, `y`, `z` of the player's feet, in world units. |
| `zone.type` | string | `circle` or `rect`. |
| `zone.radius` | number | Circle radius. Written for circles. |
| `zone.width` / `zone.height` | number | Rectangle extents. Written for rectangles. |
| `yaw`, `pitch` | number | Degrees. `pitch` is stored in the internal convention, positive looking **up**, which is the mirror image of the value shown in chat; `css_lf_load` converts it back. |
| `createdAt` | string | ISO-8601 UTC with offset. |
| `author` | string | Steam persona name of the player who saved it. |

Enums are written as readable strings, so a saved file stays diffable and survives
an enum reordering. Fields whose value is zero (`radius`, `width`, `height`) are
omitted rather than written as `0`, so treat a missing extent as not applicable to
that zone type rather than as zero.

The directory is git-ignored: it holds server state, including player names.

## Constants the answers are built from

Recorded here because they explain the numbers a player sees. They live in
`GameMovementProvider` and the shipped configuration, not in the saved file.

| Constant | Value | Used for |
| --- | --- | --- |
| Standing eye height | 64 u | Throw point eye line, standing. |
| Crouched eye height | 46 u | Throw point eye line, crouched. |
| Jump velocity | 300 u/s | Upward velocity added by a jump throw. |
| Release offset | +16 u forward, +8 u up | Where the grenade leaves the hand. |
| Fallback aim distance | 1024 u | Zone placement when the map cannot be traced. |
| Default zone radius | 128 u | `css_lf_zone` without a radius. |
| Solve budget | 3.0 s | `solver.timeoutSeconds`. |
| Maximum results | 5 | `solver.maxResults`. |
| Release speed, `primary` | 675 u/s | Full throw. |
| Release speed, `secondary` | 450 u/s | Underhand throw. |
| Release speed, `both` | 560 u/s | Medium throw. |

The three release speeds and the gravity used while a real simulator is not
installed belong to the built-in stub. Once `CS2LineupFinder.Core.dll` is present
the physics come from `PhysicsParameters` instead, and the angles can differ by a
degree or so on long throws - see `PHYSICS.md` for those values and their sources.

## Troubleshooting / 常见问题

| Symptom | Cause |
| --- | --- |
| `Simulator is the built-in stub.` in the menu or on `!lf_find` | `CS2LineupFinder.Core.dll` is not next to the plugin, or it was built against `CS2LineupFinder.Abstractions.dll`. The server console names which one. |
| `This server cannot trace the map...` | The CSSharp build in use has no managed trace API. Zones fall back to a fixed distance along the view. |
| `The line-up finder is disabled on <map>.` | `data/maps.json` restricts the plugin to a list of maps that does not include this one. |
| `Search timed out after 3.0s.` | The budget is too small for the map, or the zone is very large. Raise `solver.timeoutSeconds`. |
| Commands do nothing at all | The plugin failed to load; typed commands would answer with the reason, so check the server console for `CS2LineupFinder failed to start`. |

## Related documents

* [INSTALL.md](INSTALL.md) - server and plugin installation.
* [README.md](README.md) - architecture and configuration reference.
* [PHYSICS.md](PHYSICS.md) - the ballistic model behind the angles.
* [../plugins/CS2LineupFinder/data/README.md](../plugins/CS2LineupFinder/data/README.md) - the data directory.
