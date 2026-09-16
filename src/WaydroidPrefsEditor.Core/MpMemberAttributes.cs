using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WaydroidPrefsEditor.Core;

/// <summary>
/// Answers "does this member carry [MemoryPackInclude]/[MemoryPackIgnore]?" out of the declaring
/// assembly's own metadata. Instantiating the attributes instead loads their assemblies —
/// Newtonsoft.Json, Odin, the UnityEngine modules a game references but whose DLLs may not be in the
/// folder the user imported — and for those games reflection reports no attributes at all, hiding
/// every private member that <c>[MemoryPackInclude]</c> is what makes part of the save. Metadata has
/// no such dependency, and it is the same answer, because the attribute is read, not run.
/// </summary>
static class MpMemberAttributes
{
    const string IncludeName = "MemoryPack.MemoryPackIncludeAttribute";
    const string IgnoreName = "MemoryPack.MemoryPackIgnoreAttribute";

    /// <summary>Per assembly, parsed on first use; a null entry means "no readable file" — a game
    /// assembly always has one, the embedded UnityEngine is the case that does not.</summary>
    static readonly Dictionary<Assembly, Flags?> Cache = [];

    public static bool IsIgnored(MemberInfo member) => Has(member, ignore: true);

    public static bool IsIncluded(MemberInfo member) => Has(member, ignore: false);

    static bool Has(MemberInfo member, bool ignore)
    {
        if (member.DeclaringType is not { FullName: { } typeName } type)
            return false;

        if (!Cache.TryGetValue(type.Assembly, out var flags))
            Cache[type.Assembly] = flags = Read(type.Assembly);

        return flags is not null
            && (ignore ? flags.Ignored : flags.Included).Contains((typeName, member.Name));
    }

    static Flags? Read(Assembly assembly)
    {
        if (PathOf(assembly) is not { } path)
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;

            var reader = pe.GetMetadataReader();
            var flags = new Flags([], []);
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var typeName = FullName(reader, handle);
                foreach (var property in type.GetProperties())
                {
                    var member = reader.GetPropertyDefinition(property);
                    Add(reader, flags, typeName, reader.GetString(member.Name), member.GetCustomAttributes());
                }
                foreach (var field in type.GetFields())
                {
                    var member = reader.GetFieldDefinition(field);
                    Add(reader, flags, typeName, reader.GetString(member.Name), member.GetCustomAttributes());
                }
            }
            return flags;
        }
        catch (Exception)
        {
            // A truncated or unreadable file costs fidelity, never a row: the caller falls back to
            // public members only, which is what every game without these attributes gets anyway.
            return null;
        }
    }

    /// <summary>
    /// The file backing <paramref name="assembly"/>, or null when there is none. Only the editor's own
    /// assemblies are bundled into one file and answer with an empty location; a game's are always
    /// loaded from the folder the user picked, which is exactly what this has to read.
    /// </summary>
    static string? PathOf(Assembly assembly)
    {
#pragma warning disable IL3000
        var path = assembly.Location;
#pragma warning restore IL3000
        return path.Length > 0 && File.Exists(path) ? path : null;
    }

    static void Add(
        MetadataReader reader,
        Flags flags,
        string typeName,
        string memberName,
        CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            switch (AttributeName(reader, handle))
            {
                case IncludeName:
                    flags.Included.Add((typeName, memberName));
                    break;
                case IgnoreName:
                    flags.Ignored.Add((typeName, memberName));
                    break;
            }
        }
    }

    /// <summary>The declaring type's full name, nested spelling (<c>Outer+Inner</c>) included, so it
    /// matches <see cref="Type.FullName"/>.</summary>
    static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        if (!type.GetDeclaringType().IsNil)
            return FullName(reader, type.GetDeclaringType()) + "+" + name;
        return reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + name : name;
    }

    static string? AttributeName(MetadataReader reader, CustomAttributeHandle handle) =>
        ConstructorOwner(reader, reader.GetCustomAttribute(handle).Constructor);

    static string? ConstructorOwner(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => ReferenceName(reader, (TypeReferenceHandle)handle),
        HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)handle),
        HandleKind.MemberReference => ConstructorOwner(reader, reader.GetMemberReference((MemberReferenceHandle)handle).Parent),
        HandleKind.MethodDefinition => FullName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()),
        _ => null,
    };

    static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        return reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + name : name;
    }

    sealed record Flags(HashSet<(string Type, string Member)> Included, HashSet<(string Type, string Member)> Ignored);
}
