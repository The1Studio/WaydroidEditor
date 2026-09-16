using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WaydroidEditor.Core;

namespace WaydroidEditor;

public sealed class PrefsViewModel : Observable
{
    const string SnapshotKind = "waydroid-playerprefs";
    static readonly JsonSerializerOptions SnapshotOptions = new() { WriteIndented = true };

    readonly StartupOptions _options;
    readonly IPrefsStore _store;
    readonly AppSettings _settings;
    readonly string _dataRoot;

    bool _started;
    MpRuntime? _runtime;
    string? _filePath;
    string? _package;
    string _status = "";

    public PrefsViewModel(StartupOptions options)
    {
        _options = options;
        _settings = AppSettings.Load();
        _dataRoot = options.DataRoot
            ?? (string.IsNullOrEmpty(_settings.DataRoot) ? WaydroidAccess.ResolveDataRoot([]) : _settings.DataRoot);
        _store = PrefsStore.Create(options, _dataRoot);

        RefreshCommand = new RelayCommand(() => RefreshPackages());
        SaveCommand = new RelayCommand(Save);
        FormatJsonCommand = new RelayCommand(() => SelectedRow?.FormatDetail());
        ExportCommand = new AsyncRelayCommand(ExportAsync, Report);
        ImportCommand = new AsyncRelayCommand(ImportAsync, Report);
        AddDllsCommand = new AsyncRelayCommand(AddDllsAsync, Report);
        RemoveDllCommand = new RelayCommand(RemoveDll);
        RemoveRowCommand = new RelayCommand(RemoveCurrentRow);
        RemoveAllRowsCommand = new RelayCommand(RemoveAllRows);

        Rows.CollectionChanged += (_, _) => Raise(nameof(HasRows));
    }

    /// <summary>Set by the window so pickers and dialogs have a parent.</summary>
    public Window? Host { get; set; }

