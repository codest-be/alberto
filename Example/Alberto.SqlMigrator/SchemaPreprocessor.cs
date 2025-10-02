using DbUp.Engine;

namespace Alberto.SqlMigrator;

public class SchemaPreprocessor(string schemaName) : IScriptPreprocessor
{
    public string Process(string contents)
    {
        // Set search_path to include both the target schema and public for DbUp's journal table
        return $"SET search_path TO {schemaName}, public;\n\n{contents}";
    }
}