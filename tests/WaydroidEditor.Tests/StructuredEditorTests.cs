using System.Text.Json.Nodes;
using WaydroidEditor;
using WaydroidEditor.Core;
using Xunit;

namespace WaydroidEditor.Tests;

/// <summary>
/// Drives the structured editor the way the window does: the game's own compiled type resolves the
/// key, the tree is built from the decoded document, fields are edited, and the row re-encodes.
/// </summary>
public sealed class StructuredEditorTests : IDisposable
{
    const string SampleJson = """
        {
          "Gold": 42,
          "Unlocked": true,
          "Name": "hero",
          "LastSeen": "2024-01-02T03:04:05.0000000Z",
          "Items": ["sword", "shield"],
          "Counts": { "gems": 7, "keys": 2 },
          "Flags": ["tutorial_done"],
          "Nested": { "Value": 11, "Label": "deep" },
          "Rarity": "Legendary",
          "Position": { "X": 1.5, "Y": 2.5, "Z": 3.5 },
          "Samples": [0.25, 0.5]
        }
        """;

    readonly MpRuntime _runtime;
    readonly Type _playerType;

    public StructuredEditorTests()
    {
        _runtime = MpRuntime.Load([Path.Combine(AppContext.BaseDirectory, "FakeGameData.dll")]);
        _playerType = _runtime.ResolveTypeForKey("FakePlayerData")!;
    }

