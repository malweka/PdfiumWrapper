namespace PdfiumWrapper.Tests.Host;

internal sealed class HostArgumentException : Exception
{
    public HostArgumentException(string message) : base(message) { }
}

/// <summary>Parses <c>key=value</c> arguments.</summary>
internal sealed class HostArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public HostArgs(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            int eq = arg.IndexOf('=');
            if (eq <= 0)
                throw new HostArgumentException($"expected key=value, got '{arg}'");
            _values[arg[..eq]] = arg[(eq + 1)..];
        }
    }

    public string Required(string key)
        => _values.TryGetValue(key, out var value)
            ? value
            : throw new HostArgumentException($"missing required argument '{key}='");

    public string String(string key, string fallback)
        => _values.TryGetValue(key, out var value) ? value : fallback;

    public int Int(string key, int fallback)
    {
        if (!_values.TryGetValue(key, out var value))
            return fallback;
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new HostArgumentException($"argument '{key}' must be an integer, got '{value}'");
    }
}
