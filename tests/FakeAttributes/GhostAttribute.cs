namespace FakeAttributes;

/// <summary>
/// Stands in for an attribute assembly a game references but does not ship next to its save types
/// (Newtonsoft.Json, UnityEngine.*Module, Odin…). FakeGameData compiles against this assembly and is
/// loaded without it, so nothing at runtime can instantiate the attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class GhostAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
