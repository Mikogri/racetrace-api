using System.Text.Json.Nodes;

namespace RaceTrace.Api.Services;

public static class RiderCategoryResolver
{
    public static bool IsJunior(DateOnly? birthDate, DateOnly onDate)
    {
        var age = CalculateAge(birthDate, onDate);
        return age is >= 0 and < 19;
    }

    public static bool WasJuniorInSeason(DateOnly? birthDate, int season) =>
        IsJunior(birthDate, new DateOnly(season, 12, 31));

    public static string? CurrentCategory(
        JsonObject rider,
        DateOnly onDate,
        string? fallbackCategory = null
    )
    {
        var gender = rider.String("gender")?.Trim().ToLowerInvariant();
        var birthDate = PublicDtoMapping.Date(rider.String("birth_date"));
        var junior = IsJunior(birthDate, onDate);
        return gender switch
        {
            "male" => junior ? "junior_men" : "elite_men",
            "female" => junior ? "junior_women" : "elite_women",
            _ => PublicApiCodes.ToPublicCategory(fallbackCategory),
        };
    }

    public static int? CalculateAge(DateOnly? birthDate, DateOnly onDate)
    {
        if (birthDate is null || birthDate > onDate)
        {
            return null;
        }

        var age = onDate.Year - birthDate.Value.Year;
        if (birthDate.Value > onDate.AddYears(-age))
        {
            age--;
        }

        return age;
    }
}
