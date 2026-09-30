# CONTRIBUTING.md - Working on CS2 Lineup Finder

The repository is built by parallel workstreams, so the rules here are mostly
about boundaries: who may edit what, how a layer asks for a change it cannot make
itself, and how the four layers come back together. The layout itself lives in
[../PROJECT_LAYOUT.md](../PROJECT_LAYOUT.md); this file is the working agreement
around it.

## Directory contract

| Path | Owner | May reference |
| --- | --- | --- |
| `contracts/` | integration | nothing (DTOs and interfaces only) |
| `src/CS2LineupFinder.Math/` | math-core | `contracts/` only |
| `src/CS2LineupFinder.Core/` | physics-sim | `contracts/` only |
| `plugins/CS2LineupFinder/` | plugin-shell | `contracts/` only |
| `tests/` | same owner as the layer under test | the layer under test |
| `docs/` | plugin-shell | - |
| `.github/workflows/` | plugin-shell | - |

Four rules follow from that table:

1. **Stay inside your directory.** A file you do not own is read-only, including
   the test project of another layer.
2. **`contracts/` changes go through
   [contract-issues.md](contract-issues.md).** Record the symptom, who it affects
   and the suggested change. The integration owner applies it, or decides not to.
   `contracts/` is frozen between merge windows, so the workaround - not the
   contract edit - is usually what unblocks you today.
3. **`plugins/` never compiles against `src/`.** It talks to the interfaces in
   `contracts/` only, and reaches the real implementations at runtime through
   `CoreLoader`. That is what keeps every layer independently testable while the
   others are in flight.
4. **No layer reaches into another layer's internals,** not even through
   `InternalsVisibleTo`. If a test needs a non-public member, expose it as
   `internal` inside your own layer and add your own friend assembly.

## Stubs and their markers

While a dependency is not merged yet, the consumer ships a stand-in marked with a
`// TODO: replace with real Core` or `// TODO: replace with real Math` comment, so
a single `rg` finds all of them:

```powershell
rg -n "TODO: replace with real" --glob '*.cs'
```

| Stand-in | File | Replaced by |
| --- | --- | --- |
| `StubTrajectorySimulator` | `plugins/CS2LineupFinder/Simulation/` | `ITrajectorySimulator` from `Core` |
| `StubLineupSolver` | `plugins/CS2LineupFinder/Simulation/` | `ILineupSolver` from `Math` |
| `CoreLoader` | `plugins/CS2LineupFinder/Simulation/` | nothing; it is the permanent seam |
| `NullTraceBackend` | `plugins/CS2LineupFinder/Game/` | nothing; it reports a missing engine API |

A stand-in must never be the basis of an assertion about real behaviour. The
plugin suite runs against the stubs and asserts the *plugin's* behaviour - that a
search returns within the budget, that a save/load round trip is lossless, that
every command has help text. Exact angles belong to the layer that computes them.

## Working on a shared tree

Several agents commit from one working tree, so a broad staging command can sweep
work that another agent has not committed yet. That has already happened once; see
P-01 in [contract-issues.md](contract-issues.md). Stage explicit paths, always:

```powershell
git add plugins/CS2LineupFinder/Commands/CommandSpec.cs tests/CS2LineupFinder.Plugin.Tests/RegistrationTests.cs
git commit -m "feat(plugin): ..."
```

Never run `git add -A`, `git add .`, `git clean`, `git checkout -- <path>` or
`git reset` in this tree. `git status` before every commit shows whose work is in
flight; leave anything you do not recognize uncommitted.

Commit messages follow conventional commits (`feat(plugin):`, `fix(core):`,
`test(math):`, `docs:`, `ci(build):`) with a scope naming the layer. The body
explains *why* the change is needed - the diff already says what changed.

## Build and test

There is no solution file: each project is built by path. The plugin and its tests
set `TreatWarningsAsErrors`, and the CI workflow compiles all three with
`-warnaserror`, so a warning is a build failure everywhere.

```powershell
dotnet build plugins\CS2LineupFinder\CS2LineupFinder.csproj -c Release -v q --nologo
dotnet build src\CS2LineupFinder.Core\CS2LineupFinder.Core.csproj -c Release -v q --nologo
dotnet test  tests\CS2LineupFinder.Plugin.Tests\CS2LineupFinder.Plugin.Tests.csproj -c Release --nologo
dotnet test  tests\CS2LineupFinder.Core.Tests\CS2LineupFinder.Core.Tests.csproj -c Release --nologo
```

To run one test: add `--no-build --filter "FullyQualifiedName~Registration"`.

## Testing the plugin without a server

`tests/CS2LineupFinder.Plugin.Tests/Harness.cs` builds a temporary plugin root with
a real `CommandService`, a real `LineupRepository` and fakes for everything that
would touch the engine. Use it instead of mocking by hand:

```csharp
using var harness = new Harness();
harness.PrepareSession();                 // throw point, circle zone, slot 3
var outcome = await harness.Service.FindAsync(Harness.Slot, harness.World);
Assert.Contains("Found", harness.Messages.Last);
```

Two rules keep the suite honest. Never construct the engine-facing types in a test
- `EngineVisuals` and `BeamRenderer` call CSSharp and cannot run outside the game;
the harness wires `FakeVisuals` in their place. And assert on the plugin's own
contract - reply text, file contents, command registration - rather than on
numbers that belong to a simulator or solver under someone else's ownership.

## Adding something to the saved schema

The file schema is frozen by the task brief (`name`, `map`, `grenadeType`,
`throwMode`, `button`, `origin`, `zone`, `yaw`, `pitch`, `createdAt`, `author`).
The pattern for a change that touches more than the plugin is:

1. Add the field to `plugins/CS2LineupFinder/Persistence/LineupRecord.cs` with a
   `[JsonPropertyName]` attribute and a doc comment.
2. If it needs a contract type change, record it in
   [contract-issues.md](contract-issues.md) instead of editing `contracts/`.
3. Extend `PersistenceTests` with a round trip, and update the schema table in
   [USAGE.md](USAGE.md#saved-file-schema).
4. Keep it additive: a file written by an older build must still load, which is
   why the JSON is case-insensitive and unknown members are tolerated.

## Merge order

`main` receives the branches in dependency order:

```
feat/math-core  ->  feat/physics-sim  ->  feat/plugin-shell
```

No branch merges another branch: each is handed over as a pull request and the
integration owner merges them in that order. Because the plugin resolves Core and
Math at runtime rather than at compile time, the last merge is what turns the stubs
off - check the load line in the server console for `stub=False` after deploying.

## 中文摘要

* 每个目录只有一个负责人，别人的目录只读；跨层需求写进 `docs/contract-issues.md`。
* `contracts/` 在合并窗口之间冻结，插件只通过它引用其它层，不直接引用 `src/`。
* 桩实现必须带 `// TODO: replace with real Core` / `Math` 注释；桩的行为不能当作真实物理的断言依据。
* 多人共用同一工作区：只允许按路径 `git add`，禁止 `git add -A`、`git add .`、`git clean`、`git checkout --`。
* 提交信息使用 conventional commits，作用域写层名。
* 构建与测试按项目路径执行；插件与测试工程开启“警告即错误”。
* 合并顺序：`feat/math-core` → `feat/physics-sim` → `feat/plugin-shell`，由集成负责人执行。
