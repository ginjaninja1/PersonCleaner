using PersonCleaner.V2.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

internal static class Program
{
    private static int passed;
    public static int Main()
    {
        Run("normalizes human names", NormalizesNames);
        Run("hard identifier bridge selects gravitational anchor", HardBridgeSelectsAnchor);
        Run("ambiguous overlap stays in human review", AmbiguousOverlapRequiresReview);
        Run("birthday conflict prevents merge and exposes split", BirthdayConflictExposesSplit);
        Run("local media mass survives provider id drift", MediaMassSurvivesIdDrift);
        Run("operator bridge joins disconnected provider records", OperatorBridgeJoinsRecords);
        Run("operator rejection keeps shared-media records separate", OperatorRejectionKeepsRecordsSeparate);
        Run("orphan without provider IDs has persistable provider text", OrphanWithoutIdsHasProviderText);
        Run("same name alone never establishes identity", SameNameAloneDoesNotMerge);
        Run("shared title alone never creates a person candidate", SharedTitleAloneDoesNotCreateCandidate);
        Run("one uncertain alignment produces review without duplicate split", ReviewDoesNotDuplicateSplit);
        Console.WriteLine("Passed " + passed + " entity-resolution tests.");
        return 0;
    }

    private static void NormalizesNames()
    {
        Equal("jose o connor jr", TextNormalizer.PersonName("  José O’Connor, Jr. "));
    }

