# Repository Guidelines

## Project Overview

A Linux x64 desktop editor for the Unity `PlayerPrefs` file of Android games running under
[Waydroid](https://waydro.id/). Its whole reason to exist: `PlayerPrefs` values are opaque text
(JSON or a Base64-encoded **MemoryPack** blob), and this tool decodes them using **the game's own
compiled save types** loaded from the game's DLLs — so you edit `Gold`, `Items`, `Rarity` instead
of a blob. No Unity project or exporter is required.

Three concerns the code guards carefully, and any change must preserve:
1. **Byte-identical round-trips** — an unedited value must write back exactly as stored.
2. **Privilege boundary** — the GUI never runs as root; only a headless helper child does.
3. **In-place file rewrite** — never replace the file; preserve inode/owner/mode.

## Architecture & Data Flow

Two projects, strictly layered: `WaydroidPrefsEditor.App` (Avalonia GUI + privilege transport) →
`WaydroidPrefsEditor.Core` (pure domain) → MemoryPack 1.21.4 + the embedded Unity engine assemblies.

```
WaydroidAccess.ResolveTarget            → PrefsTarget (host path under <dataRoot>/data/...)
ReadTarget / IPrefsStore.Read           → raw XML bytes (direct, or over pkexec pipe)
PrefsFile.ParseXml                      → List<PrefsEntry>
  per row, EntryRow classifies: StringSet → Json | MemoryPack (MpRuntime) | Json | Raw
  MemoryPack: MpRuntime.ResolveTypeForKey → Decode → ObjectJson.ToJson  (edit as JSON)
              ObjectJson.ApplyJson → MpRuntime.Encode                  (mutate in place)
PrefsFile.WriteXml                      → bytes
WriteTarget / IPrefsStore.Write         → truncate-in-place + .wpe.bak, then am force-stop
```

Core is stateless/static-heavy: `WaydroidAccess`, `UnityPrefsEscaping`, `ObjectJson` are static
classes; only `MpRuntime`, `PrefsFile`, and `AppSettings` are instance types. `MpRuntime` is the
sole `IDisposable` (owns a collectible `AssemblyLoadContext`). No DI container, no logging, no
async in Core.

### Value classification order (`PrefsViewModel.BuildRow`)

For each key the stored value is percent-decoded first, then tried in order:
**MemoryPack** (key matches a `[Key("…")]`/type-name; only kept if re-encode is byte-identical) →
**JSON** (parses as object/array) → **Raw**. A `set` entry is edited as a JSON string array.

### Elevation model

The GUI runs as the user and delegates privileged FS work to a headless child of the same binary
spawned once through `pkexec <self> --helper --data-root <root>`. The child serves line-framed
requests (`LIST`, `READ <pkg>`, `WRITE <pkg> <bytes>`, `QUIT`) over stdio via `HelperProtocol`.
One long-lived child is deliberate: polkit cannot reuse an authorization from a dead process, so a
per-operation `pkexec` would prompt per operation. The helper must **never open a display**.

## Key Directories

| Path | Purpose |
| --- | --- |
| `src/WaydroidPrefsEditor.Core/` | Domain library: XML codec, escaping, MemoryPack bridge, filesystem access, settings. |
| `src/WaydroidPrefsEditor.App/` | Avalonia GUI, MVVM layer, `pkexec` helper client + server, CLI modes. |
| `tests/WaydroidPrefsEditor.Tests/` | xUnit tests for Core and the App view model. |
| `tests/FakeGameData/` | Standalone `netstandard2.1` assembly of fake `[MemoryPackable]` types used as the "game". |
| `tests/FakeAttributes/` | Attribute assembly FakeGameData compiles against but is loaded without. |
| `desktop/` | Freedesktop `.desktop` launcher. |
| `Managed/` | Source of the bundled Unity engine assemblies; Core embeds `Managed/UnityEngine/UnityEngine*.dll`. |

The Unity engine assemblies under `Managed/UnityEngine/` are embedded as resources by Core
(`LogicalName = WaydroidPrefsEditor.Unity.<name>.dll`), so Unity-native members — and the game's
`[RuntimeInitializeOnLoadMethod]` hooks — resolve without a Unity install and without the game
folder shipping its own `UnityEngine` modules. A folder copy of an engine assembly still wins.

## Development Commands

```bash
dotnet build  WaydroidPrefsEditor.sln -c Release
dotnet test   WaydroidPrefsEditor.sln -c Release

# Self-contained single-file publish → .../net8.0/linux-x64/publish/WaydroidPrefsEditor.App
dotnet publish src/WaydroidPrefsEditor.App -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true
```

Install (per README): copy the published binary to `/opt/waydroid-playerprefs-editor/` and
`desktop/waydroid-playerprefs-editor.desktop` to `/usr/share/applications/`.

**No CI, no scripts, no Makefile, no `.editorconfig`, no `.gitignore`, no `global.json`.** The
README is the sole source of build/run/install commands. Do not assume a CI gate exists — run
build + test locally before claiming done.

Manual / headless verification (no display needed):
```bash
./WaydroidPrefsEditor.App --screenshot out.png          # render and exit
./WaydroidPrefsEditor.App --helper --data-root <root>   # serve the protocol on stdio
```
If `pkexec` is unavailable, run as root with `--no-elevate`.

## Code Conventions & Common Patterns

- **Language/build**: `net8.0`, `Nullable` and `ImplicitUsings` enabled, `LangVersion latest`,
  **`TreatWarningsAsErrors=true`** (`Directory.Build.props`). Warnings fail the build.
- **Naming**: standard .NET (PascalCase types/members, `_camelCase` private fields). One type per
  file, filename matches the type. Nullable annotations are expected on all new code.
- **File-scoped namespaces**, expression-bodied members, `sealed` by default, C# 12 features
  (`primary constructors`, collection expressions `[.. items]`) are used freely.
- **MVVM without a framework**: `App/Commands.cs` defines `Observable` (INotifyPropertyChanged
  with `[CallerMemberName] Raise()`), `RelayCommand`, and single-flight `AsyncRelayCommand`.
  Do **not** add CommunityToolkit.Mvvm or ReactiveUI.
- **Error handling**: no exceptions escape to the binding layer.
  - Core throws typed exceptions (`PrefsFormatException`, `JsonBridgeException`) for malformed
    input; callers catch and collect **warning strings** for recoverable per-row problems.
  - The view model funnels recoverable failures into the `Status` string or a modal
    `ErrorDialog.Show(Host, …)`; a bad row parks its message on `EntryRow.CommitError`/`HasError`.
  - `AppSettings.Load` and `UnityPrefsEscaping.TryDecode` deliberately swallow corrupt/pathological
    input rather than abort a whole file load — preserve that.
- **Async**: only at the UI boundary (file pickers, command handlers). Core and `IPrefsStore` are
  synchronous; store calls run on the UI thread.
- **State**: `PrefsViewModel` is the single state holder (`Packages`/`Rows`/`GameDlls`
  `ObservableCollection`s, selected items, `Status`). `EntryRow` mirrors one key across its grid
  cell and detail pane — editing either re-derives the other via `RefreshDetailFromRaw`/`ApplyDetail`.
- **Classification/decoding is mutate-in-place**: `ObjectJson.ApplyJson` edits the existing decoded
  instance so members it cannot set keep their values and re-encoding stays byte-identical.

## Important Files

- `src/WaydroidPrefsEditor.App/Program.cs` — 4-mode entry point (GUI / `--helper` / `--screenshot`
  / no-display error) and `BuildAvaloniaApp`.
- `src/WaydroidPrefsEditor.App/PrefsViewModel.cs` — all UI state and command logic, package load,
  row classification, save, snapshot import/export.
- `src/WaydroidPrefsEditor.App/PrefsStore.cs` — `IPrefsStore`, `DirectPrefsStore`,
  `ElevatedPrefsStore` (persistent `pkexec` child).
- `src/WaydroidPrefsEditor.App/HelperProtocol.cs` — `OK|ERR <len>\n` framing shared by both ends.
- `src/WaydroidPrefsEditor.App/EntryRow.cs` — the `EditMode` state machine (Raw/Json/MemoryPack/
  MemoryPackUnsupported) and value mirroring.
- `src/WaydroidPrefsEditor.Core/PrefsFile.cs` — `PrefsEntry`, `PrefsType`, `PrefsFile.ParseXml/WriteXml`.
- `src/WaydroidPrefsEditor.Core/MpRuntime.cs` — private `AssemblyLoadContext`, type indexing,
  MemoryPack decode/encode, bundled UnityEngine formatters, and replay of the game's
  `[RuntimeInitializeOnLoadMethod]` hooks so its custom formatters (R3 `ReactiveProperty<T>`, …)
  register outside the Unity player.
- `src/WaydroidPrefsEditor.Core/ObjectJson.cs` — reflection CLR↔`JsonNode` bridge.
- `src/WaydroidPrefsEditor.Core/UnityPrefsEscaping.cs` — round-trip-verified percent codec.
- `src/WaydroidPrefsEditor.Core/WaydroidAccess.cs` — data-root/package discovery, in-place write.
- `Directory.Build.props` — shared build settings (see above).

## Runtime/Tooling Preferences

- **.NET 8 SDK or newer** only; the published binary is self-contained (nothing needed at runtime).
- **No package manager beyond `dotnet`/NuGet.** Versions are pinned inline per `.csproj`, not
  centrally — update them where they are declared.
- Avalonia `12.1.2` (Desktop, Themes.Fluent, Fonts.Inter, Controls.DataGrid, Headless), MemoryPack
  `1.21.4`, xUnit `2.9.2`.
- **MemoryPack is a compatibility contract**: it must match the version the game was built with.
  Unity compiles against `MemoryPack.Core`'s **`netstandard2.1` asset**, whose
  `IMemoryPackable<T>`/`MemoryPackFormatter<T>` shape differs from the `net8.0` asset's — a save type
  built against one fails to load against the other (`TypeLoadException`). Both
  `src/WaydroidPrefsEditor.Core` and `tests/FakeGameData` therefore pin
  `lib/netstandard2.1` by hand (`MemoryPack.Core` with `ExcludeAssets="all"` + `MemoryPack.Generator`);
  keep the two in step, and change the version in both together. The editor's private ALC deliberately
  defers `MemoryPack`/`MemoryPack.Core` to its own copy so both sides share one formatter registry.
- Linux x64 target; `RID=linux-x64`. Avalonia 12 has no Wayland backend — it always connects via
  X11/XWayland, hence the `XAUTHORITY` troubleshooting in the README.
- Settings live at `~/.config/waydroid-playerprefs-editor/settings.json`
  (`AppSettings.FilePath`, camelCase JSON).

## Testing & QA

- **Framework**: xUnit 2.9.2 via `Microsoft.NET.Test.Sdk` 17.11.1 (VSTest). No fixtures/collections,
  no `.runsettings`, no coverage tooling, **no headless UI tests**. Run everything with
  `dotnet test WaydroidPrefsEditor.sln -c Release`.
- **Placement**: Core logic → `tests/WaydroidPrefsEditor.Tests` against Core's public API. App
  internals (e.g. `internal PrefsViewModel.ApplyImported`) are reachable via
  `<InternalsVisibleTo>WaydroidPrefsEditor.Tests</InternalsVisibleTo>` in the App `.csproj`.
- **Naming**: `SomethingTests` class, `[Fact]`/`[Theory]` + `[InlineData]`, method names are
  behavioral sentences (`SaveLeavesUntouchedKeysOnDiskUnchanged`). Tests own a temp dir and
  implement `IDisposable` for cleanup.
- **`FakeGameData`** compiles real `[MemoryPackable]` types (plus a look-alike
  `TheOne.Extensions.KeyAttribute`) to `FakeGameData.dll`; `MpRuntimeTests` loads it through the
  *same* private-ALC path as a real game DLL. It targets `netstandard2.1` and references
  `FakeAttributes` with `PrivateAssets="all"` — a Unity-shaped assembly whose attribute assembly is
  *absent* at runtime, which is how a game's unimported Newtonsoft/Unity modules look. Extend this
  project when adding MemoryPack coverage.
- **`PrefsViewModelTests`** drives the real view model headlessly (`NoElevate = true`) and sandboxes
  `XDG_CONFIG_HOME` so it never rewrites the developer's own settings. Keep new VM tests isolated
  the same way.
- **Caution**: tests calling `AppSettings.Save()` must isolate config; `WaydroidAccessTests` shell
  out to `stat(1)` to observe inode/owner/mode. The `NoWarn CA1416` in the test csproj is
  intentional — Unix-only APIs are exercised on purpose.
- **What to test**: preserve the byte-identical round-trip guarantees (escaped file load→save,
  MemoryPack decode→encode) and the import/remove/save semantics. Assert observable behavior
  (files on disk, row state), not implementation details.
