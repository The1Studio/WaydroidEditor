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
    public FakeWrapped()
    {
    }

    public FakeWrapped(int value) => Value = value;

    public int Value { get; set; }
}

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
    [MemoryPackOrder(0)] [MemoryPackAllowSerialize] public FakeWrapped Wrapped { get; set; } = new();
}

/// <summary>
/// Registers the formatter from a Unity runtime-init hook, exactly as a game registers a formatter for
/// a type it does not own.
/// </summary>
internal static class FakeWrappedFormatterRegistration
{
    [UnityEngine.RuntimeInitializeOnLoadMethod]
    static void Register() => MemoryPackFormatterProvider.Register(new FakeWrappedFormatter());
}
