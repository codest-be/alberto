using Xunit;

namespace EventStore.Tests.Attributes;

/// <summary>
/// Attribute to categorize tests by their purpose and characteristics
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class TestCategoryAttribute : Attribute
{
    public string Category { get; }

    public TestCategoryAttribute(string category)
    {
        Category = category;
    }
}

/// <summary>
/// Common test categories for EventStore tests
/// </summary>
public static class TestCategories
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string Performance = "Performance";
    public const string Concurrency = "Concurrency";
    public const string ErrorHandling = "ErrorHandling";
    public const string MultiSchema = "MultiSchema";
    public const string Scale = "Scale";
    public const string Memory = "Memory";
    public const string Specification = "Specification";
    public const string Advanced = "Advanced";
    public const string Security = "Security";
    public const string Regression = "Regression";
}

/// <summary>
/// Attribute to mark tests that require specific backend implementations
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresBackendAttribute : Attribute
{
    public string[] BackendTypes { get; }

    public RequiresBackendAttribute(params string[] backendTypes)
    {
        BackendTypes = backendTypes;
    }
}

/// <summary>
/// Attribute to mark tests that are slow and should only run in CI or explicit performance test runs
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SlowTestAttribute : Attribute
{
    public string Reason { get; }

    public SlowTestAttribute(string reason = "")
    {
        Reason = reason;
    }
}

/// <summary>
/// Backend type constants for RequiresBackend attribute
/// </summary>
public static class BackendTypes
{
    public const string InMemory = "InMemoryEventStoreBackend";
    public const string Postgres = "PostgresEventStoreBackend";
}