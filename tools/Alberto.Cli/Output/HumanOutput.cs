using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Alberto.Cli.Output;

public class HumanOutput : IOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Text(string text)
    {
        AnsiConsole.WriteLine(text);
    }

    public void Table(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new Spectre.Console.Table();
        table.Border = TableBorder.Rounded;

        foreach (var header in headers)
            table.AddColumn(new TableColumn(Markup.Escape(header)).LeftAligned());

        foreach (var row in rows)
        {
            var cells = row.Select(c => (IRenderable)new Markup(Markup.Escape(c ?? string.Empty))).ToArray();
            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
    }

    public void Box(string title, Dictionary<string, string> fields)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn());

        foreach (var (key, value) in fields)
        {
            grid.AddRow(
                new Markup($"[bold]{Markup.Escape(key)}[/]"),
                new Markup(Markup.Escape(value ?? string.Empty))
            );
        }

        var panel = new Panel(grid)
        {
            Header = new PanelHeader(Markup.Escape(title)),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0)
        };

        AnsiConsole.Write(panel);
    }

    public void Json(object data)
    {
        Console.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
    }

    public void Warning(string message)
    {
        Console.Error.WriteLine($"warning: {message}");
    }

    public void Error(string message)
    {
        Console.Error.WriteLine($"error: {message}");
    }
}
