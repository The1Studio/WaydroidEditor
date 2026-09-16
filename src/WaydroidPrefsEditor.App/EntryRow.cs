using System.Text.Json;
using System.Text.Json.Nodes;
using WaydroidPrefsEditor.Core;

namespace WaydroidPrefsEditor.App;

public enum EditMode
{
    Raw,
    Json,
    MemoryPack,
    MemoryPackUnsupported,
}

/// <summary>
/// One PlayerPrefs key. The grid cell and the detail pane are two views of the same value:
/// editing either re-derives the other. Raw rows are plain text, JSON rows are pretty-printed
/// JSON, MemoryPack rows are JSON over the game's own decoded object, and string sets are a
/// JSON array of strings. A row whose key resolves to a type is edited as labelled fields
/// (<see cref="Editor"/>); one that does not keeps the text pane.
/// </summary>
public sealed class EntryRow : Observable
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    readonly PrefsEntry _entry;
    readonly MpRuntime? _runtime;
    bool _escaped;

    string _rawValue;
    string _detail = "";
    string? _commitError;
    string? _warning;

    public EntryRow(PrefsEntry entry, MpRuntime? runtime)
    {
        _entry = entry;
        _runtime = runtime;

        // Values are held decoded so the grid, the JSON pane and the editors all work on the
        // string the game actually stored; ToEntry re-escapes on the way back out.
        var decoded = entry.Value;
        _escaped = entry.Type == PrefsType.String && UnityPrefsEscaping.TryDecode(entry.Value, out decoded);
        _rawValue = decoded;
        _detail = decoded;
    }

    public string Key => _entry.Key;
    public PrefsType PrefsType => _entry.Type;
    public bool IsSet => PrefsType == PrefsType.StringSet;

    public EditMode Mode { get; private set; } = EditMode.Raw;

    /// <summary>The CLR type the key resolves to, for MemoryPack and JSON rows alike.</summary>
    public Type? SchemaType { get; private set; }

    public object? Decoded { get; private set; }

    /// <summary>The structured editor, when the value has a schema to render fields from.</summary>
    public EditorNode? Editor { get; private set; }

    public bool IsStructuredMode => Editor is not null;
    public bool IsTextMode => Editor is null;

    public string TypeLabel => Mode switch
    {
        EditMode.MemoryPack => "MemoryPack",
        EditMode.MemoryPackUnsupported => "MemoryPack (raw)",
        EditMode.Json when IsSet => "StringSet",
        EditMode.Json => "JSON",
        _ => PrefsType.ToString(),
    };

    public string ModeLabel
    {
        get
        {
            var label = Mode switch
            {
                EditMode.MemoryPack => $"Structured value ({SchemaType!.Name}) — edit fields",
                EditMode.MemoryPackUnsupported => "Structured value unavailable — editing raw Base64",
                EditMode.Json when IsSet => "String set — edit as a JSON array",
                EditMode.Json when SchemaType is not null => $"JSON value ({SchemaType!.Name}) — edit fields",
                EditMode.Json => "JSON document",
                _ => "Raw value",
            };
            return _escaped ? label + "  ·  percent-escaped on disk by Unity" : label;
        }
    }

    /// <summary>The stored payload: scalar text, JSON text, Base64 for MemoryPack, JSON array for sets.</summary>
    public string RawValue
    {
        get => _rawValue;
        set
        {
            if (_rawValue == value)
                return;
            _rawValue = value;
            Raise(nameof(RawValue));
            if (IsSet)
            {
                Run(() =>
                {
                    ParseSetItems(value);
                    SetDetailText(PrettySetItems());
                });
            }
            else
            {
                RefreshDetailFromRaw();
            }
        }
    }

    public string DetailText
    {
        get => _detail;
        set
        {
            if (_detail == value)
                return;
            _detail = value;
            Raise(nameof(DetailText));
            ApplyDetail();
        }
    }

    public string? CommitError
    {
        get => _commitError;
        private set
        {
            if (_commitError == value)
                return;
            _commitError = value;
            Raise(nameof(CommitError));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _commitError is not null;

    public string? Warning
    {
        get => _warning;
        private set
        {
            if (_warning == value)
                return;
            _warning = value;
            Raise(nameof(Warning));
        }
    }

    /// <summary>Re-reads a stored on-disk value, as when a key is reintroduced by Import.</summary>
    internal void ApplyStored(string stored)
    {
        var decoded = stored;
        _escaped = PrefsType == PrefsType.String && UnityPrefsEscaping.TryDecode(stored, out decoded);
        _rawValue = decoded;
        Raise(nameof(RawValue));
        RefreshDetailFromRaw();
    }

    public void SetMode(EditMode mode, Type? schemaType = null)
    {
        Mode = mode;
        SchemaType = schemaType;
        Raise(nameof(TypeLabel));
        Raise(nameof(ModeLabel));
        RefreshDetailFromRaw();
    }

    public void SetSetItems(IReadOnlyList<string> items)
    {
        _entry.SetItems = new List<string>(items);
        RefreshDetailFromRaw();
    }

    public void FormatDetail()
    {
        Run(() =>
        {
            var node = JsonNode.Parse(_detail)
                ?? throw new JsonBridgeException("$", "empty JSON document.");
            DetailText = node.ToJsonString(Indented) + Environment.NewLine;
        });
    }

    public PrefsEntry ToEntry()
    {
        if (!IsSet)
            _entry.Value = UnityPrefsEscaping.Encode(_rawValue, _escaped);
        return _entry;
    }

    /// <summary>The raw cell and the detail pane are two views of one value.</summary>
    void RefreshDetailFromRaw()
    {
        Run(() =>
        {
            // A failed rebuild must leave the row in text mode rather than show a stale tree.
            SetEditor(null);

            switch (Mode)
            {
                case EditMode.Json when IsSet:
                    SetRawValue(CompactSetItems());
                    SetDetailText(PrettySetItems());
                    break;
                case EditMode.Json:
                {
                    var root = Parse(_rawValue);
                    SetDetailText(root.ToJsonString(Indented) + Environment.NewLine);
                    if (SchemaType is not null && root is JsonObject or JsonArray)
                        SetEditor(EditorTree.Build(root, SchemaType, clrBacked: false, Key, CommitEditedNode, ReportEditorError));
                    break;
                }
                case EditMode.MemoryPack:
                {
                    var bytes = Convert.FromBase64String(_rawValue);
                    var decoded = _runtime!.Decode(SchemaType!, bytes)
                        ?? throw new JsonBridgeException("$", "decoded value is null.");
                    Decoded = decoded;
                    var root = ObjectJson.ToJson(decoded, SchemaType!)!;
                    SetDetailText(root.ToJsonString(Indented) + Environment.NewLine);
                    SetEditor(EditorTree.Build(root, SchemaType, clrBacked: true, Key, CommitEditedNode, ReportEditorError));
                    break;
                }
                default:
                    SetDetailText(_rawValue);
                    break;
            }
        });
    }

    void ApplyDetail()
    {
        Run(() =>
        {
            switch (Mode)
            {
                case EditMode.Json when IsSet:
                    ParseSetItems(_detail);
                    SetRawValue(CompactSetItems());
                    break;
                case EditMode.Json:
                {
                    var node = Parse(_detail);
                    if (node is not (JsonObject or JsonArray))
                        throw new JsonBridgeException("$", "expected a JSON object or array.");
                    ApplyNode(node);
                    break;
                }
                case EditMode.MemoryPack:
                    ApplyNode(Parse(_detail));
                    break;
                default:
                    SetRawValue(_detail);
                    break;
            }
        });
    }

    /// <summary>Writes an edited document back through the row's own apply path.</summary>
    void ApplyNode(JsonNode node)
    {
        switch (Mode)
        {
            case EditMode.MemoryPack:
            {
                var warnings = ObjectJson.ApplyJson(Decoded!, SchemaType!, node);
                Warning = warnings.Count == 0 ? null : string.Join("; ", warnings);
                SetRawValue(Convert.ToBase64String(_runtime!.Encode(SchemaType!, Decoded!)));
                break;
            }
            case EditMode.Json when !IsSet:
                SetRawValue(node.ToJsonString());
                break;
        }
    }

    /// <summary>A committed field edit, handed to <see cref="EditorTree"/> as its apply callback.</summary>
    void CommitEditedNode(JsonNode root)
    {
        ApplyNode(root);
        SetDetailText(root.ToJsonString(Indented) + Environment.NewLine);
    }

    /// <summary>Parks the tree's first field error on the row, which is what gates Save.</summary>
    internal void ReportEditorError(string? message) => CommitError = message;

    void SetEditor(EditorNode? editor)
    {
        if (ReferenceEquals(Editor, editor))
            return;
        Editor = editor;
        Raise(nameof(Editor));
        Raise(nameof(IsStructuredMode));
        Raise(nameof(IsTextMode));
    }

    /// <summary>Runs an edit and parks the failure on the row instead of throwing at the binding layer.</summary>
    void Run(Action action)
    {
        try
        {
            action();
            CommitError = null;
        }
        catch (Exception ex)
        {
            CommitError = ex.Message;
        }
    }

    void SetRawValue(string value)
    {
        if (_rawValue == value)
            return;
        _rawValue = value;
        Raise(nameof(RawValue));
    }

    void SetDetailText(string text)
    {
        if (_detail == text)
            return;
        _detail = text;
        Raise(nameof(DetailText));
    }

    static JsonNode Parse(string text) =>
        JsonNode.Parse(text) ?? throw new JsonBridgeException("$", "empty JSON document.");

    void ParseSetItems(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
            throw new JsonBridgeException("$", "expected a JSON array of strings.");
        _entry.SetItems = array
            .Select(item => item?.GetValue<string>()
                ?? throw new JsonBridgeException("$", "set entries must be strings."))
            .ToList();
    }

    string CompactSetItems() => ToArray(_entry.SetItems).ToJsonString();
    string PrettySetItems() => ToArray(_entry.SetItems).ToJsonString(Indented) + Environment.NewLine;

    static JsonArray ToArray(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(item);
        return array;
    }
}
