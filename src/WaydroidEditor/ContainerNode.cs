using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using WaydroidEditor.Core;

namespace WaydroidEditor;

/// <summary>
/// An object, array or dictionary: a collapsible node whose children are its members, elements or
/// entries. Arrays and dictionaries add and remove; objects only edit, because their member set
/// comes from the type.
/// </summary>
// ponytail: no virtualization — collapsible-by-default keeps a 10k array at one row; swap the
// ItemsControl for a virtualizing panel if anyone expands one.
public sealed class ContainerNode : EditorNode
{
    readonly NodeShape _shape;
    bool _isExpanded;

    internal ContainerNode(
        EditorTree tree, EditorNode? parent, JsonNode? json, string label, bool readOnly, NodeShape shape)
        : base(tree, parent, json, label, shape.Type, readOnly)
    {
        _shape = shape;
        Kind = shape.Kind;
        CanAdd = !readOnly && shape.Kind switch
        {
            ValueKind.Array => ValueSchema.TryDefaultJson(shape.ElementType, out _),
            ValueKind.Dictionary => ValueSchema.TryDefaultJson(shape.ValueType, out _)
                && ValueSchema.TryNextKey(shape.KeyType ?? typeof(object), CurrentKeys(), out _),
            _ => false,
        };
        if (CanAdd)
            AddCommand = new RelayCommand(Add);
    }

    public ValueKind Kind { get; }

    public ObservableCollection<EditorNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Raise();
        }
    }

    public string Summary => Children.Count == 0
        ? "empty"
        : Kind switch
        {
            ValueKind.Object => $"{Children.Count} fields",
            ValueKind.Array => $"{Children.Count} items",
            _ => $"{Children.Count} entries",
        };

    public bool CanAdd { get; }

    public RelayCommand? AddCommand { get; }

    internal void AddChild(EditorNode child) => Children.Add(child);

    /// <summary>Appends an array element or dictionary entry and wires its remove/key editors.</summary>
    internal void AttachChild(EditorNode child, string? key)
    {
        if (Kind == ValueKind.Dictionary && key is not null
            && ValueSchema.CanEditKey(_shape.KeyType ?? typeof(object)))
        {
            child.KeyEditable = true;
            child.KeyEdited = (node, text) => RenameKey(node, text);
        }

        child.MakeRemovable(() => RemoveChild(child));
        Children.Add(child);
    }

    internal override void WriteChild(EditorNode child, JsonNode? value)
    {
        if (Json is JsonArray array)
        {
            // Position, not value identity: JSON null elements have no instance to compare against,
            // and this container keeps Children and the array in lockstep.
            var index = Children.IndexOf(child);
            if (index < 0 || index >= array.Count)
                throw new JsonBridgeException(Path, "the value is no longer in the list.");
            array[index] = value;
            return;
        }

        if (Json is JsonObject obj)
        {
            var name = NameOf(child)
                ?? throw new JsonBridgeException(Path, "the value is no longer in the object.");
            obj[name] = value;
            return;
        }

        throw new JsonBridgeException(Path, "cannot replace a value here.");
    }

    void Add()
    {
        switch (Kind)
        {
            case ValueKind.Array when Json is JsonArray array:
            {
                if (!ValueSchema.TryDefaultJson(_shape.ElementType, out var value))
                    return;
                Change(() =>
                {
                    array.Add(value);
                    AttachChild(
                        Tree.BuildNode(this, value, _shape.ElementType, $"[{array.Count - 1}]", IsReadOnly), null);
                    Raise(nameof(Summary));
                });
                break;
            }

            case ValueKind.Dictionary when Json is JsonObject obj:
            {
                if (!ValueSchema.TryNextKey(_shape.KeyType ?? typeof(object), CurrentKeys(), out var key))
                    return;
                if (!ValueSchema.TryDefaultJson(_shape.ValueType, out var value))
                    return;
                Change(() =>
                {
                    obj[key] = value;
                    AttachChild(Tree.BuildNode(this, value, _shape.ValueType, key, IsReadOnly), key);
                    Raise(nameof(Summary));
                });
                break;
            }
        }
    }

    void RemoveChild(EditorNode child)
    {
        Change(() =>
        {
            switch (Json)
            {
                case JsonArray array:
                {
                    var index = Children.IndexOf(child);
                    if (index < 0 || index >= array.Count)
                        throw new JsonBridgeException(Path, "the value is no longer in the list.");
                    array.RemoveAt(index);
                    break;
                }
                case JsonObject obj:
                {
                    var name = NameOf(child)
                        ?? throw new JsonBridgeException(Path, "the value is no longer in the object.");
                    obj.Remove(name);
                    break;
                }
                default:
                    throw new JsonBridgeException(Path, "cannot remove a value here.");
            }

            Children.Remove(child);
            Relabel();
            Raise(nameof(Summary));
        });
    }

    void RenameKey(EditorNode child, string text)
    {
        if (Json is not JsonObject obj)
            return;

        Change(() =>
        {
            var key = ValueSchema.NormalizeKey(text, _shape.KeyType ?? typeof(object), Path);
            var current = NameOf(child)
                ?? throw new JsonBridgeException(Path, "the value is no longer in the object.");
            if (current == key)
            {
                child.SetKeyText(key);
                return;
            }
            if (obj.ContainsKey(key))
                throw new JsonBridgeException(Path, $"'{key}' already exists.");

            obj.Remove(current);
            obj[key] = child.Json;
            child.SetKeyText(key);
        });
    }

    /// <summary>
    /// The child's slot: its node instance, or — when the value is JSON null, which has no instance —
    /// its current name, which this container keeps in <see cref="EditorNode.Label"/>.
    /// </summary>
    string? NameOf(EditorNode child)
    {
        if (Json is not JsonObject obj)
            return null;

        if (child.Json is null)
            return obj.ContainsKey(child.Label) ? child.Label : null;

        foreach (var (candidate, node) in obj)
        {
            if (ReferenceEquals(node, child.Json))
                return candidate;
        }
        return null;
    }

    void Relabel()
    {
        if (Kind != ValueKind.Array)
            return;
        for (var i = 0; i < Children.Count; i++)
            Children[i].Label = $"[{i}]";
    }

    List<string> CurrentKeys() =>
        Json is JsonObject obj ? [.. obj.Select(entry => entry.Key)] : [];
}