    public ObservableCollection<string> Packages { get; } = new();
    public ObservableCollection<EntryRow> Rows { get; } = new();
    public ObservableCollection<string> GameDlls { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand FormatJsonCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand AddDllsCommand { get; }
    public RelayCommand RemoveDllCommand { get; }
    public RelayCommand RemoveRowCommand { get; }
    public RelayCommand RemoveAllRowsCommand { get; }

    public bool HasRows => Rows.Count > 0;
    public bool HasSelectedRow => _selectedRow is not null;

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            Raise();
        }
    }

    string? _selectedPackage;
    public string? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (_selectedPackage == value)
                return;
            _selectedPackage = value;
            Raise();
            LoadPackage();
        }
    }

    EntryRow? _selectedRow;
    public EntryRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value))
                return;
            _selectedRow = value;
            Raise();
            Raise(nameof(HasSelectedRow));
        }
    }

    string? _selectedDll;
    public string? SelectedDll
    {
        get => _selectedDll;
        set
        {
            if (_selectedDll == value)
                return;
            _selectedDll = value;
            Raise();
            Raise(nameof(HasSelectedDll));
        }
    }

    public bool HasSelectedDll => _selectedDll is not null;

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        foreach (var dll in _settings.GameDlls)
            GameDlls.Add(dll);
        if (GameDlls.Count > 0)
            LoadRuntime([.. GameDlls]);

        if (!RefreshPackages())
            return;

        var wanted = _options.Package ?? _settings.LastPackage;
        if (wanted is not null && Packages.Contains(wanted))
            SelectedPackage = wanted;
        else if (Packages.Count > 0)
            SelectedPackage = Packages[0];
        else
            Status = $"No Waydroid app with a Unity PlayerPrefs file under {_dataRoot}";
    }

    /// <summary>Returns false when the listing failed and the status bar already explains why.</summary>
    bool RefreshPackages()
    {
        var previous = SelectedPackage;

        Packages.Clear();
        try
        {
            foreach (var package in _store.ListPackages())
                Packages.Add(package);
        }
        catch (Exception ex)
        {
            // A dismissed pkexec prompt or a missing polkit agent lands here; it must not abort.
            Rows.Clear();
            SelectedPackage = null;
            Status = "Could not list packages: " + ex.Message;
            return false;
        }

        SelectedPackage = null;
        if (previous is not null && Packages.Contains(previous))
        {
            SelectedPackage = previous;
        }
        else if (Packages.Count > 0)
        {
            SelectedPackage = Packages[0];
        }
        else
        {
            Rows.Clear();
            Status = $"No Waydroid app with a Unity PlayerPrefs file under {_dataRoot}";
        }
        return true;
    }

    void LoadPackage()
    {
        Rows.Clear();
        SelectedRow = null;

        _filePath = null;
        _package = SelectedPackage;

        if (SelectedPackage is null)
            return;

        try
        {
            var read = _store.Read(SelectedPackage);
            _filePath = read.FilePath;

            var parsed = PrefsFile.ParseXml(new MemoryStream(read.Bytes));
            var notes = new List<string>();
            foreach (var entry in parsed.Entries)
                Rows.Add(BuildRow(entry, notes));

            _settings.LastPackage = SelectedPackage;
            _settings.Save();

            Status = $"{Rows.Count} {Plural(Rows.Count, "entry", "entries")} from {Path.GetFileName(_filePath)}"
                + (notes.Count == 0 ? "" : $" — {notes.Count} raw-only: {notes[0]}");

            SelectedRow = Rows.Count > 0 ? Rows[0] : null;
        }
        catch (Exception ex)
        {
            Status = "Load failed: " + ex.Message;
        }
    }

    EntryRow BuildRow(PrefsEntry entry, List<string> notes)
    {
        var row = new EntryRow(entry, _runtime);

        if (entry.Type == PrefsType.StringSet)
        {
            row.SetMode(EditMode.Json);
            return row;
        }

        if (entry.Type != PrefsType.String)
        {
            row.SetMode(EditMode.Raw);
            return row;
        }

        // The key resolves to the game's own type whether it is stored as a MemoryPack blob or as
        // JSON keyed by the type's name; either way the row gets the schema-backed field editor.
        var type = _runtime?.ResolveTypeForKey(entry.Key);
        var memoryPackable = type is not null && MpRuntime.IsMemoryPackable(type);
        if (memoryPackable)
        {
            try
            {
                var bytes = Convert.FromBase64String(row.RawValue);
                var decoded = _runtime!.Decode(type!, bytes);
                if (decoded is not null && _runtime.Encode(type!, decoded).AsSpan().SequenceEqual(bytes))
                {
                    row.SetMode(EditMode.MemoryPack, type!);
                    return row;
                }
                notes.Add($"{entry.Key}: not round-trippable");
            }
            catch (Exception ex)
            {
                notes.Add($"{entry.Key}: {ex.Message}");
            }
        }

        try
        {
            if (JsonNode.Parse(row.RawValue) is JsonObject or JsonArray)
            {
                row.SetMode(EditMode.Json, type);
                return row;
            }
        }
        catch (JsonException)
        {
            // Not JSON — falls through to raw editing.
        }

        if (memoryPackable)
        {
            row.SetMode(EditMode.MemoryPackUnsupported);
            return row;
        }

        row.SetMode(EditMode.Raw);
        return row;
    }

    void Save()
    {
        if (_package is null)
        {
            Status = "Nothing loaded.";
            return;
        }

        var broken = Rows.FirstOrDefault(r => r.HasError);
        if (broken is not null)
        {
            Status = $"{broken.Key}: {broken.CommitError}";
            ErrorDialog.Show(Host, "Invalid value", $"{broken.Key}\n\n{broken.CommitError}");
            return;
        }

        try
        {
            var file = new PrefsFile();
            foreach (var row in Rows)
                file.Entries.Add(row.ToEntry());

            using var buffer = new MemoryStream();
            PrefsFile.WriteXml(file, buffer);
            var message = _store.Write(_package, buffer.ToArray());
            Status = $"Saved {Rows.Count} {Plural(Rows.Count, "entry", "entries")} to {Path.GetFileName(_filePath)}. {message}";
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
            ErrorDialog.Show(Host, "Save failed", ex.Message);
        }
    }

    void RemoveCurrentRow()
    {
        if (_selectedRow is not { } row)
            return;

        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        Rows.RemoveAt(index);
        SelectedRow = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];

        Status = $"Removed {row.Key}. {Rows.Count} {Plural(Rows.Count, "entry", "entries")} left"
            + " — Save to write, Refresh to discard.";
    }

    void RemoveAllRows()
    {
        var removed = Rows.Count;
        if (removed == 0)
            return;

        Rows.Clear();
        SelectedRow = null;

        Status = $"Removed all {removed} {Plural(removed, "entry", "entries")} — Save to write, "
            + "Refresh to discard.";
    }

    async Task ExportAsync()
    {
        if (_package is null || Host?.StorageProvider is not { } storage)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PlayerPrefs",
            SuggestedFileName = SelectedPackage + ".playerprefs.json",
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType, AllFileType],
        });
        if (file is null)
            return;

        var bytes = Encoding.UTF8.GetBytes(BuildSnapshot().ToJsonString(SnapshotOptions));
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(bytes);
        Status = "Exported to " + file.Name;
    }

    async Task ImportAsync()
    {
        if (Host?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PlayerPrefs",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType, XmlFileType, AllFileType],
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        string text;
        await using (var stream = await file.OpenReadAsync())
        using (var reader = new StreamReader(stream))
            text = await reader.ReadToEndAsync();

        var imported = TryParseSnapshot(text) ?? TryParsePrefsXml(text);
        if (imported is null)
        {
            ErrorDialog.Show(Host, "Import failed", $"Unrecognized import file: {file.Name}");
            return;
        }

        var applied = ApplyImported(imported);
        Status = $"Imported {applied} of {imported.Count} entries from {file.Name}.";
    }

    async Task AddDllsAsync()
    {
        if (Host?.StorageProvider is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add the folder with the game's compiled DLLs",
            AllowMultiple = true,
        });

        var added = 0;
        foreach (var folder in folders)
        {
            if (folder.TryGetLocalPath() is not { } root || GameDlls.Contains(root))
                continue;
            GameDlls.Add(root);
            added++;
        }
        if (added == 0)
            return;

        _settings.GameDlls = [.. GameDlls];
        _settings.Save();
        ReloadRuntime();
    }

    void RemoveDll()
    {
        if (SelectedDll is not { } dll)
            return;

        GameDlls.Remove(dll);
        _settings.GameDlls = [.. GameDlls];
        _settings.Save();
        ReloadRuntime();
    }

    internal void ReloadRuntime()
    {
        _runtime?.Dispose();
        _runtime = null;
        if (GameDlls.Count > 0)
            LoadRuntime([.. GameDlls]);
        LoadPackage();
    }

    internal void LoadRuntime(IReadOnlyList<string> gameDlls)
    {
        try
        {
            _runtime?.Dispose();
            _runtime = MpRuntime.Load(gameDlls);

            var warnings = _runtime.LoadWarnings;
            var count = _runtime.LoadedAssemblies;
            Status = warnings.Count == 0
                ? $"Loaded {count} {Plural(count, "DLL", "DLLs")}."
                : $"Loaded {count} {Plural(count, "DLL", "DLLs")}"
                  + $" with {warnings.Count} warning(s): {warnings[0]}";
        }
        catch (Exception ex)
        {
            _runtime = null;
            Status = "DLL load failed: " + ex.Message;
            ErrorDialog.Show(Host, "DLL load failed", ex.Message);
        }
    }

    JsonObject BuildSnapshot()
    {
        var entries = new JsonArray();
        foreach (var row in Rows)
        {
            var entry = new JsonObject
            {
                ["key"] = row.Key,
                ["type"] = TypeName(row.PrefsType),
            };
            if (row.PrefsType == PrefsType.StringSet)
            {
                var items = new JsonArray();
                foreach (var item in row.ToEntry().SetItems)
                    items.Add(item);
                entry["value"] = items;
            }
            else
            {
                entry["value"] = row.ToEntry().Value;
            }
            entries.Add(entry);
        }

        return new JsonObject
        {
            ["kind"] = SnapshotKind,
            ["version"] = 1,
            ["package"] = SelectedPackage,
            ["entries"] = entries,
        };
    }

    static List<PrefsEntry>? TryParseSnapshot(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject obj || obj["entries"] is not JsonArray entries)
            return null;
        if (obj["kind"] is JsonValue kind && kind.TryGetValue<string>(out var value) && value != SnapshotKind)
            return null;

        var result = new List<PrefsEntry>();
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || entry["key"]?.GetValue<string>() is not { } key)
                continue;
            var type = ParseType(entry["type"]?.GetValue<string>());
            var parsed = new PrefsEntry { Key = key, Type = type };
            if (type == PrefsType.StringSet && entry["value"] is JsonArray items)
                parsed.SetItems = items.Select(i => i?.GetValue<string>() ?? "").ToList();
            else
                parsed.Value = entry["value"]?.GetValue<string>() ?? "";
            result.Add(parsed);
        }
        return result;
    }

    static List<PrefsEntry>? TryParsePrefsXml(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("<?xml", StringComparison.Ordinal)
            && !trimmed.StartsWith("<map", StringComparison.Ordinal))
            return null;
        try
        {
            return PrefsFile.ParseXml(new MemoryStream(Encoding.UTF8.GetBytes(text))).Entries;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Overwrites keys that still exist and recreates the ones removed, so an exported snapshot
    /// restores a wiped package. A key already present is never duplicated.
    /// </summary>
    internal int ApplyImported(List<PrefsEntry> imported)
    {
        var byKey = new Dictionary<string, PrefsEntry>(StringComparer.Ordinal);
        foreach (var entry in imported)
            byKey.TryAdd(entry.Key, entry);

        var applied = 0;
        foreach (var row in Rows)
        {
            if (!byKey.TryGetValue(row.Key, out var entry) || entry.Type != row.PrefsType)
                continue;
            if (entry.Type == PrefsType.StringSet)
                row.SetSetItems(entry.SetItems);
            else
                row.ApplyStored(entry.Value);
            applied++;
        }

        var notes = new List<string>();
        foreach (var entry in imported)
        {
            if (Rows.Any(r => r.Key == entry.Key))
                continue;
            Rows.Add(BuildRow(entry, notes));
            SelectedRow ??= Rows[^1];
            applied++;
        }

        return applied;
    }

    void Report(Exception ex) => Status = ex.Message;

    static string Plural(int count, string one, string many) => count == 1 ? one : many;

    static string TypeName(PrefsType type) => type switch
    {
        PrefsType.Int => "int",
        PrefsType.Long => "long",
        PrefsType.Float => "float",
        PrefsType.Boolean => "boolean",
        PrefsType.StringSet => "set",
        _ => "string",
    };

    static PrefsType ParseType(string? name) => name switch
    {
        "int" => PrefsType.Int,
        "long" => PrefsType.Long,
        "float" => PrefsType.Float,
        "boolean" => PrefsType.Boolean,
        "set" => PrefsType.StringSet,
        _ => PrefsType.String,
    };

    static readonly FilePickerFileType JsonFileType =
        new("JSON") { Patterns = ["*.json"] };
    static readonly FilePickerFileType XmlFileType =
        new("PlayerPrefs XML") { Patterns = ["*.xml"] };
    static readonly FilePickerFileType AllFileType =
        new("All files") { Patterns = ["*"] };
}
