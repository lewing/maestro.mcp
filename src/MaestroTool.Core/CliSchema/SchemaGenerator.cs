using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaestroTool.Core;

public static class SchemaGenerator
{
    private const int MaxDepth = 5;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string GenerateSchema<T>() => GenerateSchema(typeof(T));

    public static string GenerateSchema(Type type)
    {
        var schema = BuildSchemaNode(type, depth: 0, new HashSet<Type>());
        return JsonSerializer.Serialize(schema, s_jsonOptions);
    }

    private static JsonNode? BuildSchemaNode(Type type, int depth, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (depth >= MaxDepth)
        {
            return JsonValue.Create("<circular>");
        }

        if (TryCreateScalarNode(type, out var scalarNode))
        {
            return scalarNode;
        }

        if (visited.Contains(type))
        {
            return JsonValue.Create("<circular>");
        }

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            return new JsonObject
            {
                ["<key>"] = BuildSchemaNode(valueType, depth + 1, visited)
            };
        }

        if (TryGetEnumerableElementType(type, out var elementType))
        {
            return new JsonArray(BuildSchemaNode(elementType, depth + 1, visited));
        }

        visited.Add(type);
        try
        {
            var obj = new JsonObject();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken);

            foreach (var property in properties)
            {
                obj[property.Name] = BuildSchemaNode(property.PropertyType, depth + 1, visited);
            }

            return obj;
        }
        finally
        {
            visited.Remove(type);
        }
    }

    private static bool TryCreateScalarNode(Type type, out JsonNode? node)
    {
        if (type.IsEnum)
        {
            node = JsonValue.Create($"<{string.Join("|", Enum.GetNames(type))}>");
            return true;
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(Uri))
        {
            node = JsonValue.Create("<string>");
            return true;
        }

        if (type == typeof(bool))
        {
            node = JsonValue.Create(false);
            return true;
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly))
        {
            node = JsonValue.Create("<datetime>");
            return true;
        }

        if (type == typeof(Guid))
        {
            node = JsonValue.Create("<guid>");
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            node = JsonValue.Create("<timespan>");
            return true;
        }

        if (type == typeof(object))
        {
            node = JsonValue.Create("<object>");
            return true;
        }

        if (IsNumericType(type))
        {
            node = JsonValue.Create(0);
            return true;
        }

        node = null;
        return false;
    }

    private static bool IsNumericType(Type type)
    {
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or
            TypeCode.SByte or
            TypeCode.UInt16 or
            TypeCode.UInt32 or
            TypeCode.UInt64 or
            TypeCode.Int16 or
            TypeCode.Int32 or
            TypeCode.Int64 or
            TypeCode.Decimal or
            TypeCode.Double or
            TypeCode.Single => true,
            _ => false
        };
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var dictionaryType = type == typeof(string)
            ? null
            : type.GetInterfaces()
                .Append(type)
                .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() is var definition &&
                    (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryType != null)
        {
            valueType = dictionaryType.GetGenericArguments()[1];
            return true;
        }

        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            valueType = typeof(object);
            return true;
        }

        valueType = null!;
        return false;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = null!;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerableType = type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableType != null)
        {
            elementType = enumerableType.GetGenericArguments()[0];
            return true;
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = typeof(object);
            return true;
        }

        elementType = null!;
        return false;
    }
}
