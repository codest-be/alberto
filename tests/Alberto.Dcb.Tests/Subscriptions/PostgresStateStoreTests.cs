// All IStateStore<TState> contract facts that were here (including the mislabelled
// "TenantIsolation_*" tests that actually verified projectionType isolation) have been
// absorbed into StateStoreSpecification<TState> / PostgresStateStoreSpecificationTests.
// See StateStoreSpecification.cs.
//
// Note: The "TenantIsolation_*" methods passed their string argument as the
// projectionType constructor parameter (position 2), not as tenantId (position 5).
// They are now correctly covered by ProjectionType_Isolates_SameDocId.
// The real multi-tenant mode (tenantId constructor parameter) is covered by
// MultiTenantPostgresStateStoreSpecificationTests in StateStoreSpecification.cs.
