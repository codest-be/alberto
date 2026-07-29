namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Identity attributed to mutations that arrive over MCP without an explicit operator.
/// </summary>
/// <remarks>
/// Every <see cref="IAdminOperator"/> mutation appends an audit event carrying the operator ID,
/// so this value is what shows up in the audit trail when an MCP client does not name itself.
/// It is a <c>const</c> because attribute-declared tool parameters require compile-time defaults.
/// </remarks>
public static class DefaultOperator
{
    /// <summary>The operator ID recorded for un-attributed MCP mutations.</summary>
    public const string Id = "mcp";
}
