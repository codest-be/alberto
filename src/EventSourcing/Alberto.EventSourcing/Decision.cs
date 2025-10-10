using System.Diagnostics.CodeAnalysis;

namespace Alberto.EventSourcing;

/// <summary>
/// Represents a decision made by a decider, containing either events to persist or problems.
/// </summary>
public readonly record struct Decision
{
    private readonly List<object> _events;
    private readonly List<Problem> _problems;

    private Decision(bool isSuccess, List<object> events, List<Problem> problems)
    {
        IsSuccess = isSuccess;
        _events = events;
        _problems = problems;
    }

    public bool IsSuccess { get; }
    public bool IsError => !IsSuccess;
    public IReadOnlyList<object> Events => _events ?? [];
    public IReadOnlyList<Problem> Problems => _problems ?? [];

    public static Decision Succeed(params object[] events) =>
        new(true, events.ToList(), []);

    public static Decision Succeed(IEnumerable<object> events) =>
        new(true, events.ToList(), []);

    public static Decision Fail(Problem problem) =>
        new(false, [], [problem]);

    public static Decision Fail(IEnumerable<Problem> problems) =>
        new(false, [], problems.ToList());

    public static Decision Fail(string message) =>
        Fail(Problem.Create("Error", message));

    public static implicit operator Decision(Problem problem) => Fail(problem);
}

/// <summary>
/// Represents a decision that returns a value along with events to persist.
/// </summary>
/// <typeparam name="T">The type of the value to return</typeparam>
public readonly record struct Decision<T>
{
    private readonly List<object> _events;
    private readonly List<Problem> _problems;

    [AllowNull, MaybeNull] private readonly T _value;

    private Decision(bool isSuccess, T? value, List<object> events, List<Problem> problems)
    {
        IsSuccess = isSuccess;
        _value = value;
        _events = events;
        _problems = problems;
    }

    public bool IsSuccess { get; }
    public bool IsError => !IsSuccess;

    public T Value =>
        IsSuccess ? _value! : throw new InvalidOperationException("Cannot access value of a failed decision");

    public IReadOnlyList<object> Events => _events ?? [];
    public IReadOnlyList<Problem> Problems => _problems ?? [];

    public static Decision<T> Succeed(T value, params object[] events) =>
        new(true, value, events.ToList(), []);

    public static Decision<T> Succeed(T value, IEnumerable<object> events) =>
        new(true, value, events.ToList(), []);

    public static Decision<T> Fail(Problem problem) =>
        new(false, default, [], [problem]);

    public static Decision<T> Fail(IEnumerable<Problem> problems) =>
        new(false, default, [], problems.ToList());

    public static Decision<T> Fail(string message) =>
        Fail(Problem.Create("Error", message));

    public static implicit operator Decision<T>(Problem problem) => Fail(problem);

    public static implicit operator Decision(Decision<T> decision) =>
        decision.IsSuccess
            ? Decision.Succeed(decision.Events)
            : Decision.Fail(decision.Problems);
}