using FakeAttributes;
using MemoryPack;
using TheOne.Extensions;

namespace FakeGameData;

/// <summary>An enum member type, serialized by name in the JSON view.</summary>
public enum FakeRarity : byte
{
    Common = 0,
    Rare = 1,
    Legendary = 7,
}

/// <summary>A representative save type covering every scalar, collection and nested shape the bridge handles.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakePlayerData")]
public partial class FakePlayerData
{
    /// <summary>Scalar member, the simplest field the editor renders.</summary>
    [MemoryPackOrder(0)] public int Gold { get; set; }

    /// <summary>Boolean member, shown as a checkbox.</summary>
    [MemoryPackOrder(1)] public bool Unlocked { get; set; }

    /// <summary>String member.</summary>
    [MemoryPackOrder(2)] public string Name { get; set; } = "";

    /// <summary>Date and time member, parsed and rendered as ISO text.</summary>
    [MemoryPackOrder(3)] public DateTime LastSeen { get; set; }

    /// <summary>List member, shown as a growable array.</summary>
    [MemoryPackOrder(4)] public List<string> Items { get; set; } = new();

    /// <summary>Dictionary member with editable string keys.</summary>
    [MemoryPackOrder(5)] public Dictionary<string, int> Counts { get; set; } = new();

    /// <summary>Set member, serialized as a JSON array.</summary>
    [MemoryPackOrder(6)] public HashSet<string> Flags { get; set; } = new();

    /// <summary>Nested object member, expanded into its own children.</summary>
    [MemoryPackOrder(7)] public FakeNested Nested { get; set; } = new();

    /// <summary>Enum member, shown as a combo of its named values.</summary>
    [MemoryPackOrder(8)] public FakeRarity Rarity { get; set; }

    /// <summary>Struct member, rendered from its fields.</summary>
    [MemoryPackOrder(9)] public FakeVector3 Position { get; set; }

    /// <summary>Primitive array member whose element type drives the default an added element gets.</summary>
    [MemoryPackOrder(10)] public double[] Samples { get; set; } = Array.Empty<double>();
}

/// <summary>A nested object type reached through a member of the save class.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeNested
{
    /// <summary>Nested scalar, edited in place inside the parent's tree.</summary>
    [MemoryPackOrder(0)] public int Value { get; set; }

    /// <summary>Nested string, proving sibling fields survive an edit to Value.</summary>
    [MemoryPackOrder(1)] public string Label { get; set; } = "";
}

/// <summary>
/// Frameworks that key their saves by the data type's own name carry no <c>[Key]</c>: the
/// PlayerPrefs key "FakeUnkeyedData" resolves through the simple-name fallback. Its <c>[Ghost]</c>
/// member attribute lives in an assembly the editor does not load, and the member it marks is
/// private — stored only because of <c>[MemoryPackInclude]</c>.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeUnkeyedData
{
    /// <summary>Public scalar on a type with no <c>[Key]</c>; the key resolves by simple name.</summary>
    [MemoryPackOrder(0)] public int Level { get; set; }

    [MemoryPackOrder(1)]
    [MemoryPackInclude]
    [Ghost("unreadable")]
    string Tag { get; set; } = "";
}

/// <summary>
/// An element type with no parameterless constructor, like the ones real save classes have: editing
/// one has to mutate it, because the editor cannot construct a replacement.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeElementData(int value)
{
    /// <summary>Scalar member of an element type that has no parameterless constructor.</summary>
    [MemoryPackOrder(0)] public int Value { get; set; } = value;

    /// <summary>String sibling kept when the element is edited in place.</summary>
    [MemoryPackOrder(1)] public string Label { get; set; } = "";
}

/// <summary>Holds a dictionary and a list of the constructor-less element type.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakeCollectionData")]
public partial class FakeCollectionData
{
    /// <summary>Dictionary of element objects, each edited where it already sits.</summary>
    [MemoryPackOrder(0)] public Dictionary<string, FakeElementData> Items { get; set; } = new();

    /// <summary>List of element objects, appended and re-indexed by the array editor.</summary>
    [MemoryPackOrder(1)] public List<FakeElementData> Ordered { get; set; } = new();