    private static void HardBridgeSelectsAnchor()
    {
        var tmdb = Person(ProviderNames.Tmdb, "10", "Alex Example", "imdb", "nm001", "m:1", "m:2");
        var tvdb = Person(ProviderNames.Tvdb, "20", "Alex Example", "imdb", "nm001", "m:1", "m:2");
        var input = BaseInput(tmdb, tvdb);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 100, Name = "Alex Example", TmdbId = "10" });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 200, Name = "Alex Example", TvdbId = "20" });
        input.Media.AddRange(new[] { Media(1, "One"), Media(2, "Two"), Media(3, "Three") });
        input.LocalCredits.AddRange(new[] { Credit(100, 1), Credit(100, 2), Credit(200, 3) });
        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Action == "AUTO_MERGE_SHADOW");
        Equal(100L, decision.AnchorEmbyPersonId.Value);
        Equal(1.0, decision.Confidence);
        True(decision.Evidence.Any(x => x.SignalType == "EXTERNAL_ID" && x.Verdict == "proves"));
    }

    private static void AmbiguousOverlapRequiresReview()
    {
        var tmdb = Person(ProviderNames.Tmdb, "11", "Robin Example", null, null, "m:1", "m:2");
        var tvdb = Person(ProviderNames.Tvdb, "21", "Robin Example", null, null, "m:1");
        var decisions = new ResolutionEngine().Resolve(BaseInput(tmdb, tvdb), new ResolutionSettings());
        var review = decisions.Single(x => x.Action == "HUMAN_REVIEW" && x.Status == "CONFLATION");
        True(review.Confidence >= 0.40 && review.Confidence < 0.75);
        True(review.Headline.Contains("not strong enough"));
    }

    private static void BirthdayConflictExposesSplit()
    {
        var tmdb = Person(ProviderNames.Tmdb, "12", "Chris Example", null, null, "m:1"); tmdb.Birthday = "1970-01-01";
        var tvdb = Person(ProviderNames.Tvdb, "22", "Chris Example", null, null, "m:1"); tvdb.Birthday = "1980-01-01";
        var input = BaseInput(tmdb, tvdb);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 300, Name = "Chris Example", TmdbId = "12", TvdbId = "22" });
        input.Media.Add(Media(1, "Shared")); input.LocalCredits.Add(Credit(300, 1));
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        var split = decisions.Single(x => x.Status == "SPLIT");
        True(split.Headline.Contains("2 disconnected"));
        True(!decisions.Any(x => x.Action == "AUTO_MERGE_SHADOW"));
    }

    private static void SameNameAloneDoesNotMerge()
    {
        var left = Person(ProviderNames.Tmdb, "13", "Sam Lee", null, null, "a");
        var right = Person(ProviderNames.Tvdb, "23", "Sam Lee", null, null, "b");
        var decisions = new ResolutionEngine().Resolve(BaseInput(left, right), new ResolutionSettings());
        True(!decisions.Any(x => x.Action == "AUTO_MERGE_SHADOW" || x.Status == "CONFLATION"));
    }

    private static void SharedTitleAloneDoesNotCreateCandidate()
    {
        var left = Person(ProviderNames.Tmdb, "16", "Johnny Example", null, null, "shared-title");
        var right = Person(ProviderNames.Tvdb, "26", "David Different", null, null, "shared-title");
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(BaseInput(left, right), new ResolutionSettings());
        True(!decisions.Any(x => x.Status == "CONFLATION" || x.Action == "AUTO_MERGE_SHADOW"));
        Equal(1, engine.Diagnostics.BlockedCrossProviderPairs);
        Equal(0, engine.Diagnostics.AdmittedCandidates);
    }

    private static void ReviewDoesNotDuplicateSplit()
    {
        var left = Person(ProviderNames.Tmdb, "17", "Robin Review", null, null, "shared-one", "tmdb-only");
        var right = Person(ProviderNames.Tvdb, "27", "Robin Review", null, null, "shared-one");
        var input = BaseInput(left, right);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 800, Name = "Robin Review", TmdbId = "17", TvdbId = "27" });
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        Equal(1, decisions.Count(x => x.Status == "CONFLATION"));
        True(!decisions.Any(x => x.Status == "SPLIT"));
    }

    private static void MediaMassSurvivesIdDrift()
    {
        var provider = Person(ProviderNames.Tmdb, "new-id", "Taylor Example", null, null, "tmdb:movie:1");
        var input = BaseInput(provider);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 400, Name = "Taylor Example", TmdbId = "old-id" });
        input.Media.Add(Media(1, "Stable title")); input.LocalCredits.Add(Credit(400, 1));
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        var drift = decisions.Single(x => x.Status == "DRIFT");
        Equal(400L, drift.AnchorEmbyPersonId.Value);
        True(drift.Headline.Contains("pull the new provider profile back"));
        True(!decisions.Any(x => x.Status == "ORPHAN"));
    }

    private static void OperatorBridgeJoinsRecords()
    {
        var left = Person(ProviderNames.Tmdb, "14", "Morgan Example", null, null, "left-only");
        var right = Person(ProviderNames.Tvdb, "24", "M. Example", null, null, "right-only");
        var input = BaseInput(left, right);
        input.Bridges.Add(new ManualBridge { ProviderA = ProviderNames.Tmdb, ProviderIdA = "14", ProviderB = ProviderNames.Tvdb, ProviderIdB = "24" });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 500, Name = "Morgan Example", TmdbId = "14" });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 501, Name = "M. Example", TvdbId = "24" });
        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Action == "AUTO_MERGE_SHADOW");
        True(decision.Evidence.Any(x => x.SignalType == "OPERATOR_BRIDGE"));
    }

    private static void OperatorRejectionKeepsRecordsSeparate()
    {
        var left = Person(ProviderNames.Tmdb, "15", "Jamie Example", null, null, "shared");
        var right = Person(ProviderNames.Tvdb, "25", "Jamie Example", null, null, "shared");
        left.Birthday = right.Birthday = "1990-01-01";
        var input = BaseInput(left, right);
        input.Bridges.Add(new ManualBridge { ProviderA = ProviderNames.Tmdb, ProviderIdA = "15", ProviderB = ProviderNames.Tvdb, ProviderIdB = "25", IsRejected = true });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 600, Name = "Jamie Example", TmdbId = "15", TvdbId = "25" });
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        True(decisions.Any(x => x.Status == "SPLIT"));
        True(!decisions.Any(x => x.Action == "AUTO_MERGE_SHADOW"));
    }

    private static void OrphanWithoutIdsHasProviderText()
    {
        var input = new ResolutionInput();
        input.LocalPeople.Add(new LocalPerson { EmbyId = 700, Name = "Unidentified Example" });
        input.Media.Add(Media(7, "Local title")); input.LocalCredits.Add(Credit(700, 7));
        var orphan = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "ORPHAN");
        True(!string.IsNullOrWhiteSpace(orphan.ProviderKeys));
        True(orphan.ProviderKeys.Contains("No current"));
    }

    private static ResolutionInput BaseInput(params ProviderPerson[] people) => new ResolutionInput { ProviderPeople = people.ToList() };
    private static ProviderPerson Person(string provider, string id, string name, string externalProvider, string externalId, params string[] media)
    {
        var result = new ProviderPerson { Provider = provider, ProviderId = id, Name = name, CanonicalMediaKeys = new HashSet<string>(media) };
        if (externalProvider != null) result.ExternalIds[externalProvider] = externalId;
        return result;
    }
    private static MediaSeed Media(long id, string name) => new MediaSeed { EmbyId = id, MediaType = MediaTypes.Movie, Name = name, TmdbId = id.ToString() };
    private static LocalCredit Credit(long person, long media) => new LocalCredit { PersonEmbyId = person, MediaEmbyId = media, Role = "Actor" };
    private static void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); throw; } }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected '" + expected + "' but got '" + actual + "'."); }
}
