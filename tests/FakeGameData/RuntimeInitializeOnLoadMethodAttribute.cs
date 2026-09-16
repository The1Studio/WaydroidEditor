namespace UnityEngine;

/// <summary>
/// Stands in for Unity's runtime-init hook. A game registers a custom MemoryPack formatter for a type
/// it does not own from a method carrying this attribute; outside the player the editor has to replay
/// it. Defined here rather than in FakeAttributes so it resolves at runtime, the way the real
/// UnityEngine module a game ships does.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
{
}