    /// <summary>Populated state for tests: nothing outside the game can build the elements.</summary>
    public static FakeCollectionData Sample() => new()
    {
        Items = { ["a"] = new FakeElementData(1) { Label = "kept" } },
        Ordered = [new FakeElementData(2) { Label = "kept" }],
    };
}

/// <summary>Closest we get to a Unity struct without UnityEngine present.</summary>
[MemoryPackable]
public partial struct FakeVector3
{
    /// <summary>First component of the fake vector struct.</summary>
    [MemoryPackOrder(0)] public float X { get; set; }

    /// <summary>Second component of the fake vector struct.</summary>
    [MemoryPackOrder(1)] public float Y { get; set; }

    /// <summary>Third component of the fake vector struct.</summary>
    [MemoryPackOrder(2)] public float Z { get; set; }
}

/// <summary>A save type with nullable members: a JSON null must clear them instead of throwing.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeOptionalData
{
    /// <summary>Nullable integer, cleared by a JSON null instead of throwing.</summary>
    [MemoryPackOrder(0)] public int? Count { get; set; }

    /// <summary>Nullable boolean, cleared by a JSON null instead of throwing.</summary>
    [MemoryPackOrder(1)] public bool? Enabled { get; set; }
}

/// <summary>Nullable dictionary values: JSON null entries have no node instance to be found by.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeNullableMap
{
    /// <summary>Dictionary with nullable values, exercising null entries that have no node instance.</summary>
    [MemoryPackOrder(0)] public Dictionary<string, int?> Values { get; set; } = new();
}

/// <summary>
/// A save type whose only parameterless constructor is non-public, the way MemoryPack's generated
/// types look: the formatter generated inside the type can still call it.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakePrivateCtorData
{
    /// <summary>Scalar on a type whose only parameterless constructor is non-public.</summary>
    [MemoryPackOrder(0)] public int Level { get; set; }

    /// <summary>String default the formatter must restore for a private-ctor type.</summary>
    [MemoryPackOrder(1)] public string Name { get; set; } = "unset";

    FakePrivateCtorData()
    {
    }
}

/// <summary>The shape real saves have: a dictionary of dictionaries of game types.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakeLevelMapData")]
public partial class FakeLevelMapData
{
    /// <summary>Simple dictionary, the outer map of a real save's nesting.</summary>
    [MemoryPackOrder(0)] public Dictionary<string, int> ModeToCurrentLevel { get; set; } = new();

    /// <summary>Dictionary of dictionaries of game types, the deepest shape a save uses.</summary>
    [MemoryPackOrder(1)] public Dictionary<string, Dictionary<int, FakePrivateCtorData>> ModeToLevelToLevelData { get; set; } = new();
}

/// <summary>Nested container shapes the field editor has to keep growing.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakeNestedShapes")]
public partial class FakeNestedShapes
{
    /// <summary>Dictionary of dictionaries, doubly nested.</summary>
    [MemoryPackOrder(0)] public Dictionary<string, Dictionary<string, int>> Sections { get; set; } = new();

    /// <summary>Dictionary of string lists.</summary>
    [MemoryPackOrder(1)] public Dictionary<string, List<string>> Groups { get; set; } = new();

    /// <summary>Dictionary of primitive arrays.</summary>
    [MemoryPackOrder(2)] public Dictionary<string, int[]> Rolled { get; set; } = new();

    /// <summary>Flat string-to-int dictionary.</summary>
    [MemoryPackOrder(3)] public Dictionary<string, int> Plain { get; set; } = new();

    /// <summary>Dictionary of element objects.</summary>
    [MemoryPackOrder(4)] public Dictionary<string, FakeElementData> Blocks { get; set; } = new();

    /// <summary>Boolean-keyed dictionary, exercising the remaining-value key rule.</summary>
    [MemoryPackOrder(5)] public Dictionary<bool, string> Switches { get; set; } = new();

    /// <summary>Integer-keyed dictionary.</summary>
    [MemoryPackOrder(6)] public Dictionary<int, string> Ranked { get; set; } = new();

    /// <summary>Dictionary of dictionaries of the private-ctor element type.</summary>
    [MemoryPackOrder(7)] public Dictionary<string, Dictionary<int, FakePrivateCtorData>> Levels { get; set; } = new();
}
