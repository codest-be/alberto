namespace Alberto.Cli;

public static class ConnectionResolver
{
    private const string DefaultConnection = "Host=localhost;Database=postgres";

    /// <summary>
    /// Resolves the connection string using the following priority:
    /// 1. --url flag (urlOption parameter)
    /// 2. ALBERTO_URL environment variable
    /// 3. .alberto/config.json (walked up from cwd)
    /// 4. DATABASE_URL environment variable
    /// 5. Default: "Host=localhost;Database=postgres"
    /// </summary>
    public static string ResolveConnectionString(string? urlOption) =>
        ResolveConnectionString(ConfigFileFinder.Find(), urlOption);

    /// <summary>
    /// The same resolution against a config already in hand, for a caller that has read the file
    /// once and should not read it again for each of a module's shards.
    /// </summary>
    public static string ResolveConnectionString(AlbertoConfig? config, string? urlOption)
    {
        if (!string.IsNullOrWhiteSpace(urlOption))
            return urlOption;

        var albertoUrl = Environment.GetEnvironmentVariable("ALBERTO_URL");
        if (!string.IsNullOrWhiteSpace(albertoUrl))
            return albertoUrl;

        if (!string.IsNullOrWhiteSpace(config?.Connection?.Url))
            return config.Connection.Url;

        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(databaseUrl))
            return databaseUrl;

        return DefaultConnection;
    }

    /// <summary>
    /// Resolves the schema name. Uses schemaOption if provided, otherwise reads from config, otherwise returns "public".
    /// </summary>
    public static string ResolveSchema(string? schemaOption) =>
        ResolveSchema(ConfigFileFinder.Find(), schemaOption);

    /// <summary>The same resolution against a config already in hand.</summary>
    public static string ResolveSchema(AlbertoConfig? config, string? schemaOption)
    {
        if (!string.IsNullOrWhiteSpace(schemaOption))
            return schemaOption;

        if (!string.IsNullOrWhiteSpace(config?.Schema))
            return config.Schema;

        return "public";
    }

    /// <summary>
    /// Resolves the operator name. Uses operatorOption if provided, otherwise reads from config, otherwise returns "cli".
    /// </summary>
    public static string ResolveOperator(string? operatorOption)
    {
        if (!string.IsNullOrWhiteSpace(operatorOption))
            return operatorOption;

        var config = ConfigFileFinder.Find();
        if (!string.IsNullOrWhiteSpace(config?.Operator))
            return config.Operator;

        return "cli";
    }
}
