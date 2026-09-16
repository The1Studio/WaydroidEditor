using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace WaydroidEditor.Core;

public sealed class JsonBridgeException(string path, string message) : Exception($"{path}: {message}");

/// <summary>
/// Bridges a live CLR object graph (usually produced by MemoryPack) to editable JSON and back.
/// <see cref="ApplyJson"/> mutates the existing instance in place so members we cannot set
/// (readonly fields, get-only properties) keep their decoded values and the re-encoded bytes
/// stay identical when nothing was edited.
/// </summary>
public static class ObjectJson
{
    public static JsonNode? ToJson(object? value, Type declaredType) =>
        Write(value, declaredType, new HashSet<object>(ReferenceEqualityComparer.Instance));

    public static IReadOnlyList<string> ApplyJson(object existing, Type declaredType, JsonNode edited)
    {
        var warnings = new List<string>();
        Apply(existing, declaredType, edited, "$", warnings, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return warnings;
    }

    static JsonNode? Write(object? value, Type declaredType, HashSet<object> path)
    {
        var effective = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (value is null)
            return null;

        if (effective.IsEnum)
        {
            var name = Enum.GetName(effective, value);
            if (name is not null)
                return JsonValue.Create(name);
            return Scalar(Convert.ChangeType(value, Enum.GetUnderlyingType(effective), CultureInfo.InvariantCulture)!);
        }

        if (value is string or bool || effective.IsPrimitive || value is decimal)
            return Scalar(value);

        if (value is DateTime dateTime) return JsonValue.Create(dateTime.ToString("o", CultureInfo.InvariantCulture));
        if (value is DateTimeOffset offset) return JsonValue.Create(offset.ToString("o", CultureInfo.InvariantCulture));
        if (value is TimeSpan span) return JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture));
        if (value is Guid guid) return JsonValue.Create(guid.ToString("D", CultureInfo.InvariantCulture));
        if (value is Int128 or UInt128 or Half) return Scalar(value);

