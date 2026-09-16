using FakeAttributes;
using MemoryPack;
using TheOne.Extensions;

namespace FakeGameData;

public enum FakeRarity : byte
{
    Common = 0,
    Rare = 1,
    Legendary = 7,
}

[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakePlayerData")]
public partial class FakePlayerData
{
    [MemoryPackOrder(0)] public int Gold { get; set; }
    [MemoryPackOrder(1)] public bool Unlocked { get; set; }
    [MemoryPackOrder(2)] public string Name { get; set; } = "";
    [MemoryPackOrder(3)] public DateTime LastSeen { get; set; }
    [MemoryPackOrder(4)] public List<string> Items { get; set; } = new();
    [MemoryPackOrder(5)] public Dictionary<string, int> Counts { get; set; } = new();
    [MemoryPackOrder(6)] public HashSet<string> Flags { get; set; } = new();
    [MemoryPackOrder(7)] public FakeNested Nested { get; set; } = new();
    [MemoryPackOrder(8)] public FakeRarity Rarity { get; set; }
    [MemoryPackOrder(9)] public FakeVector3 Position { get; set; }
    [MemoryPackOrder(10)] public double[] Samples { get; set; } = Array.Empty<double>();
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeNested
{
    [MemoryPackOrder(0)] public int Value { get; set; }
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
    [MemoryPackOrder(0)] public int Value { get; set; } = value;
    [MemoryPackOrder(1)] public string Label { get; set; } = "";
}

[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakeCollectionData")]
public partial class FakeCollectionData
{
    [MemoryPackOrder(0)] public Dictionary<string, FakeElementData> Items { get; set; } = new();
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
    [MemoryPackOrder(0)] public float X { get; set; }
    [MemoryPackOrder(1)] public float Y { get; set; }
    [MemoryPackOrder(2)] public float Z { get; set; }
}

/// <summary>A save type with nullable members: a JSON null must clear them instead of throwing.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeOptionalData
{
    [MemoryPackOrder(0)] public int? Count { get; set; }
    [MemoryPackOrder(1)] public bool? Enabled { get; set; }
}

/// <summary>Nullable dictionary values: JSON null entries have no node instance to be found by.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakeNullableMap
{
    [MemoryPackOrder(0)] public Dictionary<string, int?> Values { get; set; } = new();
}

/// <summary>
/// A save type whose only parameterless constructor is non-public, the way MemoryPack's generated
/// types look: the formatter generated inside the type can still call it.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class FakePrivateCtorData
{
    [MemoryPackOrder(0)] public int Level { get; set; }
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
    [MemoryPackOrder(0)] public Dictionary<string, int> ModeToCurrentLevel { get; set; } = new();
    [MemoryPackOrder(1)] public Dictionary<string, Dictionary<int, FakePrivateCtorData>> ModeToLevelToLevelData { get; set; } = new();
}

/// <summary>Nested container shapes the field editor has to keep growing.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakeNestedShapes")]
public partial class FakeNestedShapes
{
    [MemoryPackOrder(0)] public Dictionary<string, Dictionary<string, int>> Sections { get; set; } = new();
    [MemoryPackOrder(1)] public Dictionary<string, List<string>> Groups { get; set; } = new();
    [MemoryPackOrder(2)] public Dictionary<string, int[]> Rolled { get; set; } = new();
    [MemoryPackOrder(3)] public Dictionary<string, int> Plain { get; set; } = new();
    [MemoryPackOrder(4)] public Dictionary<string, FakeElementData> Blocks { get; set; } = new();
    [MemoryPackOrder(5)] public Dictionary<bool, string> Switches { get; set; } = new();
    [MemoryPackOrder(6)] public Dictionary<int, string> Ranked { get; set; } = new();
    [MemoryPackOrder(7)] public Dictionary<string, Dictionary<int, FakePrivateCtorData>> Levels { get; set; } = new();
}
