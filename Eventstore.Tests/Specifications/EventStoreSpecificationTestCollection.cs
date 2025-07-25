using Xunit;

namespace Eventstore.Tests.Specifications;

/// <summary>
/// Example of how to create a test collection for running specification tests
/// </summary>
[CollectionDefinition("EventStore Specification Tests")]
public class EventStoreSpecificationTestCollection : ICollectionFixture<EventStoreTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}