        if (value is IDictionary dictionary)
        {
            var obj = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "";
                obj[key] = Write(entry.Value, entry.Value?.GetType() ?? typeof(object), path);
            }
            return obj;
        }

        if (value is IEnumerable enumerable)
        {
            var array = new JsonArray();
            foreach (var item in enumerable)
                array.Add(Write(item, item?.GetType() ?? typeof(object), path));
            return array;
        }

        if (!path.Add(value))
            throw new NotSupportedException($"Cycle detected while serializing {effective.Name}.");

        try
        {
            var obj = new JsonObject();
            foreach (var member in WritableMembers(effective))
                obj[member.Name] = Write(ReadMember(member, value), MemberType(member), path);
            return obj;
        }
        finally
        {
            path.Remove(value);
        }
    }

    static JsonNode? Scalar(object value) => value switch
    {
        string s => JsonValue.Create(s),
        char c => JsonValue.Create(c.ToString()),
        bool b => JsonValue.Create(b),
        byte n => JsonValue.Create(n),
        sbyte n => JsonValue.Create(n),
        short n => JsonValue.Create(n),
        ushort n => JsonValue.Create(n),
        int n => JsonValue.Create(n),
        uint n => JsonValue.Create(n),
        long n => JsonValue.Create(n),
        ulong n => JsonValue.Create(n),
        float n => JsonValue.Create(n),
        double n => JsonValue.Create(n),
        decimal n => JsonValue.Create(n),
        Half n => JsonValue.Create((double)n),
        Int128 n => JsonValue.Create(n.ToString(CultureInfo.InvariantCulture)),
        UInt128 n => JsonValue.Create(n.ToString(CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    static void Apply(
        object target,
        Type declaredType,
        JsonNode node,
        string path,
        List<string> warnings,
        HashSet<object> visited)
    {
        if (node is not JsonObject obj)
            throw new JsonBridgeException(path, $"expected a JSON object for {declaredType.Name}.");

        if (!visited.Add(target))
            throw new JsonBridgeException(path, "cycle detected.");

        try
        {
            foreach (var (name, value) in obj)
            {
                var member = FindMember(declaredType, name);
                if (member is null)
                {
                    warnings.Add($"{path}.{name}: no such member on {declaredType.Name}.");
                    continue;
                }
                if (!CanWrite(member))
                {
                    warnings.Add($"{path}.{name}: member is read-only, left unchanged.");
                    continue;
                }

                var memberType = MemberType(member);
                var current = ReadMember(member, target);
                WriteMember(member, target,
                    FromJson(value, memberType, current, $"{path}.{name}", warnings, visited));
            }
        }
        finally
        {
            visited.Remove(target);
        }
    }

    static object? FromJson(
        JsonNode? node,
        Type declaredType,
        object? current,
        string path,
        List<string> warnings,
        HashSet<object> visited)
    {
        var effective = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (node is null)
        {
            if (Nullable.GetUnderlyingType(declaredType) is null && effective.IsValueType)
                throw new JsonBridgeException(path, $"null is not valid for {effective.Name}.");
            return null;
        }

        if (effective.IsEnum)
            return ParseEnum(node, effective, path);

        if (IsScalar(effective))
            return ParseScalar(node, effective, path);

        if (node is JsonArray array)
            return ConvertCollection(array, effective, current, path, warnings, visited);

        if (node is JsonObject obj)
        {
            if (current is IDictionary || (current is null && typeof(IDictionary).IsAssignableFrom(effective)))
            {
                var dictionary = current ?? CreateInstance(effective, path);
                return ConvertDictionary(obj, dictionary, path, warnings, visited);
            }

            var instance = current ?? CreateInstance(effective, path);
            Apply(instance, effective, obj, path, warnings, visited);
            return instance;
        }

        throw new JsonBridgeException(path, "unexpected JSON node kind.");
    }

    static object ConvertDictionary(
        JsonObject obj, object current, string path, List<string> warnings, HashSet<object> visited)
    {
        var dictionary = (IDictionary)current;
        var keyType = current.GetType().IsGenericType
            ? current.GetType().GetGenericArguments()[0]
            : typeof(string);
        var valueType = current.GetType().IsGenericType
            ? current.GetType().GetGenericArguments()[1]
            : typeof(object);

        var edited = new List<(object Key, JsonNode? Value)>();
        foreach (var (key, value) in obj)
            edited.Add((ParseKey(key, keyType, path), value));

        // Keys the edit dropped go away; the surviving ones keep their decoded instances, so members
        // the JSON cannot carry survive and a value type without a parameterless constructor stays
        // editable. Rebuilding every entry instead is what used to fail on real save types.
        var kept = new HashSet<object>(edited.Select(entry => entry.Key));
        foreach (var key in dictionary.Keys.Cast<object>().Where(key => !kept.Contains(key)).ToList())
            dictionary.Remove(key);

        foreach (var (key, value) in edited)
            dictionary[key] = FromJson(
                value, valueType, dictionary.Contains(key) ? dictionary[key] : null, $"{path}.{key}", warnings, visited);
        return current;
    }

    static object ConvertCollection(
        JsonArray array, Type declaredType, object? current, string path,
        List<string> warnings,
        HashSet<object> visited)
    {
        var elementType = ElementTypeOf(declaredType);

        if (declaredType.IsArray)
        {
            var element = declaredType.GetElementType()!;
            var result = Array.CreateInstance(element, array.Count);
            for (var i = 0; i < array.Count; i++)
                result.SetValue(FromJson(array[i], element, null, $"{path}[{i}]", warnings, visited), i);
            return result;
        }

        // A dictionary entry or member the editor just added has no instance yet: build the concrete
        // collection its elements can go into. Interfaces, abstract types and collections without a
        // parameterless constructor stay as uneditable as ValueSchema.TryDefaultJson says they are.
        if (current is null && CanCreateCollection(declaredType))
            current = CreateInstance(declaredType, path);

        if (current is IList list)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var existing = i < list.Count ? list[i] : null;
                var updated = FromJson(array[i], elementType, existing, $"{path}[{i}]", warnings, visited);
                if (i < list.Count)
                    list[i] = updated;
                else
                    list.Add(updated);
            }
            while (list.Count > array.Count)
                list.RemoveAt(list.Count - 1);
            return current;
        }

        if (current is not null)
        {
            current.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(current, null);
            var add = current.GetType().GetMethod("Add", new[] { elementType });
            if (add is not null)
            {
                for (var i = 0; i < array.Count; i++)
                    add.Invoke(current, new[] { FromJson(array[i], elementType, null, $"{path}[{i}]", warnings, visited) });
                return current;
            }
        }

        throw new JsonBridgeException(path, $"cannot edit a collection of type {declaredType.Name}.");
    }

    /// <summary>
    /// An empty instance for a member or dictionary entry that has none yet. The type's own
    /// parameterless constructor is used even when it is not public — MemoryPack's generated save
    /// types keep theirs non-public and the formatter generated inside the type still calls it — and
    /// a type with no parameterless constructor at all still gets a zeroed instance, so an entry can
    /// be added for it and edited field by field.
    /// </summary>
    public static object CreateInstance(Type type, string path)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type)!;

        if (CanCreate(type))
        {
            return ParameterlessConstructor(type) is { } constructor
                ? constructor.Invoke(null)
                : RuntimeHelpers.GetUninitializedObject(type);
        }

        throw new JsonBridgeException(path, $"cannot construct {type.Name}.");
    }

    /// <summary>Whether <see cref="CreateInstance"/> can materialize the type at all.</summary>
    public static bool CanCreate(Type type) =>
        type.IsValueType || (type.IsClass && !type.IsAbstract && type != typeof(string));

    /// <summary>
    /// Whether an empty collection instance can be built for <see cref="ConvertCollection"/> to fill:
    /// an uninitialized collection is not usable, so this needs a real constructor.
    /// </summary>
    public static bool CanCreateCollection(Type type) =>
        !type.IsAbstract && !type.IsInterface && ParameterlessConstructor(type) is not null;

    static ConstructorInfo? ParameterlessConstructor(Type type) => type.GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

    static object ParseEnum(JsonNode node, Type enumType, string path)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var name))
        {
            try
            {
                return Enum.Parse(enumType, name, ignoreCase: false);
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                throw new JsonBridgeException(path, $"'{name}' is not a member of {enumType.Name}.");
            }
        }
        var underlying = Enum.GetUnderlyingType(enumType);
        return Enum.ToObject(enumType, ParseScalar(node, underlying, path));
    }

    static object ParseKey(string key, Type keyType, string path)
    {
        if (keyType == typeof(string))
            return key;
        if (keyType.IsEnum)
            return ParseEnum(JsonValue.Create(key)!, keyType, path);
        if (keyType == typeof(Guid))
            return Guid.Parse(key);

        try
        {
            return Type.GetTypeCode(keyType) switch
            {
                TypeCode.Boolean => bool.Parse(key),
                TypeCode.Char => key[0],
                TypeCode.SByte => sbyte.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Byte => byte.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Int16 => short.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.UInt16 => ushort.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Int32 => int.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.UInt32 => uint.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Int64 => long.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.UInt64 => ulong.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Single => float.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Double => double.Parse(key, CultureInfo.InvariantCulture),
                TypeCode.Decimal => decimal.Parse(key, CultureInfo.InvariantCulture),
                _ => throw new JsonBridgeException(path, $"unsupported dictionary key type {keyType.Name}."),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
        {
            throw new JsonBridgeException(path, $"'{key}' is not a valid {keyType.Name} dictionary key.");
        }
    }

    static object ParseScalar(JsonNode node, Type type, string path)
    {
        try
        {
            if (type == typeof(string)) return node.GetValue<string>();
            if (type == typeof(bool)) return node.GetValue<bool>();
            if (type == typeof(char)) return node.GetValue<string>()[0];
            if (type == typeof(byte)) return node.GetValue<byte>();
            if (type == typeof(sbyte)) return node.GetValue<sbyte>();
            if (type == typeof(short)) return node.GetValue<short>();
            if (type == typeof(ushort)) return node.GetValue<ushort>();
            if (type == typeof(int)) return node.GetValue<int>();
            if (type == typeof(uint)) return node.GetValue<uint>();
            if (type == typeof(long)) return node.GetValue<long>();
            if (type == typeof(ulong)) return node.GetValue<ulong>();
            if (type == typeof(float)) return node.GetValue<float>();
            if (type == typeof(double)) return node.GetValue<double>();
            if (type == typeof(decimal)) return node.GetValue<decimal>();
            if (type == typeof(Half)) return (Half)node.GetValue<double>();
            if (type == typeof(Int128)) return Int128.Parse(RawText(node), CultureInfo.InvariantCulture);
            if (type == typeof(UInt128)) return UInt128.Parse(RawText(node), CultureInfo.InvariantCulture);
            if (type == typeof(DateTime))
                return DateTime.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(TimeSpan))
                return TimeSpan.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture);
            if (type == typeof(Guid)) return Guid.Parse(node.GetValue<string>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException
                                       or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            throw new JsonBridgeException(path, $"cannot read a {type.Name} from {RawText(node)} ({ex.Message}).");
        }

        throw new JsonBridgeException(path, $"{type.Name} is not a supported scalar.");
    }

    static string RawText(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();

    public static bool IsScalar(Type type) =>
        type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
        || type == typeof(Guid) || type == typeof(Int128) || type == typeof(UInt128) || type == typeof(Half);

    /// <summary>Public instance fields plus public get/set properties, minus ignored members, plus included privates.</summary>
    static IEnumerable<MemberInfo> WritableMembers(Type type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (HasIgnore(property))
                continue;
            if (property.GetMethod is null || property.SetMethod is null)
                continue;
            if (seen.Add(property.Name))
                yield return property;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (HasIgnore(field))
                continue;
            if (seen.Add(field.Name))
                yield return field;
        }

        foreach (var member in type.GetMembers(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (member is not (FieldInfo or PropertyInfo))
                continue;
            if (HasIgnore(member) || !HasInclude(member))
                continue;
            if (seen.Add(member.Name))
                yield return member;
        }
    }

    public static MemberInfo? FindMember(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is not null && IsVisible(property))
            return property;
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null && IsVisible(field))
            return field;
        return null;
    }

    static bool IsVisible(MemberInfo member) => member switch
    {
        PropertyInfo p => !HasIgnore(p) && !IsPrivate(p) && p.GetIndexParameters().Length == 0,
        FieldInfo f => !HasIgnore(f) && !IsPrivate(f),
        _ => false,
    };

    static bool IsPrivate(MemberInfo member) => member switch
    {
        PropertyInfo p => p.GetMethod is null || p.SetMethod is null
            ? !HasInclude(p)
            : (p.GetMethod.IsPrivate || p.SetMethod.IsPrivate) && !HasInclude(p),
        FieldInfo f => f.IsPrivate && !HasInclude(f),
        _ => true,
    };

    public static bool CanWrite(MemberInfo member) => member switch
    {
        PropertyInfo p => p.SetMethod is not null,
        FieldInfo f => !f.IsInitOnly,
        _ => false,
    };

    static bool HasIgnore(MemberInfo member) => MpMemberAttributes.IsIgnored(member);

    static bool HasInclude(MemberInfo member) => MpMemberAttributes.IsIncluded(member);

    public static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => typeof(object),
    };

    static object? ReadMember(MemberInfo member, object instance) => member switch
    {
        PropertyInfo p => p.GetValue(instance),
        FieldInfo f => f.GetValue(instance),
        _ => null,
    };

    static void WriteMember(MemberInfo member, object instance, object? value)
    {
        switch (member)
        {
            case PropertyInfo p:
                p.SetValue(instance, value);
                break;
            case FieldInfo f:
                f.SetValue(instance, value);
                break;
        }
    }

    public static Type ElementTypeOf(Type type)
    {
        if (type.IsArray)
            return type.GetElementType()!;
        if (type.IsGenericType)
        {
            foreach (var candidate in type.GetInterfaces().Prepend(type))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return candidate.GetGenericArguments()[0];
            }
        }
        return typeof(object);
    }
}
