using MemoryPack;

namespace TheOne.Extensions;

/// <summary>
/// Stands in for the game's own key attribute so the runtime's by-name matching is exercised.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class KeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