    public void Dispose()
    {
        _runtime.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MemoryPackRowBuildsATypedEditorTree()
    {
        var row = MemoryPackRow(_playerType, SampleJson);

        var root = Container(row.Editor);
        Assert.Equal("FakePlayerData", root.Label);
        Assert.Equal("FakePlayerData", root.TypeLabel);
        Assert.True(root.IsExpanded);
        Assert.Equal(
            new[]
            {
                "Gold", "Unlocked", "Name", "LastSeen", "Items", "Counts", "Flags", "Nested", "Rarity",
                "Position", "Samples",
            },
            root.Children.Select(child => child.Label));

        var gold = Leaf(root.Children[0]);
        Assert.True(gold.IsText);
        Assert.Equal("int", gold.TypeLabel);
        Assert.Equal("42", gold.ValueText);

        var unlocked = Leaf(root.Children[1]);
        Assert.True(unlocked.IsBool);
        Assert.True(unlocked.IsChecked);

        Assert.Equal(ValueKind.Array, Container(root.Children[4]).Kind);
        Assert.Equal(ValueKind.Dictionary, Container(root.Children[5]).Kind);
        Assert.Equal(ValueKind.Array, Container(root.Children[6]).Kind);
        Assert.Equal(ValueKind.Object, Container(root.Children[7]).Kind);

        var rarity = Leaf(root.Children[8]);
        Assert.True(rarity.IsEnum);
        Assert.Contains("Legendary", rarity.Choices);
        Assert.Equal("Legendary", rarity.SelectedChoice);

        Assert.Equal(ValueKind.Object, Container(root.Children[9]).Kind);
        Assert.Equal(ValueKind.Array, Container(root.Children[10]).Kind);
        Assert.Equal(new[] { "Value", "Label" }, Container(root.Children[7]).Children.Select(child => child.Label));
        Assert.Equal(new[] { "X", "Y", "Z" }, Container(root.Children[9]).Children.Select(child => child.Label));

        Assert.True(Container(root.Children[4]).CanAdd);
        Assert.True(Container(root.Children[5]).CanAdd);
        Assert.False(Container(root.Children[7]).CanAdd);
    }

    [Fact]
    public void EditingOneFieldLeavesTheOtherFieldsAlone()
    {
        var row = MemoryPackRow(_playerType, SampleJson);
        var gold = Leaf(Container(row.Editor).Children[0]);

        gold.ValueText = "99";

        Assert.Null(gold.Error);
        Assert.False(row.HasError);
        var view = Decoded(row, _playerType);
        Assert.Equal(99, view["Gold"]!.GetValue<int>());
        Assert.Equal("hero", view["Name"]!.GetValue<string>());
        Assert.Equal(new[] { "sword", "shield" }, view["Items"]!.AsArray().Select(item => item!.GetValue<string>()));

        var rarity = Leaf(Container(row.Editor).Children[8]);
        rarity.SelectedChoice = "Rare";

        Assert.False(row.HasError);
        Assert.Equal("Rare", Decoded(row, _playerType)["Rarity"]!.GetValue<string>());
    }

    [Fact]
    public void InvalidFieldInputKeepsTheStoredValueAndBlocksSaving()
    {
        var row = MemoryPackRow(_playerType, SampleJson);
        var stored = row.RawValue;
        var gold = Leaf(Container(row.Editor).Children[0]);

        gold.ValueText = "lots";

        Assert.Contains("FakePlayerData.Gold", gold.Error, StringComparison.Ordinal);
        Assert.True(row.HasError);
        Assert.Equal(stored, row.RawValue);
        Assert.Equal("lots", gold.ValueText);

        gold.ValueText = "7";

        Assert.Null(gold.Error);
        Assert.False(row.HasError);
        Assert.Equal(7, Decoded(row, _playerType)["Gold"]!.GetValue<int>());
    }

    [Fact]
    public void AddingThenRemovingAListItemRestoresTheOriginalBytes()
    {
        var row = MemoryPackRow(_playerType, SampleJson);
        var stored = row.RawValue;
        var items = Container(Container(row.Editor).Children[4]);

        items.AddCommand!.Execute(null);

        Assert.Equal(3, items.Children.Count);
        Assert.Equal("[2]", items.Children[2].Label);
        Assert.Equal(
            new[] { "sword", "shield", "" },
            Decoded(row, _playerType)["Items"]!.AsArray().Select(item => item!.GetValue<string>()));

        items.Children[2].RemoveCommand!.Execute(null);

        Assert.Equal(2, items.Children.Count);
        Assert.Equal("[0]", items.Children[0].Label);
        Assert.Equal(stored, row.RawValue);
    }

    [Fact]
    public void DictionaryEntriesCanBeAddedRenamedAndRemoved()
    {
        var row = MemoryPackRow(_playerType, SampleJson);
        var counts = Container(Container(row.Editor).Children[5]);

        counts.AddCommand!.Execute(null);

        var added = counts.Children[^1];
        Assert.Equal("NewKey", added.KeyText);
        Assert.True(added.KeyEditable);

        added.KeyText = "swords";

        Assert.Null(added.Error);
        var view = Decoded(row, _playerType);
        Assert.Equal(0, view["Counts"]!["swords"]!.GetValue<int>());
        Assert.Equal(3, view["Counts"]!.AsObject().Count);

        counts.Children.Single(child => child.KeyText == "gems").RemoveCommand!.Execute(null);

        Assert.Null(Decoded(row, _playerType)["Counts"]!["gems"]);
        Assert.False(row.HasError);
    }

    [Fact]
    public void ElementsWithoutAParameterlessConstructorAreAddedAsZeroedInstances()
    {
        var type = _runtime.ResolveTypeForKey("FakeCollectionData")!;
        var sample = type.GetMethod("Sample")!.Invoke(null, null)!;
        var row = MemoryPackRow(type, sample, type);

        var root = Container(row.Editor);
        var entries = Container(root.Children[0]);
        var ordered = Container(root.Children[1]);

        // FakeElementData only has FakeElementData(int): a zeroed instance is what the apply path can
        // build for a new entry, and MemoryPack encodes it like any other.
        Assert.True(entries.CanAdd);
        Assert.True(ordered.CanAdd);
        Assert.True(entries.Children[0].CanRemove);

        entries.AddCommand!.Execute(null);
        ordered.AddCommand!.Execute(null);

        Assert.False(row.HasError);
        var added = Container(ordered.Children[^1]);
        Assert.Equal(new[] { "Value", "Label" }, added.Children.Select(child => child.Label));
        Assert.Equal("0", Leaf(added.Children[0]).ValueText);
        Assert.True(Leaf(added.Children[1]).IsText); // a null member is still typed into

        var view = ObjectJson.ToJson(_runtime.Decode(type, Convert.FromBase64String(row.RawValue))!, type)!.AsObject();
        Assert.Equal(0, view["Items"]!["NewKey"]!["Value"]!.GetValue<int>());
        Assert.Equal(2, view["Ordered"]!.AsArray().Count);
        Assert.Equal(0, view["Ordered"]![1]!["Value"]!.GetValue<int>());

        // Editing the element that is already there still lands in the re-encoded bytes: the apply
        // path mutates the decoded instance instead of constructing a replacement.
        var value = Leaf(Container(entries.Children[0]).Children[0]);
        Assert.Equal("Value", value.Label);
        value.ValueText = "5";

        Assert.False(row.HasError);
        view = ObjectJson.ToJson(_runtime.Decode(type, Convert.FromBase64String(row.RawValue))!, type)!.AsObject();
        Assert.Equal(5, view["Items"]!["a"]!["Value"]!.GetValue<int>());
        Assert.Equal("kept", view["Items"]!["a"]!["Label"]!.GetValue<string>());
    }

    [Fact]
    public void AddedElementsCarryTheDeclaredElementType()
    {
        // Samples is double[]: the added default has to be a double node, or applying it back
        // through ParseScalar would fail on an int-backed JSON value.
        var row = MemoryPackRow(_playerType, SampleJson);
        var samples = Container(Container(row.Editor).Children[10]);

        samples.AddCommand!.Execute(null);

        Assert.Null(samples.Children[^1].Error);
        Assert.False(row.HasError);
        Assert.Equal(
            new[] { 0.25, 0.5, 0d },
            Decoded(row, _playerType)["Samples"]!.AsArray().Select(item => item!.GetValue<double>()));

        samples.Children[^1].RemoveCommand!.Execute(null);

        Assert.False(row.HasError);
        Assert.Equal(2, samples.Children.Count);
    }

    [Fact]
    public void JsonRowValuesAreEditedThroughTheSchema()
    {
        var row = JsonRow("FakePlayerData", SampleJson);

        var unlocked = Leaf(Container(row.Editor).Children.Single(child => child.Label == "Unlocked"));
        Assert.True(unlocked.IsChecked);

        unlocked.IsChecked = false;

        Assert.False(row.HasError);
        Assert.Contains("\"Unlocked\":false", row.RawValue, StringComparison.Ordinal);
        Assert.Contains("\"Samples\":[0.25,0.5]", row.RawValue, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsAbsentFromTheTypeFallBackToInferredEditors()
    {
        var row = JsonRow("FakePlayerData", """{"Gold":"many","Extra":{"a":[1,2]}}""");

        var root = Container(row.Editor);
        var gold = Leaf(root.Children.Single(child => child.Label == "Gold"));
        Assert.True(gold.IsText);
        Assert.Equal("int", gold.TypeLabel);
        Assert.Equal("many", gold.ValueText);

        var extra = Container(root.Children.Single(child => child.Label == "Extra"));
        Assert.Equal("", extra.TypeLabel);
        var array = Container(extra.Children.Single(child => child.Label == "a"));
        Assert.Equal(ValueKind.Array, array.Kind);
        Assert.Equal("", array.TypeLabel);
        Assert.False(array.CanAdd);

        gold.ValueText = "3";

        Assert.False(row.HasError);
        Assert.Contains("\"Gold\":3", row.RawValue, StringComparison.Ordinal);
    }

    [Fact]
    public void NullForANullableMemberAppliesButStillThrowsForPlainInts()
    {
        var type = _runtime.ResolveTypeForKey("FakeOptionalData")!;
        var instance = Activator.CreateInstance(type)!;

        Assert.Empty(ObjectJson.ApplyJson(instance, type, JsonNode.Parse("""{ "Count": 3 }""")!));
        Assert.Empty(ObjectJson.ApplyJson(instance, type, JsonNode.Parse("""{ "Count": null }""")!));

        Assert.Null(ObjectJson.ToJson(instance, type)!.AsObject()["Count"]);

        var error = Assert.Throws<JsonBridgeException>(() => ObjectJson.ApplyJson(
            Activator.CreateInstance(_playerType)!, _playerType, JsonNode.Parse("""{ "Gold": null }""")!));
        Assert.Contains("$.Gold", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingANullableBooleanWritesNullAndCanBeSetAgain()
    {
        var type = _runtime.ResolveTypeForKey("FakeOptionalData")!;
        var row = MemoryPackRow(type, Applied(type, """{ "Count": 3, "Enabled": true }"""), type);

        var enabled = Leaf(Container(row.Editor).Children.Single(child => child.Label == "Enabled"));
        Assert.True(enabled.IsThreeState);
        Assert.True(enabled.IsChecked);

        enabled.IsChecked = null;

        Assert.False(row.HasError);
        Assert.Null(Decoded(row, type)["Enabled"]);

        // A JSON null has no node instance to be located by, so the second edit proves the slot
        // is still found: it writes through the entry's name.
        enabled.IsChecked = false;

        Assert.False(row.HasError);
        Assert.False(Decoded(row, type)["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void RenamingANullValuedEntryMovesOnlyThatEntry()
    {
        var type = _runtime.ResolveTypeForKey("FakeNullableMap")!;
        var row = MemoryPackRow(type, Applied(type, """{ "Values": { "first": null, "second": null } }"""), type);

        var values = Container(Container(row.Editor).Children[0]);
        Assert.Equal(2, values.Children.Count);
        Assert.All(values.Children, child => Assert.Empty(Leaf(child).ValueText));

        values.Children[1].KeyText = "renamed";

        Assert.False(row.HasError);
        var view = Decoded(row, type)["Values"]!.AsObject();
        Assert.Equal(new[] { "first", "renamed" }, view.Select(entry => entry.Key));
        Assert.Null(view["first"]);
        Assert.Null(view["renamed"]);

        values.Children[1].RemoveCommand!.Execute(null);

        Assert.Equal(new[] { "first" }, Decoded(row, type)["Values"]!.AsObject().Select(entry => entry.Key));
    }

    [Fact]
    public void DictionaryEntriesWithCollectionValuesCanBeAdded()
    {
        var type = _runtime.ResolveTypeForKey("FakeNestedShapes")!;
        var row = MemoryPackRow(
            type, Applied(type, """{"Groups":{"party":["a"]},"Rolled":{"luck":[1,2]},"Blocks":{}}"""), type);

        var root = Container(row.Editor);
        var groups = Container(root.Children.Single(child => child.Label == "Groups"));
        var rolled = Container(root.Children.Single(child => child.Label == "Rolled"));

        Assert.True(groups.CanAdd);
        Assert.True(rolled.CanAdd);

        groups.AddCommand!.Execute(null);
        rolled.AddCommand!.Execute(null);

        Assert.False(row.HasError);
        var view = Decoded(row, type);
        Assert.Empty(view["Groups"]!["NewKey"]!.AsArray());
        Assert.Empty(view["Rolled"]!["NewKey"]!.AsArray());

        // The list the new entry created takes elements like any other container.
        var added = Container(groups.Children[^1]);
        Assert.True(added.CanAdd);
        added.AddCommand!.Execute(null);

        Assert.False(row.HasError);
        Assert.Equal(
            new[] { "" },
            Decoded(row, type)["Groups"]!["NewKey"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void BooleanKeyedEntriesUseTheRemainingValue()
    {
        var type = _runtime.ResolveTypeForKey("FakeNestedShapes")!;
        var row = MemoryPackRow(type, Applied(type, """{"Switches":{"true":"on"}}"""), type);

        var switches = Container(Container(row.Editor).Children.Single(child => child.Label == "Switches"));
        Assert.True(switches.CanAdd);

        switches.AddCommand!.Execute(null);

        Assert.False(row.HasError);
        var view = Decoded(row, type)["Switches"]!.AsObject();
        Assert.Equal(new[] { "True", "False" }, view.Select(entry => entry.Key));
        Assert.Equal("on", view["True"]!.GetValue<string>());
        Assert.Equal("", view["False"]!.GetValue<string>());
    }

    [Fact]
    public void DictionaryEntriesBuiltByANonPublicConstructorCanBeAdded()
    {
        // The shape real saves have: a nested dictionary whose values are game types MemoryPack
        // materializes through a constructor it keeps to itself.
        var type = _runtime.ResolveTypeForKey("FakeNestedShapes")!;
        var row = MemoryPackRow(
            type, Applied(type, """{"Levels":{"classic":{"1":{"Level":5,"Name":"one"}}}}"""), type);

        var levels = Container(Container(row.Editor).Children.Single(child => child.Label == "Levels"));
        var style = Container(levels.Children.Single(child => child.KeyText == "classic"));
        Assert.True(style.CanAdd);

        style.AddCommand!.Execute(null);

        Assert.False(row.HasError);
        var added = Container(style.Children[^1]);
        Assert.Equal("0", added.KeyText);
        Assert.Equal("unset", Leaf(added.Children.Single(child => child.Label == "Name")).ValueText);

        var view = Decoded(row, type)["Levels"]!["classic"]!.AsObject();
        Assert.Equal(new[] { "0", "1" }, view.Select(entry => entry.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(0, view["0"]!["Level"]!.GetValue<int>());
        Assert.Equal("unset", view["0"]!["Name"]!.GetValue<string>());
    }

    EntryRow MemoryPackRow(Type type, string json) => MemoryPackRow(type, Applied(type, json), type);

    EntryRow MemoryPackRow(Type type, object instance, Type? schemaType = null)
    {
        var row = new EntryRow(
            new PrefsEntry
            {
                Key = "FakePlayerData",
                Type = PrefsType.String,
                Value = Convert.ToBase64String(_runtime.Encode(type, instance)),
            },
            _runtime);
        row.SetMode(EditMode.MemoryPack, schemaType ?? type);
        return row;
    }

    EntryRow JsonRow(string key, string json)
    {
        var row = new EntryRow(
            new PrefsEntry { Key = key, Type = PrefsType.String, Value = json }, _runtime);
        row.SetMode(EditMode.Json, _runtime.ResolveTypeForKey(key));
        return row;
    }

    static object Applied(Type type, string json)
    {
        var instance = Activator.CreateInstance(type)!;
        ObjectJson.ApplyJson(instance, type, JsonNode.Parse(json)!);
        return instance;
    }

    JsonObject Decoded(EntryRow row, Type type) =>
        ObjectJson.ToJson(_runtime.Decode(type, Convert.FromBase64String(row.RawValue))!, type)!.AsObject();

    static ContainerNode Container(EditorNode? node) => Assert.IsType<ContainerNode>(node);

    static LeafNode Leaf(EditorNode node) => Assert.IsType<LeafNode>(node);
}
