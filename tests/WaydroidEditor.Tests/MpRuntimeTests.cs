using System.Text.Json.Nodes;
using FakeGameData;
using WaydroidEditor.Core;
using Xunit;

namespace WaydroidEditor.Tests;

/// <summary>
/// Drives the whole bridge the way the app does: the game's compiled assembly is loaded through
/// the private ALC, decoded, edited as JSON, and re-encoded.
/// </summary>
public class MpRuntimeTests : IDisposable
{
    readonly string _gameDir = Path.Combine(Path.GetTempPath(), "wpe-game-" + Guid.NewGuid().ToString("N"));

    public MpRuntimeTests()
    {
        Directory.CreateDirectory(_gameDir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "FakeGameData.dll"),
            Path.Combine(_gameDir, "FakeGameData.dll"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_gameDir, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    static readonly string SampleJson = """
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

    [Fact]
    public void LoadsTheGameAssemblyAndResolvesTheKeyedType()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);

        Assert.Empty(runtime.LoadWarnings);
        var type = runtime.ResolveTypeForKey("FakePlayerData");
        Assert.NotNull(type);
        Assert.True(MpRuntime.IsMemoryPackable(type!));
        Assert.Null(runtime.ResolveTypeForKey("NoSuchKey"));
    }

    [Fact]
    public void ResolvesSaveTypesThatCarryNoKeyAttribute()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);

        // Frameworks that key their saves by the data type's own name put no [Key] on the type:
        // the PlayerPrefs key is the simple name, not a name somebody remembered to attribute.
        var type = runtime.ResolveTypeForKey("FakeUnkeyedData");

        Assert.Equal("FakeGameData.FakeUnkeyedData", type?.FullName);
        Assert.True(MpRuntime.IsMemoryPackable(type!));
    }

    [Fact]
    public void PrivateIncludedMembersStayEditableWhenAttributesCannotBeInstantiated()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);
        var type = runtime.ResolveTypeForKey("FakeUnkeyedData")!;
        var value = Activator.CreateInstance(type)!;
        Assert.Empty(ObjectJson.ApplyJson(value, type, JsonNode.Parse("""{ "Level": 3, "Tag": "boss" }""")!));

        // Tag is private, so only its [MemoryPackInclude] keeps it in the save — and that attribute
        // sits next to [Ghost], from an assembly this DLL folder does not contain. Both facts have to
        // come out of metadata: instantiating the member's attributes throws, and guessing "no
        // include" would quietly drop the value the user came to edit.
        var json = ObjectJson.ToJson(runtime.Decode(type, runtime.Encode(type, value))!, type)!.AsObject();

        Assert.Equal(3, json["Level"]!.GetValue<int>());
        Assert.Equal("boss", json["Tag"]!.GetValue<string>());
    }

    [Fact]
    public void EditsInsideCollectionsKeepTheirElementInstances()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);
        var type = runtime.ResolveTypeForKey("FakeCollectionData")!;

        // The sample has to come out of the game's own load context — the copy this test compiles
        // against is a different type identity. FakeElementData has no parameterless constructor, so
        // the fixture is the only thing that can build the elements at all.
        var sample = type.GetMethod(nameof(FakeCollectionData.Sample))!.Invoke(null, null);
        var value = runtime.Decode(type, runtime.Encode(type, sample!))!;

        // Elements are edited where they sit: FakeElementData has no parameterless constructor, so a
        // rebuilt element would throw, and the [Label] this JSON leaves out has to survive the edit.
        var edited = JsonNode.Parse("""{ "Items": { "a": { "Value": 9 } }, "Ordered": [{ "Value": 8 }] }""")!;
        Assert.Empty(ObjectJson.ApplyJson(value, type, edited));

        var json = ObjectJson.ToJson(runtime.Decode(type, runtime.Encode(type, value))!, type)!.AsObject();
        Assert.Equal(9, json["Items"]!["a"]!["Value"]!.GetValue<int>());
        Assert.Equal("kept", json["Items"]!["a"]!["Label"]!.GetValue<string>());
        Assert.Equal(8, json["Ordered"]![0]!["Value"]!.GetValue<int>());
        Assert.Equal("kept", json["Ordered"]![0]!["Label"]!.GetValue<string>());
    }

    [Fact]
    public void LoadsFromAFolderInsteadOfIndividualFiles()
    {
        // The game's types come as a folder (Library/ScriptAssemblies, Data/Managed), which is what
        // users point the DLL list at; a folder entry has to stand for the assemblies inside it.
        using var runtime = MpRuntime.Load([_gameDir]);

        Assert.Empty(runtime.LoadWarnings);
        Assert.Equal(1, runtime.LoadedAssemblies);
        Assert.NotNull(runtime.ResolveTypeForKey("FakePlayerData"));
    }

    [Fact]
    public void RegistersGameFormattersFromRuntimeInitializeHooks()
    {
        // A save type with a member the game does not own (FakeWrapped stands in for R3's
        // ReactiveProperty<T>): its formatter exists only because the game registers it from a Unity
        // [RuntimeInitializeOnLoadMethod] hook. Unless that hook is replayed the generated
        // FakeWrappedData formatter dies with "… is not registered in this provider".
        using var runtime = MpRuntime.Load([_gameDir]);

        var type = runtime.ResolveTypeForKey("FakeWrappedData")!;
        var bytes = runtime.Encode(type, Activator.CreateInstance(type)!);

        Assert.Equal(bytes, runtime.Encode(type, runtime.Decode(type, bytes)!));
    }

    [Fact]
    public void LoadsAssembliesFromNestedSubfolders()
    {
        var deep = Path.Combine(_gameDir, "a", "b");
        Directory.CreateDirectory(deep);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "FakeGameData.dll"),
            Path.Combine(deep, "FakeGameData.dll"));

        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "a")]);

        Assert.Empty(runtime.LoadWarnings);
        Assert.Equal(1, runtime.LoadedAssemblies);
        Assert.NotNull(runtime.ResolveTypeForKey("FakePlayerData"));
    }

    [Fact]
    public void DuplicateAssemblyNamesKeepTheShallowestCopy()
    {
        var nested = Path.Combine(_gameDir, "nested", "FakePlayerData");
        Directory.CreateDirectory(nested);
        var rootCopy = Path.Combine(_gameDir, "FakeGameData.dll");
        var nestedCopy = Path.Combine(nested, "FakeGameData.dll");
        File.Copy(rootCopy, nestedCopy);

        using var runtime = MpRuntime.Load([rootCopy, nestedCopy]);

        Assert.NotNull(runtime.ResolveTypeForKey("FakePlayerData"));
        Assert.Single(runtime.LoadWarnings, w => w.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void ShipsBundledUnityEngine()
    {
        var resources = typeof(MpRuntime).Assembly.GetManifestResourceNames();
        Assert.Contains("WaydroidEditor.Unity.UnityEngine.dll", resources);
        Assert.Contains("WaydroidEditor.Unity.UnityEngine.CoreModule.dll", resources);
    }

    [Fact]
    public void EditsSurviveAToJsonApplyJsonEncodeRoundTrip()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);
        var type = runtime.ResolveTypeForKey("FakePlayerData")!;
        var original = Activator.CreateInstance(type)!;
        ObjectJson.ApplyJson(original, type, JsonNode.Parse(SampleJson)!);
        var bytes = runtime.Encode(type, original);

        // An unedited graph must re-encode to the identical bytes — the UI's round-trip guard.
        var decoded = runtime.Decode(type, bytes)!;
        var edited = ObjectJson.ToJson(decoded, type)!;
        Assert.Equal(bytes, runtime.Encode(type, decoded));

        edited["Gold"] = 99;
        edited["Nested"]!["Label"] = "changed";
        edited["Items"]!.AsArray().Add("potion");

        Assert.Empty(ObjectJson.ApplyJson(decoded, type, edited));

        var reDecoded = runtime.Decode(type, runtime.Encode(type, decoded))!;
        var view = ObjectJson.ToJson(reDecoded, type)!.AsObject();
        Assert.Equal(99, view["Gold"]!.GetValue<int>());
        Assert.Equal("changed", view["Nested"]!["Label"]!.GetValue<string>());
        Assert.Equal(new[] { "sword", "shield", "potion" }, view["Items"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Legendary", view["Rarity"]!.GetValue<string>());
        Assert.Equal(3.5f, view["Position"]!["Z"]!.GetValue<float>());
        Assert.Equal(7, view["Counts"]!["gems"]!.GetValue<int>());
    }

    [Fact]
    public void RejectsUnknownMembersWithoutClearingTheRest()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);
        var type = runtime.ResolveTypeForKey("FakePlayerData")!;
        var instance = Activator.CreateInstance(type)!;
        ObjectJson.ApplyJson(instance, type, JsonNode.Parse(SampleJson)!);

        var edited = ObjectJson.ToJson(instance, type)!.AsObject();
        edited["NotAMember"] = 1;

        var warnings = ObjectJson.ApplyJson(instance, type, edited);
        Assert.Contains(warnings, w => w.Contains("NotAMember", StringComparison.Ordinal));
        Assert.Equal(42, ObjectJson.ToJson(instance, type)!["Gold"]!.GetValue<int>());
    }

    [Fact]
    public void MismatchedJsonShapeThrowsWithTheMemberPath()
    {
        using var runtime = MpRuntime.Load([Path.Combine(_gameDir, "FakeGameData.dll")]);
        var type = runtime.ResolveTypeForKey("FakePlayerData")!;
        var instance = Activator.CreateInstance(type)!;

        var error = Assert.Throws<JsonBridgeException>(
            () => ObjectJson.ApplyJson(instance, type, JsonNode.Parse("""{ "Gold": "lots" }""")!));
        Assert.Contains("$.Gold", error.Message, StringComparison.Ordinal);
    }
}
