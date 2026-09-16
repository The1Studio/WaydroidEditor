using System.Text.Json;
using System.Text.Json.Nodes;
using WaydroidEditor.Core;

namespace WaydroidEditor;

/// <summary>A JSON scalar, enum or null: a text box, checkbox, combo or a read-only "(null)".</summary>
public sealed class LeafNode : EditorNode
{
    readonly IReadOnlyList<string> _choices;
    string _valueText;
    bool? _isChecked;
    string? _selectedChoice;

    internal LeafNode(
        EditorTree tree, EditorNode? parent, JsonNode? json, string label,
        Type? declaredType, bool readOnly, ValueKind kind)
        : base(tree, parent, json, label, declaredType, readOnly)
    {
        ScalarType = declaredType is null ? null : Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (ScalarType is null && json is JsonValue value)
            Inferred = value.GetValueKind();

        Kind = EditableKind(kind, ScalarType);
        _valueText = ValueSchema.ScalarText(json);
        if (Kind == ValueKind.Boolean && json is JsonValue boolean && boolean.TryGetValue<bool>(out var stored))
            _isChecked = stored;

        if (Kind == ValueKind.Enum)
        {
            var choices = new List<string>(Enum.GetNames(ScalarType!));
            // An unrecognized stored value stays selectable, or the stored value would display blank
            // and the first real member would silently replace it.
            if (_valueText.Length > 0 && !choices.Contains(_valueText, StringComparer.Ordinal))
                choices.Add(_valueText);
            _choices = choices;
            _selectedChoice = _valueText.Length > 0 ? _valueText : null;
        }
        else
        {
            _choices = [];
        }

        IsThreeState = Kind == ValueKind.Boolean
            && declaredType is not null && Nullable.GetUnderlyingType(declaredType) is not null;
    }

    /// <summary>
    /// A JSON null still gets an editor when the declared type is something the bridge parses from a
    /// value: that is how a member whose value is null gets its first one. A null collection or
    /// object has no such editor and stays display-only.
    /// </summary>
    static ValueKind EditableKind(ValueKind kind, Type? type)
    {
        if (kind != ValueKind.Null || type is null)
            return kind;
        if (type.IsEnum)
            return ValueKind.Enum;
        if (!ObjectJson.IsScalar(type))
            return kind;
        return type == typeof(bool) ? ValueKind.Boolean : ValueKind.Text;
    }

    public ValueKind Kind { get; }

    public bool IsText => Kind == ValueKind.Text;

    public bool IsBool => Kind == ValueKind.Boolean;

    public bool IsEnum => Kind == ValueKind.Enum;

    public bool IsNull => Kind == ValueKind.Null;

    /// <summary>A boolean node whose declared type is nullable can also be cleared.</summary>
    public bool IsThreeState { get; }

    /// <summary>Enum names, plus the stored value when it is not one.</summary>
    public IReadOnlyList<string> Choices => _choices;

    /// <summary>Two-way text of a scalar field; invalid text stays on screen with an inline error.</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value)
                return;
            _valueText = value;
            Raise();
            if (Kind == ValueKind.Null)
                return; // display-only: nothing to commit
            Change(() => Replace(ScalarType is null
                ? ValueSchema.ParseInferredScalar(value, Inferred!.Value, Path)
                : ValueSchema.ParseScalarText(value, ScalarType, Path)));
        }
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            Raise();
            Change(() => Replace(value is null ? null : JsonValue.Create(value.Value)));
        }
    }

    public string? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (_selectedChoice == value)
                return;
            _selectedChoice = value;
            Raise();
            if (value is null)
                return;
            Change(() => Replace(ValueSchema.ParseScalarText(value, ScalarType!, Path)));
        }
    }

    /// <summary>Declared type with <see cref="Nullable{T}"/> unwrapped; null when only the JSON node is known.</summary>
    internal Type? ScalarType { get; }

    /// <summary>The node's own JSON kind when there is no declared type to parse against.</summary>
    internal JsonValueKind? Inferred { get; }
}
