# Waydroid PlayerPrefs Editor

A desktop editor for the Unity `PlayerPrefs` file of Android games running under
[Waydroid](https://waydro.id/).

`PlayerPrefs` values are stored as raw text. Many games keep their real save state in a value
string that is either a JSON document or a **MemoryPack**-serialized byte array (stored Base64).
This editor decodes those using the **game's own compiled save types**, so you edit a structured
form — `Gold`, `Items`, `Rarity` — instead of an opaque blob. Types are imported from the game's
assemblies at runtime; there is no Unity project to open and no exporter to install.

## Features

- Dropdown of every Waydroid package that has a Unity PlayerPrefs file.
- Unity's engine assemblies ship inside the app; the **DLLs** tab adds and removes the game's
  compiled assemblies.
- Export / import of a full data snapshot as JSON.
- Remove the selected row, or every row at once. Nothing is written until **Save**; **Refresh**
  re-reads the file and drops the in-memory removals. Import recreates keys that were removed.
- Per-key editing: raw text, pretty-printed JSON, or a structured object decoded through the
  game's MemoryPack formatters. When the key resolves to a type the value is edited as labelled
  fields instead — checkbox for `bool`, dropdown for enums, text for numbers/strings/dates, and
  collapsible arrays and dictionaries with **Add**/**Remove**.
- Writes back in place, preserving the file's inode, owner, mode and SELinux label, with a
  `.wpe.bak` backup, and force-stops the app so it cannot flush stale prefs over the edit.
- Elevates only the file access it needs, never the GUI itself, so it works under X11, Wayland
  and tiling compositors alike.

## Requirements

- Linux x64, Waydroid installed and initialised.
- `pkexec` and a polkit authentication agent to write prefs files; or run the binary as root
  with `--no-elevate`.
- .NET 8 SDK or newer to build. Nothing is needed at runtime — the published binary is
  self-contained.

## Build

```bash
dotnet build WaydroidPrefsEditor.sln -c Release
dotnet test  WaydroidPrefsEditor.sln -c Release

dotnet publish src/WaydroidPrefsEditor.App -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true
```

The result is a single executable at
`src/WaydroidPrefsEditor.App/bin/Release/net8.0/linux-x64/publish/WaydroidPrefsEditor.App`.
Double-click it, or install it and the launcher:

```bash
sudo install -Dm755 src/WaydroidPrefsEditor.App/bin/Release/net8.0/linux-x64/publish/WaydroidPrefsEditor.App \
    /opt/waydroid-playerprefs-editor/WaydroidPrefsEditor.App
sudo install -Dm644 desktop/waydroid-playerprefs-editor.desktop \
    /usr/share/applications/waydroid-playerprefs-editor.desktop
```

## Where the prefs files live

Waydroid bind-mounts its data directory onto the container's `/data`. On the host that is:

```
<dataRoot>/data/<package>/shared_prefs/<package>.v2.playerprefs.xml
```

`<dataRoot>` is resolved in this order: `--data-root <path>`, then `WAYDROID_DATA_ROOT`, then
`$XDG_DATA_HOME/waydroid/data` (or `~/.local/share/waydroid/data`), then `/var/lib/waydroid/data`.

## How elevation works

Those files belong to the Android app's real uid, so an unprivileged user cannot read or write
them — and the game's package directories are usually too locked down even to list.

The GUI therefore runs **as you**, and delegates all privileged filesystem work to a headless
child of the same binary invoked once through `pkexec`:

```
pkexec /path/to/WaydroidPrefsEditor.App --helper serve --data-root <root>
```

That child stays alive for the run and serves line-based requests over its pipes:

| Request | Reply |
| --- | --- |
| `LIST` | every package with a PlayerPrefs file, newline separated |
| `READ <pkg>` | `<absolute path>\n` followed by the raw prefs XML |
| `WRITE <pkg> <byteCount>` | the state, after `byteCount` payload bytes on stdin |
| `QUIT` | ends the session |

Replies are framed as `OK <length>\n` or `ERR <length>\n` plus exactly that many bytes.

**Why one long-lived child** — polkit cannot reuse an authorization granted to a process that has
already exited, so a fresh `pkexec` per operation means a fresh prompt per operation. With the
persistent helper you authenticate **once per app run**: switching packages, refreshing and
saving cost nothing further. The child exits on `QUIT`, or by itself when the GUI closes its
end of the pipe.

Running only the helper as root is deliberate. An earlier design ran the whole GUI elevated,
which forces a root process to authenticate against *your* display; that breaks under Hyprland
and anything else where the XWayland cookie lives somewhere a root process will not look
(`Authorization required, but no authorization protocol specified`, then `XOpenDisplay failed`).
The helper never opens a display, so the problem cannot arise.


## Supplying the game's types

Open the **DLLs** tab and press **Add folder…**, then choose the folder holding the game's *compiled* save types; the folder is listed, and its `*.dll` files (any depth) are loaded from there.
Either source works:

| Source | Folder |
| --- | --- |
| Unity project on this machine | `<project>/Library/ScriptAssemblies` |
| Shipped Mono build | the Android build's `assets/bin/Data/Managed` |

Where the same assembly name appears more than once, the shallowest copy is used and the rest are
counted in the status line. Select an assembly in the list and press **Remove** to drop it. The
list is remembered in `~/.config/waydroid-playerprefs-editor/settings.json` as `gameDlls`.

The folder also has to carry what the save types depend on beyond the engine — `Newtonsoft.Json`,
`R3`, and every other third-party or game-internal assembly the save types serialise through. A
project's `ScriptAssemblies` folder can still be enough when the game's own code is all it needs. A
save type whose dependencies are missing cannot load, and its row stays in raw mode; a row that
loads but fails to decode says which assembly it wanted in the status line.

Custom MemoryPack formatters are covered as well. A game registers a formatter for a type it does
not own — R3's `ReactiveProperty<T>`, say — from a `[RuntimeInitializeOnLoadMethod]` hook, which
never fires outside the player. The editor replays those hooks when it loads the folder, so such
save types decode instead of failing with *… is not registered in this provider*.

The app bundles Unity's engine assemblies — the whole `UnityEngine.*Module` set — so Unity-native
members (`Vector3`, `Color`, `AnimationCurve`, …) resolve with no Unity install and no
`UnityEngine` files in the game folder. A game folder that ships its own engine copies still wins.

**IL2CPP builds ship no managed assemblies.** There is nothing to import, so every value stays
in raw editing mode — JSON where the game wrote JSON, Base64 otherwise.

### Version matching

The editor must be built against **the MemoryPack the game was compiled against** — currently
`MemoryPack.Core 1.21.4`, matching `com.cysharp.memorypack`. Unity compiles against that package's
`netstandard2.1` asset, whose `IMemoryPackable<T>`/`MemoryPackFormatter<T>` shape differs from the
`net8.0` asset's, so `src/WaydroidPrefsEditor.Core` and `tests/FakeGameData` both pin
`lib/netstandard2.1` by hand (`MemoryPack.Core` with `ExcludeAssets="all"`, plus
`MemoryPack.Generator`). Publishing against the `MemoryPack` metapackage instead makes every save
type fail to load with `TypeLoadException`.

The editor loads the game's assemblies into a private context that deliberately defers `MemoryPack`
and `MemoryPack.Core` to its own copy, so both sides share one formatter registry. To match a
different game, change the `MemoryPack.Core` version in those two projects together.

## Unity percent-escapes string values on Android

Unity's Android loader reads every stored key and value through `Uri.UnescapeDataString`, so what
it writes to the XML is escaped:

```xml
<string name="Graphics">%7B%22quality%22%3A3%7D</string>   <!-- {"quality":3} -->
```

Left alone that hides most of the payload, because an escaped JSON document is not parseable JSON
and an escaped Base64 blob is not valid Base64 (`+`, `/` and `=` become `%2B`, `%2F`, `%3D`). The
editor therefore **decodes values on load and re-encodes them on save**, so you edit
`{"quality":3}` and `Player One/Two` rather than their escapes. The detail pane adds
*percent-escaped on disk by Unity* to say which rows were affected.

Decoding is applied only when re-encoding reproduces the stored text exactly. A value Unity left
alone (`50% off`, `100%`, a bare Base64 blob) is never touched, and a value you open without
editing is written back byte-for-byte — the same guarantee the MemoryPack path gives, verified by
a round-trip test over a whole escaped file.

Keys are left as stored. Escaping them is harmless here because the editor never rewrites a key.

## How a value is classified

For each key, the stored value is first percent-decoded, then, in order:

1. **MemoryPack** — the key names a `[MemoryPackable]` type (via `[Key("...")]`, else the type
   name) in the imported assemblies. The value is Base64-decoded, deserialized with the game's
   own formatter, and re-encoded. If the re-encoded bytes do not match the original exactly the
   row is downgraded to raw editing — that guard is what keeps an unedited save byte-identical.
2. **JSON** — the decoded value parses as a JSON object or array.
3. **Raw** — anything else, including `set` (string set) entries, which are edited as a JSON
   array.

A row whose key resolves to a type — whether the value is a MemoryPack blob or JSON — is edited as
**fields**: one labelled widget per member, with nested objects, arrays and dictionaries as
expanders that add and remove entries. Members the CLR object cannot write are shown disabled, and
an invalid value stays on screen with an inline error that blocks **Save** until it is fixed. Only
rows with no schema at all keep the text pane, and every structured edit goes through the same
re-encode path, so an unedited value still writes back byte-identically.

A Base64 blob whose key has **no** matching `[MemoryPackable]` type stays in raw mode: without the
game's formatters the bytes are just bytes, and guessing at them would be worse than showing them.

A MemoryPack row that cannot be decoded (Unity-native members such as `AnimationCurve` and
`Gradient` need the Unity runtime) shows as `MemoryPack (raw)` and falls back to Base64.

## Snapshot format

Export and import use the same JSON document:

```json
{
  "kind": "waydroid-playerprefs",
  "version": 1,
  "package": "com.example.game",
  "entries": [
    {"key": "PlayerData", "type": "string", "value": "AQIDBA=="},
    {"key": "RemoveAds", "type": "boolean", "value": "true"},
    {"key": "Levels", "type": "set", "value": ["a", "b"]}
  ]
}
```

Import also accepts a raw `<map>…</map>` PlayerPrefs XML file. Keys are matched by name: one that
still exists is updated in place, and one that is missing — because its row was removed — is
created again, so an exported snapshot restores a wiped package. Import never deletes a key and
never duplicates one.

`value` holds the text as it sits on disk, so a percent-escaped Unity string is exported and
imported verbatim.

## Command line

| Flag | Meaning |
| --- | --- |
| `--data-root <path>` | Override the Waydroid data root. |
| `--package <name>` | Preselect a package. |
| `--no-elevate` | Touch the prefs files directly instead of going through `pkexec`. Use when already root. |
| `--helper` | Internal: serve privileged file requests on stdin/stdout instead of showing a GUI. |
| `--screenshot <png>` | Headless render to a PNG and exit (used for verification). |

## Troubleshooting

**`No display available`** — printed when both `$DISPLAY` and `$WAYLAND_DISPLAY` are empty.
Exit code `2`. Run it from a desktop session, not a bare TTY or an SSH shell without forwarding.
To check the wiring on a headless box, use `--screenshot out.png`.

**`XOpenDisplay failed`** / **`Authorization required, but no authorization protocol specified`** —
the GUI shell could not reach a display, or reached one without a cookie. The GUI is never
elevated, so this is a plain environment problem, not an elevation one:

```bash
echo "$DISPLAY $WAYLAND_DISPLAY $XDG_SESSION_TYPE"
xauth list                                   # is there a cookie for your $DISPLAY?
```

Avalonia 12 ships no Wayland backend, so it always connects through X11 — on a Wayland
compositor that means XWayland, which needs a valid `XAUTHORITY`. If your other X11 apps work,
this should too; if they do not, point the app at the right file explicitly:

```bash
XAUTHORITY=<the file xauth list read> ./WaydroidPrefsEditor.App
```

**`pkexec could not start the helper` / `Authorization was dismissed or denied`** — `pkexec`
returned 127 or 126. Install and start a polkit authentication agent (on Hyprland, e.g.
`hyprpolkitagent`), then retry; or run the whole app as root with `--no-elevate`.

**`Error creating textual authentication agent`** — `pkexec` found no polkit agent, which is
normal in a TTY. Run it from a desktop session, or start it as root directly.

**Nothing happens on double-click** — the error text goes to stderr, which a `.desktop`
launcher discards. Run it from a terminal once to see it.

## Safety

- Every write first copies the file to `<name>.wpe.bak`.
- The existing file is truncated in place, never replaced, so owner/mode/inode survive.
- The app is force-stopped after a successful write (`waydroid shell -- am force-stop <pkg>`);
  a failure there is reported but not fatal.
