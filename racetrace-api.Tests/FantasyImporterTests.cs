using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Features.Fantasy.Import;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class FantasyImporterTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static string Fixture =>
        File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "pinkbike-fantasy-athletes-2026.html"
            )
        );

    [Fact]
    public void ParsesObservedEmbeddedJsonWithoutExecutingTrailingJavascript()
    {
        var riders = PinkbikeFantasyParser.Parse(Fixture, 2026, FetchedAt);
        Assert.Equal(6, riders.Count);
        var vali = Assert.Single(riders, x => x.ExternalId == "48");
        Assert.Equal("Vali Höll", vali.Name);
        Assert.Equal("female", vali.Gender);
        Assert.Equal(350_000m, vali.Price);
        Assert.Equal(3925, vali.SeasonPoints);
        Assert.Equal(FetchedAt, vali.FetchedAtUtc);
        Assert.Equal("Gracey Hemstreet", Assert.Single(riders, x => x.ExternalId == "59").Name);
    }

    [Fact]
    public void HistoricalPricesAreNotCurrentPricesAndMissingZeroPricesRemainUnknown()
    {
        var bode = Assert.Single(
            PinkbikeFantasyParser.Parse(Fixture, 2026, FetchedAt),
            x => x.ExternalId == "1154"
        );
        Assert.Null(bode.HistoricalRounds.Single(x => x.PinkbikeRoundNumber == 1).Price);
        Assert.Null(bode.HistoricalRounds.Single(x => x.PinkbikeRoundNumber == 2).Price);
        var andorra = bode.HistoricalRounds.Single(x => x.PinkbikeRoundNumber == 6);
        Assert.Equal(85_000m, andorra.Price);
        Assert.Equal(75, andorra.Points); // Observed Q1 P2 bonus retained despite Final DNF.
        Assert.NotEqual(bode.Price, andorra.Price);
    }

    [Theory]
    [InlineData("\"value\":\"340000\"", "\"value\":\"-1\"")]
    [InlineData("\"value\":\"340000\"", "\"value\":\"0\"")]
    [InlineData("\"gender\":\"1\"", "\"gender\":\"3\"")]
    [InlineData("\"year\":\"2026\"", "\"year\":\"2025\"")]
    [InlineData("\"id\":\"2\"", "\"id\":\"14\"")]
    [InlineData("\"totalpoints\":\"1945\"", "\"totalpoints\":\"x\"")]
    [InlineData("2:290,000;3:310,000", "2:290,000;2:310,000")]
    public void RejectsMalformedOrWrongSeasonRecords(string original, string replacement) =>
        Assert.Throws<InvalidDataException>(() =>
            PinkbikeFantasyParser.Parse(Fixture.Replace(original, replacement), 2026, FetchedAt)
        );

    [Fact]
    public void RejectsDuplicateExternalJsonKeys()
    {
        var duplicate = Fixture.Replace("\"14\":{\"id\":\"14\"", "\"2\":{\"id\":\"2\"");
        Assert.Throws<InvalidDataException>(() =>
            PinkbikeFantasyParser.Parse(duplicate, 2026, FetchedAt)
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Attention required. Cloudflare</html>")]
    [InlineData("<script>let aAthletesKeyed = {};let aAllEventsJS = [];</script>")]
    [InlineData("let aAthletesKeyed = {not valid json}; let aAllEventsJS = [];")]
    [InlineData("let aAthletesKeyed = {\"2\": null}; let aAllEventsJS = [];")]
    [InlineData("let aAthletesKeyed = {}; let aAllEventsJS = [null];")]
    [InlineData(
        "let aAthletesKeyed = {}; let aAllEventsJS = [{\"round\":\"1\",\"name\":\"Round\",\"racedate\":\"x\"}];"
    )]
    public void RejectsEmptyBlockedOrIncompleteResponses(string html) =>
        Assert.Throws<InvalidDataException>(() =>
            PinkbikeFantasyParser.Parse(html, 2026, FetchedAt)
        );

    [Theory]
    [InlineData("Valentina HÖLL", "valentina holl")]
    [InlineData("Oisín O’Callaghan", " Oisin O Callaghan ")]
    [InlineData("Lachlan Stevens-McNab", "LACHLAN STEVENS MCNAB")]
    public void MatchesAccentsApostrophesHyphensAndCase(string official, string imported)
    {
        var rider = SampleRider() with { Name = imported };
        var mapped = FantasyRiderMapper.Map([rider], [new("123", official, "male")], new());
        Assert.Equal("123", mapped[0].RiderId);
    }

    [Fact]
    public void DoesNotGuessAmbiguousNamesOrWrongGender()
    {
        var rider = SampleRider();
        Assert.Null(
            FantasyRiderMapper
                .Map([rider], [new("1", rider.Name, "male"), new("2", rider.Name, "male")], new())[
                    0
                ]
                .RiderId
        );
        Assert.Null(
            FantasyRiderMapper.Map([rider], [new("1", rider.Name, "female")], new())[0].RiderId
        );
    }

    [Fact]
    public void ExplicitAliasesAndManualExternalMappingsAreSupported()
    {
        var vali = SampleRider() with { ExternalId = "48", Name = "Vali Höll", Gender = "female" };
        var identities = new[]
        {
            new FantasyRiderIdentity("10", "Valentina Höll", "female"),
            new FantasyRiderIdentity("11", "Other Rider", "female"),
        };
        var options = new FantasyImportOptions { NameAliases = new() { ["10"] = ["Vali Höll"] } };
        Assert.Equal("10", FantasyRiderMapper.Map([vali], identities, options)[0].RiderId);
        options.RiderMappings["48"] = "11";
        Assert.Equal("11", FantasyRiderMapper.Map([vali], identities, options)[0].RiderId);
        options.RiderMappings["48"] = "missing";
        Assert.Equal(
            "invalid_mapping",
            FantasyRiderMapper.Map([vali], identities, options)[0].MappingStatus
        );
    }

    [Fact]
    public void KnownExternalIdSurvivesNameChangesButDuplicateMappingsAreDisabled()
    {
        var rider = SampleRider();
        var prior = Snapshot([rider with { RiderId = "123" }]);
        var identity = new FantasyRiderIdentity("123", "Renamed Rider", "male");
        Assert.Equal("123", FantasyRiderMapper.Map([rider], [identity], new(), prior)[0].RiderId);
        var options = new FantasyImportOptions
        {
            RiderMappings = new() { [rider.ExternalId] = "123", ["other"] = "123" },
        };
        var duplicates = FantasyRiderMapper.Map(
            [rider, rider with { ExternalId = "other" }],
            [identity],
            options
        );
        Assert.All(
            duplicates,
            x =>
            {
                Assert.Null(x.RiderId);
                Assert.Equal("duplicate_mapping", x.MappingStatus);
            }
        );
    }

    [Fact]
    public async Task LegacySnapshotsCannotReenterTheActiveCatalogue()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"racetrace-legacy-test-{Guid.NewGuid():N}"
        );
        var store = new FantasyPriceSnapshotStore(
            Options.Create(new FantasyImportOptions { SnapshotDirectory = "prices" }),
            new TestEnvironment(directory),
            new FixedTime(FetchedAt),
            NullLogger<FantasyPriceSnapshotStore>.Instance
        );
        try
        {
            await store.SaveAsync(Snapshot([SampleRider()]));
            Assert.NotNull(await store.GetLatestAsync(2026));
            var path = Directory
                .GetFiles(Path.Combine(directory, "prices", "2026"), "snapshot-*.json")
                .Single();
            var stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            stored.Remove("dataVersion");
            await File.WriteAllTextAsync(path, stored.ToJsonString());
            Assert.Null(await store.GetLatestAsync(2026));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FailedSyncPreservesDurableHistoryAndHistoricalCutoff()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"racetrace-fantasy-test-{Guid.NewGuid():N}"
        );
        var environment = new TestEnvironment(directory);
        var options = Options.Create(new FantasyImportOptions { SnapshotDirectory = "prices" });
        var store = new FantasyPriceSnapshotStore(
            options,
            environment,
            new FixedTime(FetchedAt),
            NullLogger<FantasyPriceSnapshotStore>.Instance
        );
        try
        {
            var old = Snapshot([SampleRider()]) with
            {
                Id = "older",
                FetchedAtUtc = FetchedAt.AddDays(-2),
            };
            var current = Snapshot([SampleRider()]) with { Id = "newer" };
            await store.SaveAsync(old);
            await store.SaveAsync(current);
            await store.RecordAttemptAsync(2026, "Source unavailable");
            // A new instance verifies persistence, not just in-memory fallback.
            var restarted = new FantasyPriceSnapshotStore(
                options,
                environment,
                new FixedTime(FetchedAt),
                NullLogger<FantasyPriceSnapshotStore>.Instance
            );
            var status = await restarted.GetStatusAsync(2026);
            Assert.Equal("newer", status.Snapshot?.Id);
            Assert.Equal("Source unavailable", status.LastSyncError);
            Assert.False(status.IsStale);
            Assert.Equal(
                "older",
                (await restarted.GetAtOrBeforeAsync(2026, FetchedAt.AddDays(-1)))?.Id
            );
            Assert.Null(await restarted.GetAtOrBeforeAsync(2026, FetchedAt.AddDays(-3)));
            Assert.Equal(
                2,
                Directory
                    .GetFiles(Path.Combine(directory, "prices", "2026"), "snapshot-*.json")
                    .Length
            );
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BrowserSyncUsesExistingValidationMappingAndRoundAssociation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"racetrace-fantasy-test-{Guid.NewGuid():N}"
        );
        var options = Options.Create(new FantasyImportOptions { SnapshotDirectory = "prices" });
        var store = new FantasyPriceSnapshotStore(
            options,
            new TestEnvironment(directory),
            new FixedTime(FetchedAt),
            NullLogger<FantasyPriceSnapshotStore>.Instance
        );
        var browser = new TestBrowserPage { Html = Fixture };
        var importer = new PinkbikeFantasyImporter(
            new TestHttpFactory(),
            browser,
            new ImportSupabaseClient(),
            store,
            options,
            new FixedTime(FetchedAt),
            NullLogger<PinkbikeFantasyImporter>.Instance
        );
        try
        {
            var result = await importer.SyncAsync(2026, "38");
            Assert.Equal("headless-browser", result.AcquisitionMethod);
            Assert.Equal("38", result.UpcomingRoundId);
            Assert.Equal("123", result.Riders.Single(x => x.ExternalId == "2").RiderId);
            browser.Html = "<html><title>Attention Required! | Cloudflare</title></html>";
            await Assert.ThrowsAsync<InvalidDataException>(() => importer.SyncAsync(2026, "38"));
            Assert.Equal(result.Id, (await store.GetLatestAsync(2026))!.Id);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ManualImportSharesSyncLockAndFailedParsePreservesLastSnapshot()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"racetrace-fantasy-test-{Guid.NewGuid():N}"
        );
        var options = Options.Create(new FantasyImportOptions { SnapshotDirectory = "prices" });
        var store = new FantasyPriceSnapshotStore(
            options,
            new TestEnvironment(directory),
            new FixedTime(FetchedAt),
            NullLogger<FantasyPriceSnapshotStore>.Instance
        );
        var database = new ImportSupabaseClient { Block = true };
        var http = new TestBrowserPage();
        var importer = new PinkbikeFantasyImporter(
            new TestHttpFactory(),
            http,
            database,
            store,
            options,
            new FixedTime(FetchedAt),
            NullLogger<PinkbikeFantasyImporter>.Instance
        );
        Task<FantasyPriceSnapshot>? first = null;
        try
        {
            first = importer.ImportHtmlAsync(
                Fixture,
                2026,
                "next",
                "browser-observed",
                CancellationToken.None
            );
            await database.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => importer.SyncAsync(2026));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                importer.ImportHtmlAsync(Fixture, 2026, null, "manual", CancellationToken.None)
            );
            Assert.Equal(0, http.Calls);
            database.Release.TrySetResult();
            var snapshot = await first;
            Assert.Equal("browser-observed", snapshot.AcquisitionMethod);
            Assert.Equal(6, snapshot.Riders.Count);
            Assert.Equal("123", snapshot.Riders.Single(x => x.ExternalId == "2").RiderId);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                importer.ImportHtmlAsync(
                    "let aAthletesKeyed = broken;",
                    2026,
                    null,
                    "manual",
                    CancellationToken.None
                )
            );
            var status = await store.GetStatusAsync(2026);
            Assert.Equal(snapshot.Id, status.Snapshot?.Id);
            Assert.NotNull(status.LastSyncError);
            Assert.Single(
                Directory.GetFiles(Path.Combine(directory, "prices", "2026"), "snapshot-*.json")
            );
            // A failed manual import releases the gate; a new HTTP attempt can run and records its failure.
            await Assert.ThrowsAsync<HttpRequestException>(() => importer.SyncAsync(2026));
            Assert.Equal(1, http.Calls);
            Assert.Equal(snapshot.Id, (await store.GetLatestAsync(2026))?.Id);
        }
        finally
        {
            database.Release.TrySetResult();
            if (first is not null)
                await first;
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static PinkbikeFantasyRider SampleRider() =>
        new(
            "2",
            null,
            "Jackson Goldstone",
            "male",
            340_000,
            1945,
            FetchedAt,
            "unmapped",
            "Canada",
            []
        );

    private static FantasyPriceSnapshot Snapshot(IReadOnlyList<PinkbikeFantasyRider> riders) =>
        new("sample", 2026, null, FetchedAt, riders, PinkbikeFantasyParser.SourceUrl, "test");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task HttpIsTriedFirstAndBrowserOnlyAfterDownloadOrValidationFailure(
        bool validHttp,
        bool blockedHtml
    )
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"racetrace-fantasy-test-{Guid.NewGuid():N}"
        );
        var options = Options.Create(new FantasyImportOptions { SnapshotDirectory = "prices" });
        var store = new FantasyPriceSnapshotStore(
            options,
            new TestEnvironment(directory),
            new FixedTime(FetchedAt),
            NullLogger<FantasyPriceSnapshotStore>.Instance
        );
        var http = new TestHttpFactory
        {
            Html =
                validHttp ? Fixture
                : blockedHtml ? "<html>Cloudflare</html>"
                : null,
        };
        var browser = new TestBrowserPage { Html = Fixture };
        var importer = new PinkbikeFantasyImporter(
            http,
            browser,
            new ImportSupabaseClient(),
            store,
            options,
            new FixedTime(FetchedAt),
            NullLogger<PinkbikeFantasyImporter>.Instance
        );
        try
        {
            var result = await importer.SyncAsync(2026, "38");
            Assert.Equal(1, http.Calls);
            Assert.Equal(validHttp ? 0 : 1, browser.Calls);
            Assert.Equal(validHttp ? "http" : "headless-browser", result.AcquisitionMethod);
            Assert.Equal(6, result.Riders.Count);
            Assert.Equal(result.Id, (await store.GetLatestAsync(2026))!.Id);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private sealed class TestHttpFactory : IHttpClientFactory
    {
        public string? Html { get; init; }
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(TestHttpFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken
            )
            {
                owner.Calls++;
                return Task.FromResult(
                    owner.Html is null
                        ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                owner.Html,
                                System.Text.Encoding.UTF8,
                                "text/html"
                            ),
                        }
                );
            }
        }
    }

    private sealed class TestBrowserPage : PinkbikeBrowserPage
    {
        public string? Html { get; set; }
        public int Calls { get; private set; }

        public override Task<string> FetchAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Html is not null)
                return Task.FromResult(Html);
            throw new HttpRequestException("Pinkbike browser request returned HTTP 403.");
        }
    }

    private sealed class ImportSupabaseClient : ISupabaseRestClient
    {
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            Entered.TrySetResult();
            if (Block)
                await Release.Task.WaitAsync(cancellationToken);
            return new(
                [
                    new JsonObject
                    {
                        ["id"] = 123,
                        ["first_name"] = "Jackson",
                        ["last_name"] = "Goldstone",
                        ["gender"] = "male",
                    },
                ],
                null
            );
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "FantasyTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
