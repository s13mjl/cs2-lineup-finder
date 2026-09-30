# Plugin data directory

Everything in here can be edited on a running server without stopping it, as long
as you know which file is re-read when: `maps.json` and `points.json` are read at
map start, and `lineups/` is read on every command. There is no reload command for
the catalogue, so a change to the first two takes effect on the next map change or
plugin reload. Nothing in this directory is required - delete a file and the
plugin falls back to the built-in default shown below.

| Path | Read by | Purpose |
| --- | --- | --- |
| `maps.json` | `MapCatalog` | Restricts which maps the plugin solves on. |
| `points.json` | `MapCatalog` | Named positions you want to hand out to players. |
| `lineups/<map>/*.json` | `LineupRepository` | Line-ups written by `css_lf_save`. Git-ignored. |

## maps.json

```json
{ "maps": ["de_mirage", "de_dust2"] }
```

An empty list (the shipped default) means **every** map is allowed. When the list
is not empty, `css_lf_find` on any other map answers "The line-up finder is
disabled on <map>." instead of spending server time. The other commands keep
working, so you can still set up and save on a map you have not enabled yet.

A malformed file is reported once in the server console at map start and then
ignored, so a stray comma cannot stop the plugin from loading.

## points.json

```json
{
  "maps": {
    "de_mirage": [
      { "name": "window", "kind": "land", "note": "A site connector", "x": -760, "y": -820, "z": 60 }
    ]
  }
}
```

`kind` is `throw` for a spot a player should stand on, or `land` for a
target. Coordinates are Source world units, the same ones `css_lf_start` prints
in chat after marking a throw point.

## lineups/

One directory per map, one file per saved line-up. `css_lf_save` creates
them, `css_lf_list` reads them. The directory is git-ignored because its
contents are server state. The file schema is in
[USAGE.md](../../../docs/USAGE.md#saved-file-schema).
