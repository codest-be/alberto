namespace Alberto.Cli.Output;

public interface IOutput
{
    void Text(string text);
    void Table(string[] headers, IEnumerable<string[]> rows);
    void Box(string title, Dictionary<string, string> fields);
    void Json(object data);
    void Warning(string message);
    void Error(string message);
}
