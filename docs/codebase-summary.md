# Codebase summary

Architecture overview of WaydroidEditor, gathered by reading the source. For build/run/install
commands and the value-classification rules the README covers, see `README.md`; the contributor
guidelines are in `AGENTS.md`. This file is the map of the code: modules, data flow, and the key
classes with their paths.

## What it is

A Linux x64 desktop editor for the Unity `PlayerPrefs` file of Android games running under
Waydroid. `PlayerPrefs` values are opaque text — either a JSON document or a Base64-encoded
**MemoryPack** blob — so the editor loads the **game's own compiled save types** and edits a
structured form (`Gold`, `Items`, `Rarity`) instead of the raw blob.

Three invariants the code protects and any change must preserve:

1. **Byte-identical round-trips** — an unedited value writes back exactly as stored.
2. **Privilege boundary** — the GUI never runs as root; only a headless helper child does.
3. **In-place file rewrite** — never replace the file; preserve inode, owner and mode.

## Layout

Two projects, strictly layered: `WaydroidEditor` (Avalonia GUI + privilege transport) depends on
`WaydroidEditor.Core` (pure domain), which depends only on MemoryPack and the embedded Unity engine
assemblies.

| Path | What |
| --- | --- |
| `src/WaydroidEditor.Core/` | Domain library — XML codec, escaping, MemoryPack bridge, filesystem access, settings. |
| `src/WaydroidEditor/` | Avalonia GUI, MVVM layer, `pkexec` helper client and server, CLI modes. |
| `tests/WaydroidEditor.Tests/` | xUnit tests for Core and the app view model. |
| `tests/FakeGameData/` | A standalone assembly of fake `[MemoryPackable]` types used as "the game". |
| `tests/FakeAttributes/` | An attribute assembly FakeGameData compiles against but is loaded without. |

## Data flow

```
WaydroidAccess.ResolveTarget          → PrefsTarget (host path under <dataRoot>/data/...)
ReadTarget / IPrefsStore.Read         → raw XML bytes (direct, or over the pkexec pipe)
PrefsFile.ParseXml                    → List<PrefsEntry>
  per row, EntryRow classifies: StringSet → Json | MemoryPack (MpRuntime) | Json | Raw
  MemoryPack: MpRuntime.ResolveTypeForKey → Decode → ObjectJson.ToJson   (edit as JSON)
              ObjectJson.ApplyJson → MpRuntime.Encode                    (mutate in place)
PrefsFile.WriteXml                    → bytes
WriteTarget / IPrefsStore.Write       → truncate-in-place + .wpe.bak, then `am force-stop`
```

## Key classes

### Core (`src/WaydroidEditor.Core/`)

| Type | File | Role |
| --- | --- | --- |
| `PrefsFile`, `PrefsEntry`, `PrefsType`, `PrefsFormatException` | `PrefsFile.cs` | The PlayerPrefs XML codec and its entry model. |
| `UnityPrefsEscaping` | `UnityPrefsEscaping.cs` | Detect, decode and re-encode Unity's Android percent-escaping. |
| `MpRuntime` | `MpRuntime.cs` | Owns a collectible `AssemblyLoadContext`, resolves a prefs key to a save type, decodes and encodes MemoryPack bytes. The only `IDisposable` in Core. |
| `MpMemberAttributes` | `MpMemberAttributes.cs` | Reads `[MemoryPackInclude]`/`[MemoryPackIgnore]` from assembly metadata rather than by instantiating the attribute. |
| `ObjectJson` | `ObjectJson.cs` | Converts a decoded CLR object to JSON and applies an edited JSON document back, mutating in place. Throws `JsonBridgeException`. |
| `ValueSchema`, `ValueKind`, `NodeShape` | `ValueSchema.cs` | Classifies a JSON node plus its declared type into the shape the field editor renders, and back. |
| `WaydroidAccess` | `WaydroidAccess.cs` | Resolves the data root and a package's prefs path; reads, writes in place, and force-stops the app. |
| `AppSettings` | `AppSettings.cs` | The editor's own settings file (last data root, package and DLL folders), stored under the user's config dir. |

Core is mostly static: `WaydroidAccess`, `UnityPrefsEscaping`, `ObjectJson` and `ValueSchema` are
static classes; only `MpRuntime`, `PrefsFile` and `AppSettings` are instance types. No DI container,
no logging, no async.

### App (`src/WaydroidEditor/`)

| Type | File | Role |
| --- | --- | --- |
| `Program` | `Program.cs` | Entry point: dispatches helper mode, headless screenshot mode, or the GUI. |
| `StartupOptions` | `StartupOptions.cs` | Parsed command line. |
| `App`, `MainWindow` | `App.axaml.cs`, `MainWindow.axaml.cs` | Avalonia application and window; the window hands itself to the view model and starts it. |
| `PrefsViewModel` | `PrefsViewModel.cs` | The single state holder: package/row/DLL collections, selection and status, and every command. |
| `EntryRow` | `EntryRow.cs` | One prefs key; the grid cell and the detail pane are two views of the same value. |
| `EditMode` | `EntryRow.cs` | The editor a row offers: raw, JSON, MemoryPack, or raw Base64 when MemoryPack is unsupported. |
| `EditorTree`, `EditorNode`, `ContainerNode`, `LeafNode` | `EditorTree.cs`, `EditorNode.cs`, `ContainerNode.cs`, `LeafNode.cs` | The structured field editor: a tree built from the decoded document, each node writing its edits back through the tree's apply callback. |
| `IPrefsStore`, `PrefsStore`, `DirectPrefsStore`, `ElevatedPrefsStore`, `PrefsReadResult` | `PrefsStore.cs` | Chooses direct file access or the long-lived elevated helper. |
| `HelperProtocol` | `HelperProtocol.cs` | Frame format between the GUI and the `pkexec` helper child. |
| `Observable`, `RelayCommand`, `AsyncRelayCommand` | `Commands.cs` | Minimal MVVM primitives, no toolkit dependency. |
| `ErrorDialog` | `ErrorDialog.cs` | A small modal message box (Avalonia 12 core ships no `ContentDialog`). |

## Elevation model

The GUI runs as the user and delegates privileged filesystem work to a headless child of the same
binary, spawned once through `pkexec <self> --helper --data-root <root>`. The child serves
line-framed requests (`LIST`, `READ <pkg>`, `WRITE <pkg> <bytes>`, `QUIT`) over stdio via
`HelperProtocol`. One long-lived child is deliberate: polkit cannot reuse an authorization granted
to a dead process, so a per-operation `pkexec` would prompt per operation. The helper never opens a
display. When the process is already root, `--no-elevate` was passed, or `pkexec` is absent,
`PrefsStore.Create` returns the `DirectPrefsStore` instead.

## Tests

`tests/WaydroidEditor.Tests/` exercises Core and the view model:

- `PrefsFileTests` — parse/write round-trips and malformed-input rejection.
- `UnityPrefsEscapingTests` — escape detection and byte-identical re-encoding.
- `WaydroidAccessTests` — package listing, path resolution, inode-preserving writes.
- `MpRuntimeTests` — loading the fake game assembly, resolving keyed and unkeyed types, and the
  encode/decode/edit round trip.
- `StructuredEditorTests` — building and editing the field-editor tree.
- `PrefsViewModelTests` — row removal, save, and snapshot import behaviour.
