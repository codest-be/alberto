namespace Alberto.Testing;

/// <summary>
/// Thrown when a specification's expectation is not met.
/// </summary>
/// <remarks>
/// <see cref="Alberto.Testing"/> takes no dependency on a test framework, so a specification
/// throws rather than calling into xUnit, NUnit or MSTest. Every runner reports an unhandled
/// exception as a failure with its message intact, which is all an assertion needs to do.
/// </remarks>
public sealed class SpecificationException : Exception
{
    public SpecificationException() { }

    public SpecificationException(string message) : base(message) { }

    public SpecificationException(string message, Exception innerException)
        : base(message, innerException) { }
}
