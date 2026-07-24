using System.CommandLine;

namespace System.CommandLine;

internal static class CommandLineCompat
{
    public static void AddCommand(this Command command, Command subcommand) => command.Add(subcommand);

    public static void AddOption(this Command command, Option option) => command.Add(option);

    public static void AddArgument(this Command command, Argument argument) => command.Add(argument);

    public static Task<int> InvokeAsync(this Command command, string[] args) =>
        command.Parse(args).InvokeAsync();

    public static void SetHandler<T1, T2, T3>(
        this Command command,
        Func<T1, T2, T3, Task> handler,
        object symbol1,
        object symbol2,
        object symbol3) =>
        command.SetAction(parseResult => handler(
            GetValue<T1>(parseResult, symbol1),
            GetValue<T2>(parseResult, symbol2),
            GetValue<T3>(parseResult, symbol3)));

    public static void SetHandler<T1, T2, T3, T4>(
        this Command command,
        Func<T1, T2, T3, T4, Task> handler,
        object symbol1,
        object symbol2,
        object symbol3,
        object symbol4) =>
        command.SetAction(parseResult => handler(
            GetValue<T1>(parseResult, symbol1),
            GetValue<T2>(parseResult, symbol2),
            GetValue<T3>(parseResult, symbol3),
            GetValue<T4>(parseResult, symbol4)));

    public static void SetHandler<T1, T2, T3, T4, T5>(
        this Command command,
        Func<T1, T2, T3, T4, T5, Task> handler,
        object symbol1,
        object symbol2,
        object symbol3,
        object symbol4,
        object symbol5) =>
        command.SetAction(parseResult => handler(
            GetValue<T1>(parseResult, symbol1),
            GetValue<T2>(parseResult, symbol2),
            GetValue<T3>(parseResult, symbol3),
            GetValue<T4>(parseResult, symbol4),
            GetValue<T5>(parseResult, symbol5)));

    public static void SetHandler<T1, T2, T3, T4, T5, T6>(
        this Command command,
        Func<T1, T2, T3, T4, T5, T6, Task> handler,
        object symbol1,
        object symbol2,
        object symbol3,
        object symbol4,
        object symbol5,
        object symbol6) =>
        command.SetAction(parseResult => handler(
            GetValue<T1>(parseResult, symbol1),
            GetValue<T2>(parseResult, symbol2),
            GetValue<T3>(parseResult, symbol3),
            GetValue<T4>(parseResult, symbol4),
            GetValue<T5>(parseResult, symbol5),
            GetValue<T6>(parseResult, symbol6)));

    public static void SetHandler<T1, T2, T3, T4, T5, T6, T7>(
        this Command command,
        Func<T1, T2, T3, T4, T5, T6, T7, Task> handler,
        object symbol1,
        object symbol2,
        object symbol3,
        object symbol4,
        object symbol5,
        object symbol6,
        object symbol7) =>
        command.SetAction(parseResult => handler(
            GetValue<T1>(parseResult, symbol1),
            GetValue<T2>(parseResult, symbol2),
            GetValue<T3>(parseResult, symbol3),
            GetValue<T4>(parseResult, symbol4),
            GetValue<T5>(parseResult, symbol5),
            GetValue<T6>(parseResult, symbol6),
            GetValue<T7>(parseResult, symbol7)));

    private static T GetValue<T>(ParseResult parseResult, object symbol) =>
        symbol switch
        {
            Option<T> option => parseResult.GetValue(option)!,
            Argument<T> argument => parseResult.GetValue(argument)!,
            _ => throw new InvalidOperationException(
                $"Unsupported command line symbol type '{symbol.GetType().FullName}'.")
        };
}
