# Alberto.EventStore.Postgres

PostgreSQL implementation of Alberto EventStore for production use.

## Installation

```bash
dotnet add package Alberto.EventStore.Postgres
```

## Requirements

- PostgreSQL 15+ (PostgreSQL 16+ recommended for optimal performance)

## Overview

This package provides a production-ready PostgreSQL backend for Alberto EventStore with:

- High-performance event persistence
- JSONB-based event storage
- Multi-tenant and multi-schema support
- Automatic schema migrations
- Optimized indexing for fast queries

## Quick Start

```csharp
using Alberto.EventStore;
using Alberto.EventStore.Postgres;

// Configure services
services.AddEventStore()
        .AddPostgresEventStore(options =>
        {
            options.ConnectionString = "Host=localhost;Database=mydb;Username=user;Password=pass";
            options.Schema = "events"; // Optional: defaults to "public"
            options.RunMigrations = true; // Auto-migrate on startup (development)
        });
```

## Configuration

### Connection String

Provide a standard PostgreSQL connection string:

```csharp
options.ConnectionString = "Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass";
```

### Schema Management

Specify a custom schema for isolation:

```csharp
options.Schema = "myapp_events"; // Defaults to "public"
```

### Migrations

For development, enable automatic migrations:

```csharp
options.RunMigrations = true; // Runs migrations at startup
```

For production, run migrations manually during deployment using migration tools or scripts.

## Features

- **High Performance** - Optimized for PostgreSQL's strengths
- **JSONB Storage** - Flexible event data storage
- **Multi-Tenant** - Isolated event streams per tenant
- **Schema Isolation** - Multiple schemas for different contexts
- **Auto-Migrations** - Optional automatic schema setup

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
