using System.Diagnostics.CodeAnalysis;

namespace Alberto;

public readonly record struct Result
{
    private readonly List<Problem> _problems;

    private Result(bool isSuccess, List<Problem> problems)
    {
        IsSuccess = isSuccess;
        _problems = problems;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public IReadOnlyList<Problem> Problems => _problems ?? [];

    public static Result Success() => new(true, []);
    public static Result Fail(Problem problem) => new(false, [problem]);
    public static Result Fail(IEnumerable<Problem> problems) => new(false, problems.ToList());

    public static implicit operator Result(Problem problem) => Fail(problem);
}

[SuppressMessage("Design", "CA1000:Do not declare static members on generic types")]
public readonly record struct Result<T>
{
    private readonly List<Problem> _problems;
    private readonly T? _value;

    private Result(bool isSuccess, T? value, List<Problem> problems)
    {
        IsSuccess = isSuccess;
        _value = value;
        _problems = problems;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value =>
        IsSuccess ? _value! : throw new InvalidOperationException("Cannot access the value of a failed result.");

    public IReadOnlyList<Problem> Problems => _problems ?? [];

    public static Result<T> Success(T value) => new(true, value, []);
    public static Result<T> Fail(Problem problem) => new(false, default, [problem]);
    public static Result<T> Fail(IEnumerable<Problem> problems) => new(false, default, problems.ToList());

    public Result<TNew> Map<TNew>(Func<T, TNew> transform) =>
        IsSuccess ? Result<TNew>.Success(transform(Value)) : Result<TNew>.Fail(Problems);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Problem problem) => Fail(problem);
}
