# EventStore Tests

This directory contains comprehensive tests for the EventStore library, ensuring robust functionality across all backend implementations.

## Test Structure

### Specification-Based Testing
The test suite follows a **specification pattern** where abstract base classes define contracts that all backend implementations must follow:

- `Specifications/EventStoreBackendSpecification.cs` - Core specification with 40+ test scenarios
- `Specifications/AdvancedQueryTests.cs` - Advanced querying scenarios and edge cases

### Backend-Specific Tests

#### InMemory Backend
- `InMemory/InMemoryEventStoreBackendSpecificationTests.cs` - Tests for in-memory implementation

#### PostgreSQL Backend
- `Postgres/PostgresEventStoreBackendSpecificationTests.cs` - PostgreSQL implementation tests
- `Postgres/MultiSchemaEventStoreTests.cs` - Multi-schema support and isolation
- `Postgres/EventStoreErrorHandlingTests.cs` - Error handling and edge cases
- `Postgres/PostgresConfigurationTests.cs` - Configuration validation

### Test Infrastructure
- `Specifications/EventStoreTestFixture.cs` - Shared utilities and test data generators
- `Attributes/TestCategoryAttribute.cs` - Test categorization for organization

## Test Categories

Tests are organized into the following categories:

### Core Functionality
- **Unit Tests**: Fast, isolated tests of individual components
- **Integration Tests**: Tests using real databases with Testcontainers
- **Specification Tests**: Contract compliance tests for all backends

### Advanced Scenarios
- **Performance Tests**: Large-scale operations (1K-5K events)
- **Concurrency Tests**: Parallel operations and thread safety
- **Scale Tests**: Multi-tenant scenarios (100+ tenants)
- **Memory Tests**: Large payload handling (InMemory backend specific)

### Edge Cases & Error Handling
- **Error Handling Tests**: Invalid inputs, connection failures, timeouts
- **Security Tests**: Tenant isolation, SQL injection protection
- **Configuration Tests**: Invalid configurations, edge case settings

### Multi-Schema Support
- **Schema Isolation**: Events isolated between different schemas
- **Cross-Schema Operations**: Ensuring proper boundaries
- **Configuration**: Multiple backend instances with different schemas

## Running Tests

### All Tests
```bash
dotnet test
```

### Specific Test Project
```bash
dotnet test EventStore.Tests/EventStore.Tests.csproj
```

### By Category (when filtering is implemented)
```bash
# Performance tests only
dotnet test --filter "Category=Performance"

# Integration tests only
dotnet test --filter "Category=Integration"

# PostgreSQL-specific tests
dotnet test --filter "FullyQualifiedName~Postgres"
```

### Individual Test Classes
```bash
# Multi-schema tests
dotnet test --filter "ClassName=MultiSchemaEventStoreTests"

# Error handling tests
dotnet test --filter "ClassName=EventStoreErrorHandlingTests"
```

## Test Data and Fixtures

### EventStoreTestFixture Utilities
The test fixture provides several utilities for creating test data:

- `CreateTestEvent()` - Basic event creation
- `CreateTestEventWithData()` - Events with custom JSON data
- `CreateEventBatch()` - Bulk event creation for performance tests
- `CreateECommerceEvents()` - Realistic domain events
- `GenerateTenantIds()` - Multi-tenant test data
- `ValidateEventOrdering()` - Event sequence validation
- `ValidateTenantIsolation()` - Multi-tenant verification

### PostgreSQL Test Infrastructure
- **Testcontainers**: Real PostgreSQL instances for integration tests
- **Schema Management**: Automatic schema creation and cleanup
- **Tenant Isolation**: Unique tenant IDs prevent test interference
- **Connection Pooling**: Shared container across test classes

## Performance Expectations

### Baseline Performance (PostgreSQL with optimized indexes)
- **Small Scale (1K events)**: Tag queries < 3ms
- **Medium Scale (10K events)**: Tag queries < 15ms
- **Large Scale (100K events)**: Tag queries < 10ms
- **Bulk Append (1K events)**: < 30 seconds
- **Complex Queries**: < 5 seconds

### Memory Usage (InMemory backend)
- **Large Payloads**: 50 events × 100KB each (5MB total)
- **Event Count**: Up to 5K events in single tests
- **Concurrent Operations**: 10 parallel clients × 20 events

## Test Configuration

### PostgreSQL Container Settings
- **Image**: `postgres:17-alpine`
- **Database**: `eventstore_test`
- **Schema**: `app` (default), with additional schemas for multi-schema tests
- **Cleanup**: Automatic container cleanup after tests

### Timeouts and Limits
- **Query Timeout**: 5 seconds for complex queries
- **Bulk Operations**: 30 seconds for large appends
- **Connection Timeout**: Standard PostgreSQL defaults
- **Test Isolation**: Each test gets unique tenant ID

## Adding New Tests

### For Core Functionality
1. Add tests to `EventStoreBackendSpecification.cs`
2. Ensure tests work with both InMemory and PostgreSQL backends
3. Use the abstract methods (`CreateBackend()`, `CurrentTenant()`)

### For Backend-Specific Features
1. Create tests in the appropriate backend folder
2. Use `[Collection("Postgres Integration Tests")]` for PostgreSQL tests
3. Extend from appropriate test fixtures

### For Advanced Scenarios
1. Add to `AdvancedQueryTests.cs` if query-related
2. Create new test classes for domain-specific scenarios
3. Use test categories for organization

### Test Categories to Use
```csharp
[TestCategory(TestCategories.Performance)]
[TestCategory(TestCategories.Integration)]
[SlowTest("Large dataset processing")]
[RequiresBackend(BackendTypes.Postgres)]
```

## Known Issues and Limitations

1. **Slow Tests**: Performance tests with 5K+ events may take several seconds
2. **Container Startup**: First PostgreSQL test may be slower due to container initialization
3. **Parallel Execution**: Some tests may conflict if not properly isolated
4. **Resource Usage**: Large-scale tests consume significant memory/CPU

## Contributing

When adding new tests:

1. **Follow Naming Conventions**: `Should_ExpectedBehavior_When_StateUnderTest`
2. **Use Test Categories**: Properly categorize tests for filtering
3. **Document Performance Expectations**: Add comments for slow tests
4. **Ensure Cleanup**: Always clean up test data in disposal methods
5. **Test Both Backends**: Ensure specifications work across implementations