using System.Text.Json.Nodes;
using WaydroidEditor.Core;

namespace WaydroidEditor;

/// <summary>
/// One value in the structured editor tree: the chrome both templates bind, plus the commit path
/// shared by every node. A user edit mutates this node's JSON, then re-applies the whole document
/// through <see cref="EditorTree"/> so the row's raw value (and its grid cell) tracks the field.
/// </summary>
public abstract class EditorNode : Observable
{
    string _label;
    string _keyText;
    string? _error;

    internal EditorNode(
        EditorTree tree, EditorNode? parent, JsonNode? json, string label, Type? declaredType, bool readOnly)
    {
        Tree = tree;
        Parent = parent;
        Json = json;
        _label = label;
        _keyText = label;
        TypeLabel = ValueSchema.DisplayName(declaredType);
        IsReadOnly = readOnly;
        tree.Register(this);
    }

    public EditorTree Tree { get; }
    public EditorNode? Parent { get; }

    /// <summary>This node's value in the edited document — the instance its container locates it by.</summary>
    public JsonNode? Json { get; private set; }

    /// <summary>Member name, <c>[0]</c> index, dictionary key, or the prefs key for the root.</summary>
    public string Label
    {
        get => _label;
        internal set
        {
            if (_label == value)
                return;
            _label = value;
            Raise();
        }
    }

    public string TypeLabel { get; }

    /// <summary>The document path used in errors: <c>Key.Items[0].Label</c>.</summary>
    public string Path => Parent is null ? Label : Parent.Path + (Label.StartsWith('[') ? Label : "." + Label);

    /// <summary>Inline validation/commit error, shown next to the field.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (_error == value)
                return;
            _error = value;
            Raise();
        }
    }

    /// <summary>A CLR-backed read-only member, or inherited from the parent.</summary>
    public bool IsReadOnly { get; }

    public bool IsEditable => !IsReadOnly;

    /// <summary>Dictionary entries whose key can be typed; everything else shows its label instead.</summary>
    public bool KeyEditable { get; internal set; }

    public bool ShowLabel => !KeyEditable;

    /// <summary>Editable dictionary key text; an edit invokes <see cref="KeyEdited"/>.</summary>
    public string KeyText
    {
        get => _keyText;
        set
        {
            if (_keyText == value)
                return;
            _keyText = value;
            Raise();
            if (KeyEditable)
                KeyEdited?.Invoke(this, value);
        }
    }

    public bool CanRemove { get; private set; }

    public RelayCommand? RemoveCommand { get; private set; }

    internal Action<EditorNode, string>? KeyEdited;

    /// <summary>
    /// Silent key update after normalization. The label follows, so a renamed dictionary entry
    /// keeps a name to be found by on the next edit.
    /// </summary>
    internal void SetKeyText(string text)
    {
        if (_keyText != text)
        {
            _keyText = text;
            Raise(nameof(KeyText));
        }
        Label = text;
    }

    internal void MakeRemovable(Action remove)
    {
        CanRemove = true;
        RemoveCommand = new RelayCommand(remove);
        Raise(nameof(CanRemove));
        Raise(nameof(RemoveCommand));
    }

    /// <summary>
    /// Applies a user edit: mutate the JSON, then re-apply the whole document, parking any failure
    /// on this node and reporting the tree's first error on the row.
    /// </summary>
    // ponytail: re-encodes the whole object per keystroke; fine for save-sized values.
    protected void Change(Action mutate)
    {
        try
        {
            mutate();
            Tree.Apply();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        Tree.ReportErrors();
    }

    /// <summary>Writes this node's value into its slot in the parent container.</summary>
    protected void Replace(JsonNode? value)
    {
        if (Parent is null)
            throw new InvalidOperationException("the root value cannot be replaced.");
        Parent.WriteChild(this, value);
        Json = value;
    }

    /// <summary>Container nodes override this to write into their JSON; leaves have no slot to fill.</summary>
    internal virtual void WriteChild(EditorNode child, JsonNode? value) =>
        throw new InvalidOperationException($"{Path}: cannot replace a value here.");
}
