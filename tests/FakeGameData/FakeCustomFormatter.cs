using MemoryPack;
using TheOne.Extensions;

namespace FakeGameData;

/// <summary>
/// A member type the game does not own, standing in for R3's <c>ReactiveProperty&lt;T&gt;</c>: it is
/// not <c>[MemoryPackable]</c> and carries no attribute to hang a formatter on, so the generated
/// <see cref="FakeWrappedData"/> formatter can only serialize it through a formatter the game
/// registers itself.
/// </summary>
public sealed class FakeWrapped
{
    /// <summary>Creates an empty wrapper; the formatter fills it on deserialize.</summary>
    public FakeWrapped()
    {
    }

    /// <summary>Builds the wrapper around an already-decoded value.</summary>
    /// <param name="value">The integer the formatter serializes.</param>
    public FakeWrapped(int value) => Value = value;

    /// <summary>The wrapped integer the custom formatter round-trips.</summary>
    public int Value { get; set; }
}

/// <summary>Serializes <see cref="FakeWrapped"/> as its bare integer, standing in for a formatter the game registers itself.</summary>
public sealed class FakeWrappedFormatter : MemoryPackFormatter<FakeWrapped>
{
    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer, scoped ref FakeWrapped? value)
        => writer.WriteValue(value?.Value ?? 0);

    public override void Deserialize(ref MemoryPackReader reader, scoped ref FakeWrapped? value)
    {
        var inner = reader.ReadValue<int>();
        if (value is null)
            value = new FakeWrapped(inner);
        else
            value.Value = inner;
    }
}

/// <summary>
/// A save type whose member needs <see cref="FakeWrappedFormatter"/> — the shape SoundSetting has with
/// its ReactiveProperty members.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
[Key("FakeWrappedData")]
public partial class FakeWrappedData
{
    /// <summary>The member whose type has no generated formatter, resolved through the registered one.</summary>
    [MemoryPackOrder(0)] [MemoryPackAllowSerialize] public FakeWrapped Wrapped { get; set; } = new();
}

// Registers the formatter from a Unity runtime-init hook, exactly as a game registers a formatter
// for a type it does not own.
internal static class FakeWrappedFormatterRegistration
{
    [UnityEngine.RuntimeInitializeOnLoadMethod]
    static void Register() => MemoryPackFormatterProvider.Register(new FakeWrappedFormatter());
}
