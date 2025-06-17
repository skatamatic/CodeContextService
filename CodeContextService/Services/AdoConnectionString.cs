namespace CodeContextService.Services;

public class AdoConnectionString
{
    private readonly Dictionary<string, string> _parts;

    public AdoConnectionString(string connectionString)
    {
        _parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => part[0].Trim().ToLowerInvariant(),
                part => part.Length > 1 ? part[1].Trim() : string.Empty);
    }

    private string Get(string key)
    {
        if (_parts.TryGetValue(key.ToLowerInvariant(), out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new ArgumentException($"Missing required connection string key: '{key}'");
    }

    public string Token => Get("token");
    public string Org => Get("org");
    public string Project => Get("project");
    public string Repo => Get("repo");

    public bool TryGet(string key, out string? value) => _parts.TryGetValue(key.ToLowerInvariant(), out value);
}
