using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WaydroidPrefsEditor.Core;

public enum ValueKind
{
    Text,
    Boolean,
    Enum,
    Object,
    Array,
    Dictionary,
    Null,
}

/// <summary>What a JSON node is, given the CLR type the schema knows it by (null when unknown).</summary>
public sealed record NodeShape(ValueKind Kind, Type? Type, Type? ElementType, Type? KeyType, Type? ValueType);

/// <summary>
/// Turns a JSON node plus its declared CLR type into the shape a field editor renders, and back:
/// typed text parsing, dictionary key normalization, and defaults for entries the editor adds.
/// </summary>
public static class ValueSchema
{
    /// <summary>
    /// The node wins over the type: a JSON document that does not match its schema still renders
    /// (as inferred nodes), because the tree is built from the document and only annotated by types.
    /// </summary>
    public static NodeShape Describe(JsonNode? node, Type? declaredType)
    {
        var type = declaredType is null ? null : Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (node is null)
            return new NodeShape(ValueKind.Null, type, null, null, null);

        if (type is not null && type.IsEnum && node is JsonValue)
            return new NodeShape(ValueKind.Enum, type, null, null, null);

        if (type is not null && ObjectJson.IsScalar(type) && node is JsonValue)
            return new NodeShape(
                type == typeof(bool) ? ValueKind.Boolean : ValueKind.Text, type, null, null, null);

        if (type is not null && TryDictionaryTypes(type, out var keyType, out var valueType) && node is JsonObject)
            return new NodeShape(ValueKind.Dictionary, type, null, keyType, valueType);

        if (type is not null && IsEnumerable(type) && node is JsonArray)
            return new NodeShape(ValueKind.Array, type, ObjectJson.ElementTypeOf(type), null, null);

        if (type is not null && IsObjectLike(type) && node is JsonObject)
            return new NodeShape(ValueKind.Object, type, null, null, null);

        return node switch
        {
            JsonObject => new NodeShape(ValueKind.Object, null, null, null, null),
            JsonArray => new NodeShape(ValueKind.Array, null, null, null, null),
            JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False =>
                new NodeShape(ValueKind.Boolean, null, null, null, null),
            _ => new NodeShape(ValueKind.Text, null, null, null, null),
        };
    }

