using System.Globalization;
using Microsoft.AspNetCore.Http;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public sealed class QueryValidationService
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 150;

    public PageRequest Page(IQueryCollection query)
    {
        var page = Int(query, "page") ?? 1;
        var limit = IntAny(query, "pageSize", "page_size", "limit") ?? DefaultLimit;

        if (page < 1)
        {
            throw BadQuery("page must be greater than or equal to 1.");
        }

        if (limit < 1 || limit > MaxLimit)
        {
            throw BadQuery($"pageSize must be between 1 and {MaxLimit}.");
        }

        return new PageRequest(page, limit);
    }

    public SortRequest Sort(IQueryCollection query, IReadOnlyDictionary<string, string> allowedSorts, string defaultSort, string defaultOrder = "asc")
    {
        var sort = StringAny(query, "sortBy", "sort_by", "sort") ?? defaultSort;
        if (!allowedSorts.ContainsKey(sort))
        {
            throw BadQuery($"Invalid sortBy '{sort}'. Allowed values: {string.Join(", ", allowedSorts.Keys)}.");
        }

        var order = StringAny(query, "sortDirection", "sort_direction", "order") ?? defaultOrder;
        if (!string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase))
        {
            throw BadQuery("sortDirection must be asc or desc.");
        }

        return new SortRequest(sort, order.ToLowerInvariant());
    }

    public string? String(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var value))
        {
            return null;
        }

        var text = value.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public string? StringAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    public int? Int(IQueryCollection query, string key)
    {
        var value = String(query, key);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw BadQuery($"{key} must be an integer.");
        }

        return parsed;
    }

    public int? IntAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is not null)
            {
                return Int(query, key);
            }
        }

        return null;
    }

    public int[] IntCsv(IQueryCollection query, string key)
    {
        var values = Csv(query, key);
        if (values.Length == 0)
        {
            return [];
        }

        var parsedValues = new List<int>(values.Length);
        foreach (var value in values)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw BadQuery($"{key} must contain integers separated by commas.");
            }

            parsedValues.Add(parsed);
        }

        return parsedValues
            .Distinct()
            .ToArray();
    }

    public int[] IntCsvAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is not null)
            {
                return IntCsv(query, key);
            }
        }

        return [];
    }

    public decimal? Decimal(IQueryCollection query, string key)
    {
        var value = String(query, key);
        if (value is null)
        {
            return null;
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            throw BadQuery($"{key} must be a number.");
        }

        return parsed;
    }

    public decimal? DecimalAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is not null)
            {
                return Decimal(query, key);
            }
        }

        return null;
    }

    public DateOnly? Date(IQueryCollection query, string key)
    {
        var value = String(query, key);
        if (value is null)
        {
            return null;
        }

        if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw BadQuery($"{key} must be an ISO date.");
        }

        return parsed;
    }

    public DateOnly? DateAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is not null)
            {
                return Date(query, key);
            }
        }

        return null;
    }

    public bool? Bool(IQueryCollection query, string key)
    {
        var value = String(query, key);
        if (value is null)
        {
            return null;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw BadQuery($"{key} must be true or false.");
        }

        return parsed;
    }

    public bool? BoolAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is not null)
            {
                return Bool(query, key);
            }
        }

        return null;
    }

    public string[] Csv(IQueryCollection query, string key)
    {
        var value = String(query, key);
        return value is null
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string[] CsvAny(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (String(query, key) is not null)
            {
                return Csv(query, key);
            }
        }

        return [];
    }

    public void ValidateDateRange(DateOnly? dateFrom, DateOnly? dateTo)
    {
        if (dateFrom is not null && dateTo is not null && dateFrom > dateTo)
        {
            throw BadQuery("dateFrom must not be greater than dateTo.");
        }
    }

    public void RequireAllowed(IQueryCollection query, string key, IReadOnlySet<string> allowed)
    {
        var value = String(query, key);
        if (value is not null && !allowed.Contains(value))
        {
            throw BadQuery($"Invalid {key} '{value}'. Allowed values: {string.Join(", ", allowed)}.");
        }
    }

    public ApiException BadQuery(string message)
    {
        return new ApiException(StatusCodes.Status400BadRequest, "INVALID_QUERY", message);
    }
}
