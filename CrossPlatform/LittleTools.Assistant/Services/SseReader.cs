using System.Runtime.CompilerServices;

namespace LittleTools.Assistant.Services;

internal static class SseReader
{
    public sealed record Event(string? Name, string Data);

    public static async IAsyncEnumerable<Event> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var data = new List<string>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    yield return new Event(eventName, string.Join("\n", data));
                    eventName = null;
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                data.Add(line[5..].TrimStart());
        }
        if (data.Count > 0)
        {
            yield return new Event(eventName, string.Join("\n", data));
        }
    }
}
