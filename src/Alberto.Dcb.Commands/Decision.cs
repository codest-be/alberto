using System.Diagnostics.CodeAnalysis;

namespace Alberto.Dcb;

public readonly record struct Decision
{
    private readonly List<IEvent> _events;
    private readonly List<Problem> _problems;

    private Decision(bool isSuccess, List<IEvent> events, List<Problem> problems)
    {
        IsSuccess = isSuccess;
        _events = events;
        _problems = problems;
    }

    public bool IsSuccess { get; }
    public bool IsError => !IsSuccess;
    public IReadOnlyList<IEvent> Events => _events ?? [];
    public IReadOnlyList<Problem> Problems => _problems ?? [];

    public static Decision Succeed(params IEvent[] events) => new(true, events.ToList(), []);
    public static Decision Succeed(IEnumerable<IEvent> events) => new(true, events.ToList(), []);
    public static Decision Fail(Problem problem) => new(false, [], [problem]);
    public static Decision Fail(IEnumerable<Problem> problems) => new(false, [], problems.ToList());

    public static implicit operator Decision(Problem problem) => Fail(problem);
}

[SuppressMessage("Design", "CA1000:Do not declare static members on generic types")]
public readonly record struct Decision<T>
{
    private readonly List<IEvent> _events;
    private readonly List<Problem> _problems;
    private readonly T? _value;

    private Decision(bool isSuccess, T? value, List<IEvent> events, List<Problem> problems)
    {
        IsSuccess = isSuccess;
        _value = value;
        _events = events;
        _problems = problems;
    }

    public bool IsSuccess { get; }
    public bool IsError => !IsSuccess;

    public T Value =>
        IsSuccess ? _value! : throw new InvalidOperationException("Cannot access the value of a failed decision.");

    public IReadOnlyList<IEvent> Events => _events ?? [];
    public IReadOnlyList<Problem> Problems => _problems ?? [];

    public static Decision<T> Succeed(T value, params IEvent[] events) => new(true, value, events.ToList(), []);
    public static Decision<T> Succeed(T value, IEnumerable<IEvent> events) => new(true, value, events.ToList(), []);
    public static Decision<T> Fail(Problem problem) => new(false, default, [], [problem]);
    public static Decision<T> Fail(IEnumerable<Problem> problems) => new(false, default, [], problems.ToList());

    public Decision<TNew> Map<TNew>(Func<T, TNew> transform) =>
        IsSuccess
            ? Decision<TNew>.Succeed(transform(Value), Events)
            : Decision<TNew>.Fail(Problems);

    public static implicit operator Decision<T>(Problem problem) => Fail(problem);
    public static implicit operator Decision(Decision<T> decision) =>
        decision.IsSuccess
            ? Decision.Succeed(decision.Events)
            : Decision.Fail(decision.Problems);
}
