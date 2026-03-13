using System.Reflection;
using System.Text.Json;
using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class SchemaGeneratorTests
{
    [Fact]
    public void GenerateSchema_WithPrimitiveProperties_UsesExpectedPlaceholders()
    {
        using var document = GenerateSchemaDocument<PrimitiveContainer>();
        var root = document.RootElement;

        Assert.Equal("<string>", root.GetProperty(nameof(PrimitiveContainer.Name)).GetString());
        Assert.Equal(0, root.GetProperty(nameof(PrimitiveContainer.Count)).GetInt32());
        Assert.False(root.GetProperty(nameof(PrimitiveContainer.Enabled)).GetBoolean());
    }

    [Fact]
    public void GenerateSchema_WithDateTimeAndGuid_UsesExpectedPlaceholders()
    {
        using var document = GenerateSchemaDocument<TemporalIdentityContainer>();
        var root = document.RootElement;

        Assert.Equal("<datetime>", root.GetProperty(nameof(TemporalIdentityContainer.CreatedAt)).GetString());
        Assert.Equal("<datetime>", root.GetProperty(nameof(TemporalIdentityContainer.UpdatedAt)).GetString());
        Assert.Equal("<guid>", root.GetProperty(nameof(TemporalIdentityContainer.Id)).GetString());
    }

    [Fact]
    public void GenerateSchema_WithEnumProperty_UsesPipeSeparatedEnumNames()
    {
        using var document = GenerateSchemaDocument<EnumContainer>();
        var root = document.RootElement;

        Assert.Equal("<Draft|Published>", root.GetProperty(nameof(EnumContainer.State)).GetString());
    }

    [Fact]
    public void GenerateSchema_WithNullableProperties_UsesUnderlyingTypePlaceholders()
    {
        using var document = GenerateSchemaDocument<NullableContainer>();
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty(nameof(NullableContainer.RetryCount)).GetInt32());
        Assert.Equal("<string>", root.GetProperty(nameof(NullableContainer.Notes)).GetString());
    }

    [Fact]
    public void GenerateSchema_WithCollectionProperties_WrapsSingleElementSkeletonsInArrays()
    {
        using var document = GenerateSchemaDocument<CollectionContainer>();
        var root = document.RootElement;

        var tags = root.GetProperty(nameof(CollectionContainer.Tags));
        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Equal("<string>", Assert.Single(tags.EnumerateArray()).GetString());

        var items = root.GetProperty(nameof(CollectionContainer.Items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal("<string>", item.GetProperty(nameof(NestedItem.Name)).GetString());
    }

    [Fact]
    public void GenerateSchema_WithNestedObject_RecursesIntoChildProperties()
    {
        using var document = GenerateSchemaDocument<NestedContainer>();
        var root = document.RootElement;

        var child = root.GetProperty(nameof(NestedContainer.Child));
        Assert.Equal(JsonValueKind.Object, child.ValueKind);
        Assert.Equal("<string>", child.GetProperty(nameof(NestedItem.Name)).GetString());
    }

    [Fact]
    public void GenerateSchema_WithSelfReferencingType_EmitsCircularPlaceholder()
    {
        using var document = GenerateSchemaDocument<SelfReferencingNode>();

        Assert.True(ContainsStringValue(document.RootElement, "<circular>"));
    }

    [Fact]
    public void GenerateSchema_UsesPascalCasePropertyNames()
    {
        using var document = GenerateSchemaDocument<PascalCaseContainer>();
        var root = document.RootElement;

        Assert.Equal("<string>", root.GetProperty(nameof(PascalCaseContainer.SourceRepository)).GetString());
        Assert.False(root.TryGetProperty("sourceRepository", out _));
    }

    [Fact]
    public void GenerateSchema_ForBuildFreshnessResult_ProducesExpectedSkeleton()
    {
        using var document = GenerateSchemaDocument<BuildFreshnessResult>();
        var root = document.RootElement;

        Assert.Equal("<string>", root.GetProperty(nameof(BuildFreshnessResult.Channel)).GetString());
        Assert.Equal("<datetime>", root.GetProperty(nameof(BuildFreshnessResult.LastModified)).GetString());
        Assert.False(root.GetProperty(nameof(BuildFreshnessResult.IsAvailable)).GetBoolean());
    }

    [Fact]
    public void GenerateSchema_ForSubscriptionHealthResult_ProducesNestedStructure()
    {
        using var document = GenerateSchemaDocument<SubscriptionHealthResult>();
        var root = document.RootElement;

        Assert.Equal("<guid>", root.GetProperty(nameof(SubscriptionHealthResult.SubscriptionId)).GetString());
        Assert.Equal("<datetime>", root.GetProperty(nameof(SubscriptionHealthResult.LastAppliedDate)).GetString());

        var recentCommits = root.GetProperty(nameof(SubscriptionHealthResult.RecentCommits));
        var commit = Assert.Single(recentCommits.EnumerateArray());
        Assert.Equal("<string>", commit.GetProperty(nameof(CommitInfo.Sha)).GetString());
        Assert.Equal("<datetime>", commit.GetProperty(nameof(CommitInfo.Date)).GetString());

        var validation = root.GetProperty(nameof(SubscriptionHealthResult.Validation));
        Assert.Equal(JsonValueKind.Object, validation.ValueKind);
        Assert.Equal(0, validation.GetProperty(nameof(ValidationResult.MergedPrsSinceLastApplied)).GetInt32());
        Assert.Equal("<string>", Assert.Single(validation.GetProperty(nameof(ValidationResult.MergedPrUrls)).EnumerateArray()).GetString());

        var trackedPr = root.GetProperty(nameof(SubscriptionHealthResult.TrackedPr));
        Assert.Equal("<Missing|MergedButNotCleared|ClosedButNotCleared|BlockedByCI|Active|Unknown>", trackedPr.GetProperty(nameof(TrackedPrDiagnosis.State)).GetString());
    }

    [Fact]
    public void GenerateSchema_ForListType_UsesArrayAsRoot()
    {
        using var document = GenerateSchemaDocument<List<ListRootItem>>();
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        var item = Assert.Single(root.EnumerateArray());
        Assert.Equal("<string>", item.GetProperty(nameof(ListRootItem.Value)).GetString());
        Assert.Equal("<string>", item.GetProperty(nameof(ListRootItem.Details)).GetProperty(nameof(NestedItem.Name)).GetString());
    }

    [Fact]
    public void GenerateSchema_GenericAndTypeOverloads_ReturnEquivalentJson()
    {
        var genericJson = GenerateSchema<PrimitiveContainer>();
        var typeJson = GenerateSchema(typeof(PrimitiveContainer));

        Assert.Equal(NormalizeJson(typeJson), NormalizeJson(genericJson));
    }

    private static JsonDocument GenerateSchemaDocument<T>() => JsonDocument.Parse(GenerateSchema<T>());

    private static string GenerateSchema<T>() =>
        (string)GenericGenerateSchemaMethod.MakeGenericMethod(typeof(T)).Invoke(null, null)!;

    private static JsonDocument GenerateSchemaDocument(Type type) => JsonDocument.Parse(GenerateSchema(type));

    private static string GenerateSchema(Type type) =>
        (string)TypeGenerateSchemaMethod.Invoke(null, new object[] { type })!;

    private static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static bool ContainsStringValue(JsonElement element, string expectedValue)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() == expectedValue,
            JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsStringValue(property.Value, expectedValue)),
            JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsStringValue(item, expectedValue)),
            _ => false
        };
    }

    private static Type SchemaGeneratorType => typeof(MaestroService).Assembly.GetTypes().Single(type => type.Name == "SchemaGenerator");

    private static MethodInfo TypeGenerateSchemaMethod => SchemaGeneratorType
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method =>
            method.Name == "GenerateSchema" &&
            !method.IsGenericMethodDefinition &&
            method.GetParameters() is [{ ParameterType: var parameterType }] &&
            parameterType == typeof(Type));

    private static MethodInfo GenericGenerateSchemaMethod => SchemaGeneratorType
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method =>
            method.Name == "GenerateSchema" &&
            method.IsGenericMethodDefinition &&
            method.GetGenericArguments().Length == 1 &&
            method.GetParameters().Length == 0);

    private sealed class PrimitiveContainer
    {
        public string Name { get; init; } = string.Empty;
        public int Count { get; init; }
        public bool Enabled { get; init; }
    }

    private sealed class TemporalIdentityContainer
    {
        public DateTime CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public Guid Id { get; init; }
    }

    private enum PublishingState
    {
        Draft,
        Published
    }

    private sealed class EnumContainer
    {
        public PublishingState State { get; init; }
    }

    private sealed class NullableContainer
    {
        public int? RetryCount { get; init; }
        public string? Notes { get; init; }
    }

    private sealed class CollectionContainer
    {
        public List<string> Tags { get; init; } = [];
        public IReadOnlyList<NestedItem> Items { get; init; } = [];
    }

    private sealed class NestedContainer
    {
        public NestedItem Child { get; init; } = new();
    }

    private sealed class NestedItem
    {
        public string Name { get; init; } = string.Empty;
    }

    private sealed class SelfReferencingNode
    {
        public SelfReferencingNode? Child { get; init; }
    }

    private sealed class PascalCaseContainer
    {
        public string SourceRepository { get; init; } = string.Empty;
    }

    private sealed class ListRootItem
    {
        public string Value { get; init; } = string.Empty;
        public NestedItem Details { get; init; } = new();
    }
}