    /// <summary>Short type name for the UI: <c>int</c>, <c>FakeNested</c>, <c>List&lt;string&gt;</c>.</summary>
    public static string DisplayName(Type? type)
    {
        if (type is null)
            return "";
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return DisplayName(underlying) + "?";
        if (Keywords.TryGetValue(type, out var keyword))
            return keyword;
        if (type.IsArray)
            return DisplayName(type.GetElementType()) + "[]";
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(DisplayName))}>";
        }
        return type.Name;
    }

    // Type.Name would read "Int32"/"String"; the field labels follow the source the values came from.
    static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
    };

    /// <summary>The node as editable text: strings unquoted, everything else as its JSON token.</summary>
    public static string ScalarText(JsonNode? node) =>
        node is null ? ""
        : node is JsonValue value && value.TryGetValue<string>(out var text) ? text
        : node.ToJsonString();

    /// <summary>Text typed into a field back to the JSON node <see cref="ObjectJson.ApplyJson"/> expects.</summary>
    public static JsonNode ParseScalarText(string text, Type type, string path)
    {
        if (type == typeof(bool))
            return bool.TryParse(text, out var boolean) ? JsonValue.Create(boolean) : Fail(type, text, path);

        if (type.IsEnum)
        {
            if (Enum.GetNames(type).Contains(text, StringComparer.Ordinal))
                return JsonValue.Create(text);
            return TryNumber(text, Enum.GetUnderlyingType(type), out var numeric)
                ? numeric!
                : Fail(type, text, path);
        }

        if (type == typeof(string))
            return JsonValue.Create(text);
        if (type == typeof(char))
            return text.Length == 1 ? JsonValue.Create(text) : Fail(type, text, path);
        if (type == typeof(Half))
            return double.TryParse(text, CultureInfo.InvariantCulture, out var half)
                ? JsonValue.Create((double)(Half)half)
                : Fail(type, text, path);
        if (type == typeof(Int128))
            return Int128.TryParse(text, CultureInfo.InvariantCulture, out var int128)
                ? JsonValue.Create(int128.ToString(CultureInfo.InvariantCulture))
                : Fail(type, text, path);
        if (type == typeof(UInt128))
            return UInt128.TryParse(text, CultureInfo.InvariantCulture, out var uint128)
                ? JsonValue.Create(uint128.ToString(CultureInfo.InvariantCulture))
                : Fail(type, text, path);
        if (type == typeof(DateTime))
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime)
                ? JsonValue.Create(dateTime.ToString("o", CultureInfo.InvariantCulture))
                : Fail(type, text, path);
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset)
                ? JsonValue.Create(offset.ToString("o", CultureInfo.InvariantCulture))
                : Fail(type, text, path);
        if (type == typeof(TimeSpan))
            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)
                ? JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture))
                : Fail(type, text, path);
        if (type == typeof(Guid))
            return Guid.TryParse(text, out var guid)
                ? JsonValue.Create(guid.ToString("D", CultureInfo.InvariantCulture))
                : Fail(type, text, path);

        return TryNumber(text, type, out var number) ? number! : Fail(type, text, path);
    }

    /// <summary>Text typed into a field whose declared type the schema does not know, keyed by the node's own kind.</summary>
    public static JsonNode ParseInferredScalar(string text, JsonValueKind kind, string path) => kind switch
    {
        JsonValueKind.String => JsonValue.Create(text),
        JsonValueKind.Number => long.TryParse(text, CultureInfo.InvariantCulture, out var integer)
            ? JsonValue.Create(integer)
            : double.TryParse(text, CultureInfo.InvariantCulture, out var real)
                ? JsonValue.Create(real)
                : throw new JsonBridgeException(path, $"cannot read a number from '{text}'."),
        JsonValueKind.True or JsonValueKind.False => bool.TryParse(text, out var boolean)
            ? JsonValue.Create(boolean)
            : throw new JsonBridgeException(path, $"cannot read a bool from '{text}'."),
        _ => throw new JsonBridgeException(path, "unexpected JSON node kind."),
    };

    /// <summary>Dictionary key text as it will appear in the JSON object, so a rename is addressable.</summary>
    public static string NormalizeKey(string text, Type keyType, string path)
    {
        if (keyType == typeof(string))
            return text;
        if (keyType.IsEnum)
        {
            if (Enum.GetNames(keyType).Contains(text, StringComparer.Ordinal))
                return text;
            return TryNumber(text, Enum.GetUnderlyingType(keyType), out var numeric)
                ? numeric!.ToJsonString()
                : throw BadKey(text, keyType, path);
        }
        if (keyType == typeof(Guid))
            return Guid.TryParse(text, out var guid)
                ? guid.ToString("D", CultureInfo.InvariantCulture)
                : throw BadKey(text, keyType, path);

        return TryNumber(text, keyType, out var number) ? number!.ToJsonString() : throw BadKey(text, keyType, path);
    }

    /// <summary>Whether the key is an editable text box rather than a label — the types <see cref="NormalizeKey"/> accepts.</summary>
    public static bool CanEditKey(Type keyType) =>
        keyType == typeof(string) || keyType == typeof(Guid) || keyType.IsEnum || IsNumber(keyType);

    /// <summary>
    /// The JSON for a newly added array item or dictionary value, or false when the apply path
    /// could not create one anyway (an abstract type, a collection without an element factory).
    /// </summary>
    public static bool TryDefaultJson(Type? type, out JsonNode? value)
    {
        value = null;
        if (type is null)
            return false;

        if (Nullable.GetUnderlyingType(type) is not null)
            return true; // JSON null is what a nullable member holds by default

        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            if (names.Length > 0)
            {
                value = JsonValue.Create(names[0]);
                return true;
            }
            return TryZero(Enum.GetUnderlyingType(type), out value);
        }

        if (type == typeof(string))
        {
            value = JsonValue.Create("");
            return true;
        }
        if (type == typeof(bool))
        {
            value = JsonValue.Create(false);
            return true;
        }
        if (type == typeof(char))
            return false; // no sane default for a single character
        if (type == typeof(DateTime))
        {
            value = JsonValue.Create(default(DateTime).ToString("o", CultureInfo.InvariantCulture));
            return true;
        }
        if (type == typeof(DateTimeOffset))
        {
            value = JsonValue.Create(default(DateTimeOffset).ToString("o", CultureInfo.InvariantCulture));
            return true;
        }
        if (type == typeof(TimeSpan))
        {
            value = JsonValue.Create("00:00:00");
            return true;
        }
        if (type == typeof(Guid))
        {
            value = JsonValue.Create(Guid.Empty.ToString("D", CultureInfo.InvariantCulture));
            return true;
        }
        if (type == typeof(Int128) || type == typeof(UInt128))
        {
            value = JsonValue.Create("0");
            return true;
        }
        if (TryZero(type, out value))
            return true;

        if (TryDictionaryTypes(type, out _, out _))
        {
            if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                return false;
            value = new JsonObject();
            return true;
        }

        if (type.IsArray)
        {
            value = new JsonArray();
            return true;
        }

        // A concrete collection (List<>, HashSet<>, …) is what ObjectJson builds and fills for a new
        // entry; an interface or one without a constructor still cannot be created.
        if (IsEnumerable(type))
        {
            if (!ObjectJson.CanCreateCollection(type))
                return false;
            value = new JsonArray();
            return true;
        }

        // A type ObjectJson can materialize gets its own default document - every member at its default
        // value - so an added entry arrives editable field by field instead of as an empty object.
        if (ObjectJson.CanCreate(type))
        {
            var document = DefaultDocument(type);
            if (document is not null)
            {
                value = document;
                return true;
            }
        }

        return false;
    }

    /// <summary>The JSON of a fresh instance, or null when the apply path could not make one either.</summary>
    static JsonNode? DefaultDocument(Type type)
    {
        object instance;
        try
        {
            instance = ObjectJson.CreateInstance(type, "$");
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            return ObjectJson.ToJson(instance, type) ?? new JsonObject();
        }
        catch (Exception)
        {
            // A member the reflection bridge cannot carry: an empty document still commits.
            return new JsonObject();
        }
    }

    /// <summary>A free key for an added dictionary entry, or false when the type offers none.</summary>
    public static bool TryNextKey(Type keyType, IReadOnlyCollection<string> existing, out string key)
    {
        if (keyType == typeof(string))
        {
            key = "NewKey";
            for (var i = 2; existing.Contains(key); i++)
                key = "NewKey" + i.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (keyType.IsEnum)
        {
            foreach (var name in Enum.GetNames(keyType))
            {
                if (existing.Contains(name))
                    continue;
                key = name;
                return true;
            }
            key = "";
            return false;
        }

        if (keyType == typeof(bool))
        {
            // ObjectJson writes bool keys as "True"/"False"; a hand-written document may use any case.
            foreach (var candidate in new[] { "True", "False" })
            {
                if (existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    continue;
                key = candidate;
                return true;
            }
            key = "";
            return false;
        }

        if (keyType == typeof(Guid))
        {
            key = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            return true;
        }

        if (IsNumber(keyType))
        {
            for (var i = 0; ; i++)
            {
                var candidate = i.ToString(CultureInfo.InvariantCulture);
                if (existing.Contains(candidate))
                    continue;
                key = candidate;
                return true;
            }
        }

        key = "";
        return false;
    }

    static bool TryDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        foreach (var candidate in type.GetInterfaces().Prepend(type))
        {
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IDictionary<,>))
                continue;
            var arguments = candidate.GetGenericArguments();
            keyType = arguments[0];
            valueType = arguments[1];
            return true;
        }

        keyType = typeof(object);
        valueType = typeof(object);
        return typeof(IDictionary).IsAssignableFrom(type);
    }

    static bool IsEnumerable(Type type) =>
        type != typeof(string) && (type.IsArray || typeof(IEnumerable).IsAssignableFrom(type));

    static bool IsObjectLike(Type type) =>
        (type.IsClass || type.IsValueType) && !type.IsPrimitive && !type.IsEnum
        && !ObjectJson.IsScalar(type) && !TryDictionaryTypes(type, out _, out _) && !IsEnumerable(type);

    static bool IsNumber(Type type) => Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32
        or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single
        or TypeCode.Double or TypeCode.Decimal;

    /// <summary>
    /// A parsed number as a JSON node, typed like <see cref="ObjectJson"/>'s own values: an <c>int</c>
    /// node cannot be read back as a <c>float</c>, so the CLR type decides the node's payload.
    /// </summary>
    static bool TryNumber(string text, Type type, out JsonNode? value)
    {
        value = Type.GetTypeCode(type) switch
        {
            TypeCode.SByte => sbyte.TryParse(text, CultureInfo.InvariantCulture, out var n1) ? JsonValue.Create(n1) : null,
            TypeCode.Byte => byte.TryParse(text, CultureInfo.InvariantCulture, out var n2) ? JsonValue.Create(n2) : null,
            TypeCode.Int16 => short.TryParse(text, CultureInfo.InvariantCulture, out var n3) ? JsonValue.Create(n3) : null,
            TypeCode.UInt16 => ushort.TryParse(text, CultureInfo.InvariantCulture, out var n4) ? JsonValue.Create(n4) : null,
            TypeCode.Int32 => int.TryParse(text, CultureInfo.InvariantCulture, out var n5) ? JsonValue.Create(n5) : null,
            TypeCode.UInt32 => uint.TryParse(text, CultureInfo.InvariantCulture, out var n6) ? JsonValue.Create(n6) : null,
            TypeCode.Int64 => long.TryParse(text, CultureInfo.InvariantCulture, out var n7) ? JsonValue.Create(n7) : null,
            TypeCode.UInt64 => ulong.TryParse(text, CultureInfo.InvariantCulture, out var n8) ? JsonValue.Create(n8) : null,
            TypeCode.Single => float.TryParse(text, CultureInfo.InvariantCulture, out var n9) ? JsonValue.Create(n9) : null,
            TypeCode.Double => double.TryParse(text, CultureInfo.InvariantCulture, out var n10) ? JsonValue.Create(n10) : null,
            TypeCode.Decimal => decimal.TryParse(text, CultureInfo.InvariantCulture, out var n11) ? JsonValue.Create(n11) : null,
            _ => null,
        };
        return value is not null;
    }

    static bool TryZero(Type type, out JsonNode? value)
    {
        if (type == typeof(Half))
        {
            value = JsonValue.Create(0d);
            return true;
        }
        return TryNumber("0", type, out value);
    }

    static JsonNode Fail(Type type, string text, string path) =>
        throw new JsonBridgeException(path, $"cannot read a {type.Name} from '{text}'.");

    static JsonBridgeException BadKey(string text, Type keyType, string path) =>
        new(path, $"'{text}' is not a valid {keyType.Name} dictionary key.");
}
