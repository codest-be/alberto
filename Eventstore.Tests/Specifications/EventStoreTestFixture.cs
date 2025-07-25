namespace Eventstore.Tests.Specifications;

/// <summary>
/// Shared test fixture for event store tests
/// </summary>
public class EventStoreTestFixture : IDisposable
{
    public EventStoreTestFixture()
    {
        // Initialize any shared test infrastructure
        // E.g., ensure test database exists, run migrations, etc.
    }

    public void Dispose()
    {
        // Cleanup shared test infrastructure
    }
}