using System.Text.Json.Nodes;
using WaydroidEditor.Core;

namespace WaydroidEditor;

/// <summary>
/// Builds an editor tree from a decoded JSON document plus the CLR type the schema knows it by,
/// and funnels every committed field edit back through the row's apply callback.
/// </summary>
public sealed class EditorTree
{
    readonly Action<JsonNode> _apply;
    readonly Action<string?> _report;

    EditorTree(JsonNode root, bool clrBacked, Action<JsonNode> apply, Action<string?> report)
    {
        ApplyTarget = root;
        ClrBacked = clrBacked;
        _apply = apply;
        _report = report;
    }

    /// <summary>Builds the node tree for a decoded document and returns its root.</summary>
    /// <param name="root">The document to build from, edited in place by the returned tree.</param>
    /// <param name="rootType">The CLR type the schema knows the document by, or null when unknown.</param>
    /// <param name="clrBacked">True for a MemoryPack row, where members the CLR object cannot write render disabled.</param>
    /// <param name="rootLabel">Label for the root node, normally the prefs key.</param>
    /// <param name="apply">Callback that re-applies an edited document back through the row.</param>
    /// <param name="report">Callback that surfaces the tree’s first field error on the row.</param>
    /// <returns>The root node of the built tree.</returns>
    public static EditorNode Build(
        JsonNode root, Type? rootType, bool clrBacked, string rootLabel,
        Action<JsonNode> apply, Action<string?> report) =>
        new EditorTree(root, clrBacked, apply, report).BuildNode(null, root, rootType, rootLabel, readOnly: false);

    // The root node's JSON, never replaced: every edit re-applies this whole document.
    internal JsonNode ApplyTarget { get; }

    // True for MemoryPack rows, where members the CLR object cannot write render disabled.
    internal bool ClrBacked { get; }

    // Every built node in document order, so the first error is the one the user sees first.
    internal List<EditorNode> Nodes { get; } = [];

    internal void Apply() => _apply(ApplyTarget);

    internal void ReportErrors() => _report(Nodes.FirstOrDefault(node => node.Error is not null)?.Error);

    internal void Register(EditorNode node) => Nodes.Add(node);

    internal EditorNode BuildNode(
        EditorNode? parent, JsonNode? json, Type? declaredType, string label, bool readOnly)
    {
        var shape = ValueSchema.Describe(json, declaredType);

        if (shape.Kind is ValueKind.Object or ValueKind.Array or ValueKind.Dictionary)
        {
            var container = new ContainerNode(this, parent, json, label, readOnly, shape);
            switch (shape.Kind)
            {
                case ValueKind.Object:
                    foreach (var (name, node) in json!.AsObject())
                    {
                        var member = shape.Type is null ? null : ObjectJson.FindMember(shape.Type, name);
                        var memberReadOnly = readOnly
                            || (ClrBacked && member is not null && !ObjectJson.CanWrite(member));
                        var memberType = member is null ? null : ObjectJson.MemberType(member);
                        container.AddChild(BuildNode(container, node, memberType, name, memberReadOnly));
                    }
                    break;

                case ValueKind.Array:
                {
                    var array = json!.AsArray();
                    for (var i = 0; i < array.Count; i++)
                    {
                        container.AttachChild(
                            BuildNode(container, array[i], shape.ElementType, $"[{i}]", readOnly), null);
                    }
                    break;
                }

                default:
                    foreach (var (key, node) in json!.AsObject())
                        container.AttachChild(BuildNode(container, node, shape.ValueType, key, readOnly), key);
                    break;
            }

            container.IsExpanded = parent is null || container.Children.Count <= 8;
            return container;
        }

        return new LeafNode(this, parent, json, label, declaredType, readOnly, shape.Kind);
    }
}
