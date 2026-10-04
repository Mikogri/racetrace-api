namespace RaceTrace.Api.Features.Fantasy;

public static class FantasyRules2026
{
    public const string SourceUrl = "https://www.pinkbike.com/news/pinkbikes-dh-fantasy-league-contest-how-to-2026.html";
    public const string ScoringImageUrl = "https://ep1.pinkbike.org/p4pb29633156/p4pb29633156.jpg";
    public const string AthletesUrl = "https://www.pinkbike.com/contest/fantasy/dh/athletes/";
    // Transcribed from the official 2026 graphic, not interpolated from the podium.
    public static FantasyRules Rules { get; } = new(2026, "pinkbike-dh-2026-v1", 1_500_000m, 6, 4, 2,
        Positions([500,400,300,275,250,240,230,220,210,200,190,180,170,160,150,140,130,120,110,100,90,80,70,60,50,40,30,20,15,10]),
        Positions([500,400,300,275,250,225,200,175,150,125,100,75,50,25,10]),
        Positions([100,75,50]));

    private static IReadOnlyDictionary<int, int> Positions(int[] values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(values.Select((points, index) => (points, index)).ToDictionary(value => value.index + 1, value => value.points));
}
