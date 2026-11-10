using System.Globalization;

namespace RaceTrace.Api.Infrastructure;

public sealed class SupabaseQuery
{
    private readonly List<KeyValuePair<string, string>> _parameters = [];

    public SupabaseQuery(string table)
    {
        Table = table;
    }

    public string Table { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Parameters => _parameters;

    public SupabaseQuery Select(string select)
    {
        _parameters.Add(new("select", select));
        return this;
    }

    public SupabaseQuery Filter(string column, string op, object? value)
    {
        if (value is null)
        {
            return this;
        }

        _parameters.Add(new(column, $"{op}.{FormatValue(value)}"));
        return this;
    }

    public SupabaseQuery Is(string column, bool? value)
    {
        if (value is null)
        {
            return this;
        }

        _parameters.Add(new(column, $"is.{value.Value.ToString().ToLowerInvariant()}"));
        return this;
    }

    public SupabaseQuery In(string column, IEnumerable<string> values)
    {
        var cleanValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cleanValues.Length == 0)
        {
            return this;
        }

        _parameters.Add(new(column, $"in.({string.Join(",", cleanValues.Select(FormatValue))})"));
        return this;
    }

    public SupabaseQuery Or(string expression)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            _parameters.Add(new("or", $"({expression})"));
        }

        return this;
    }

    public SupabaseQuery Order(string column, bool descending)
    {
        _parameters.Add(new("order", $"{column}.{(descending ? "desc" : "asc")}"));
        return this;
    }

    public SupabaseQuery Limit(int limit)
    {
        _parameters.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    public SupabaseQuery Offset(int offset)
    {
        _parameters.Add(new("offset", offset.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    public SupabaseQuery Add(string key, string value)
    {
        _parameters.Add(new(key, value));
        return this;
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            bool boolean => boolean.ToString().ToLowerInvariant(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture)
                ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }
}
