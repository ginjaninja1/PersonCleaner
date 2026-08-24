using PersonCleaner.V2.Domain;
using System;
using System.Collections.Generic;
using System.Linq;

internal static class Program
{
    private static int passed;
    private static int failed;
    public static int Main()
    {
        Run("normalizes human names", NormalizesNames);
        Run("Wikipedia slug is not a Wikidata person ID", WikipediaIsNotWikidata);
        Run("typed external IDs reject the wrong entity shape", TypedExternalIdsRejectWrongShape);
        Run("Johnny Depp remains a hard external-ID match", JohnnyDeppIsHardMatch);
        Run("hard identifier bridge selects gravitational anchor", HardBridgeSelectsAnchor);
        Run("ambiguous overlap stays in human review", AmbiguousOverlapRequiresReview);
        Run("Neil subset with matching role joins without penalizing missing TVDB credit", NeilSubsetRoleEvidenceMatches);
        Run("two shared credits accumulate enough evidence without birthdays", TwoSharedCreditsAccumulateEvidence);
        Run("asymmetric media IDs resolve through the full crosswalk graph", AsymmetricMediaIdsResolveTransitively);
        Run("missing external ids are neutral", MissingExternalIdsAreNeutral);
        Run("unopposed external id conflict remains reviewable negative evidence", ConflictingExternalIdsRequireReview);
        Run("Derek Luh native crosswalk outweighs a secondary IMDb conflict", DerekLuhCrosswalkOutweighsImdbConflict);
        Run("competing same-name attribution prevents automatic merge", CompetingAttributionRequiresReview);
        Run("unopposed birthday conflict remains reviewable negative evidence", BirthdayConflictRequiresReview);
        Run("Gerald Sim corroborated identity outweighs a birthday conflict", GeraldSimBirthdayConflictRetainsIdentity);
        Run("local media mass survives provider id drift", MediaMassSurvivesIdDrift);
        Run("operator bridge joins disconnected provider records", OperatorBridgeJoinsRecords);
        Run("operator rejection keeps shared-media records separate", OperatorRejectionKeepsRecordsSeparate);
        Run("orphan without provider IDs has persistable provider text", OrphanWithoutIdsHasProviderText);
        Run("authoritative absence recommends review of the stale binding", AuthoritativeAbsenceRecommendsBindingRemoval);
        Run("present but unsupported binding remains human review", PresentUnsupportedBindingRemainsReview);
        Run("unavailable current binding withholds orphan decision", UnavailableBindingWithholdsDecision);
        Run("unavailable credited media withholds person decision", UnavailableMediaWithholdsDecision);
        Run("same name alone never establishes identity", SameNameAloneDoesNotMerge);
        Run("shared title alone never creates a person candidate", SharedTitleAloneDoesNotCreateCandidate);
        Run("one uncertain alignment produces review without duplicate split", ReviewDoesNotDuplicateSplit);
        Run("stable singleton binding is not emitted as a 100 percent provider match", StableSingletonIsNotProviderMatch);
        Run("same-provider collision is blocked at component boundary", SameProviderCollisionIsBlocked);
        Run("manual rejection survives a transitive path", ManualRejectionSurvivesTransitivePath);
        Run("provider correction removes an unusable media-person attribution", ProviderCorrectionSuppressesAttribution);
        Run("provider correction removes a bad media cross-reference before canonicalization", ProviderCorrectionSuppressesMediaCrosswalk);
        Run("provider corrections replace person facts and local bindings", ProviderCorrectionsReplaceFactsAndBindings);
        Run("provider identity correction becomes an operator bridge", ProviderIdentityCorrectionBecomesBridge);
        Console.WriteLine("Passed " + passed + " entity-resolution tests; failed " + failed + ".");
        return failed == 0 ? 0 : 1;
    }

    private static void NormalizesNames()
    {
        Equal("jose o connor jr", TextNormalizer.PersonName("  José O’Connor, Jr. "));
    }

    private static void WikipediaIsNotWikidata()
    {
        string provider; string value;
        True(ExternalIdNormalizer.TryPersonId("Wikidata", "Q37175", out provider, out value));
        Equal(ProviderNames.Wikidata, provider);
        Equal("Q37175", value);
        True(!ExternalIdNormalizer.TryPersonId("Wikipedia", "Johnny_Depp", out provider, out value));
    }

    private static void TypedExternalIdsRejectWrongShape()
    {
        string provider; string value;
        True(ExternalIdNormalizer.TryPersonId("IMDB", "NM0000136", out provider, out value));
        Equal("nm0000136", value);
        True(!ExternalIdNormalizer.TryPersonId("IMDB", "tt0325980", out provider, out value));
        True(ExternalIdNormalizer.TryMediaId("IMDB", "TT0325980", out provider, out value));
        Equal("tt0325980", value);
        True(!ExternalIdNormalizer.TryPersonId("Wikidata", "Johnny_Depp", out provider, out value));
    }

    private static void JohnnyDeppIsHardMatch()
    {
        var tmdb = Person(ProviderNames.Tmdb, "85", "Johnny Depp", ProviderNames.Imdb, "nm0000136", "one", "two", "three");
        tmdb.ExternalIds[ProviderNames.Wikidata] = "Q37175";
        tmdb.Birthday = "1963-06-09";
        var tvdb = Person(ProviderNames.Tvdb, "259154", "Johnny Depp", ProviderNames.Imdb, "nm0000136", "one", "two", "three");
        tvdb.ExternalIds[ProviderNames.Wikidata] = "Q37175";
        tvdb.Birthday = "1963-06-09";
        AddObservedCredit(tmdb, "one", "Actor", "Jack Sparrow"); AddObservedCredit(tvdb, "one", "Actor", "Jack Sparrow");
        AddObservedCredit(tmdb, "two", "Actor", "Role A"); AddObservedCredit(tvdb, "two", "Actor", "Role B");
        AddObservedCredit(tmdb, "three", "Actor", "Role C"); AddObservedCredit(tvdb, "three", "Actor", "Role D");
        var score = ResolutionEngine.Score(tmdb, tvdb, new ResolutionSettings());
        True(score.HardIdentifierMatch);
        True(!score.IdentifierConflict);
        Equal("exact", score.ExternalIdState);
        Equal(1.0, score.Score);
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
        True(review.Headline.Contains("below the automatic threshold"));
    }

    private static void NeilSubsetRoleEvidenceMatches()
    {
        var tmdb = Person(ProviderNames.Tmdb, "85970", "Neil Edmond", ProviderNames.Imdb, "nm0249467", "imdb:tt2567026", "imdb:tt21994906");
        var tvdb = Person(ProviderNames.Tvdb, "484902", "Neil Edmond", null, null, "imdb:tt2567026");
        AddObservedCredit(tmdb, "imdb:tt2567026", "Actor", "Footman");
        AddObservedCredit(tmdb, "imdb:tt21994906", "Actor", "Middle Aged Man");
        AddObservedCredit(tvdb, "imdb:tt2567026", "Actor", "Footman");
        tmdb.Birthday = "1970-12-01";
        var input = BaseInput(tmdb, tvdb);
        input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits));
        input.LocalPeople.Add(new LocalPerson { EmbyId = 40924, Name = "Neil Edmond", TmdbId = "85970", TvdbId = "484902" });
        input.Media.AddRange(new[] { Media(1, "Alice Through the Looking Glass"), Media(2, "Your Christmas or Mine?") });
        input.LocalCredits.AddRange(new[] { Credit(40924, 1), Credit(40924, 2) });
        var engine = new ResolutionEngine();
        var match = engine.Resolve(input, new ResolutionSettings()).Single(x => x.Status == "MATCH");
        var score = engine.PairEvaluations.Single().Score;
        Equal(1, score.SharedMediaCount);
        Equal(0.5, score.FilmographyJaccard);
        Equal(1.0, score.FilmographyContainment);
        Equal(1, score.ExactRoleMatches);
        Equal("missing-opposite", score.ExternalIdState);
        Equal("missing", score.BirthdayState);
        True(score.Score >= 0.75);
        True(match.Evidence.Any(x => x.SignalType == "FILMOGRAPHY" && x.Narrative.Contains("not negative evidence")));
    }

    private static void TwoSharedCreditsAccumulateEvidence()
    {
        var left = Person(ProviderNames.Tmdb, "two-left", "Taylor Evidence", null, null, "one", "two");
        var right = Person(ProviderNames.Tvdb, "two-right", "Taylor Evidence", null, null, "one", "two");
        var score = ResolutionEngine.Score(left, right, new ResolutionSettings());
        True(score.Score >= 0.75);
    }

    private static void AsymmetricMediaIdsResolveTransitively()
    {
        var media = new[]
        {
            ProviderMedia(ProviderNames.Tmdb, "67928", External(ProviderNames.Imdb, "tt0781574"), External(ProviderNames.Wikidata, "Q7906940")),
            ProviderMedia(ProviderNames.Tvdb, "78640", External(ProviderNames.Tmdb, "67928")),
            ProviderMedia(ProviderNames.Tmdb, "31107", External(ProviderNames.Imdb, "tt1556240")),
            ProviderMedia(ProviderNames.Tvdb, "63457", External(ProviderNames.Imdb, "tt1556240"), External(ProviderNames.Tmdb, "31107"))
        };
        var keys = MediaIdentityResolver.Resolve(media);
        Equal(keys[MediaIdentityResolver.RecordKey(ProviderNames.Tmdb, MediaTypes.Movie, "67928")], keys[MediaIdentityResolver.RecordKey(ProviderNames.Tvdb, MediaTypes.Movie, "78640")]);
        Equal(keys[MediaIdentityResolver.RecordKey(ProviderNames.Tmdb, MediaTypes.Movie, "31107")], keys[MediaIdentityResolver.RecordKey(ProviderNames.Tvdb, MediaTypes.Movie, "63457")]);

        var left = Person(ProviderNames.Tmdb, "107618", "Patti Scialfa", ProviderNames.Imdb, "nm0778393",
            keys[MediaIdentityResolver.RecordKey(ProviderNames.Tmdb, MediaTypes.Movie, "67928")],
            keys[MediaIdentityResolver.RecordKey(ProviderNames.Tmdb, MediaTypes.Movie, "31107")]);
        var right = Person(ProviderNames.Tvdb, "334647", "Patti Scialfa", null, null,
            keys[MediaIdentityResolver.RecordKey(ProviderNames.Tvdb, MediaTypes.Movie, "78640")],
            keys[MediaIdentityResolver.RecordKey(ProviderNames.Tvdb, MediaTypes.Movie, "63457")]);
        AddObservedCredit(left, left.CanonicalMediaKeys.ElementAt(0), "Actor", "Self");
        AddObservedCredit(left, left.CanonicalMediaKeys.ElementAt(1), "Actor", "Self");
        AddObservedCredit(right, right.CanonicalMediaKeys.ElementAt(0), "Actor", "Herself");
        AddObservedCredit(right, right.CanonicalMediaKeys.ElementAt(1), "Actor", "Herself");
        var input = BaseInput(left, right);
        input.ProviderCredits.AddRange(left.Credits.Concat(right.Credits));
        input.LocalPeople.Add(new LocalPerson { EmbyId = 290806, Name = "Patti Scialfa", TmdbId = "107618", TvdbId = "334647", ImdbId = "nm0778393" });
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(input, new ResolutionSettings());
        var score = engine.PairEvaluations.Single().Score;
        Equal(2, score.SharedMediaCount);
        Equal(1.0, score.FilmographyContainment);
        True(score.Score >= 0.75);
        True(decisions.Any(x => x.Status == "MATCH"));
    }

    private static void MissingExternalIdsAreNeutral()
    {
        var withId = Person(ProviderNames.Tmdb, "id-left", "Neutral Missing", ProviderNames.Imdb, "nm100", "shared");
        var withoutId = Person(ProviderNames.Tvdb, "id-right", "Neutral Missing", null, null, "shared");
        AddObservedCredit(withId, "shared", "Actor", "Clerk");
        AddObservedCredit(withoutId, "shared", "Actor", "Clerk");
        var score = ResolutionEngine.Score(withId, withoutId, new ResolutionSettings());
        Equal("missing-opposite", score.ExternalIdState);
        True(!score.IdentifierConflict);
        True(score.Score >= 0.75);
    }

    private static void ConflictingExternalIdsRequireReview()
    {
        var left = Person(ProviderNames.Tmdb, "conflict-left", "Identifier Conflict", ProviderNames.Imdb, "nm-left", "shared");
        var right = Person(ProviderNames.Tvdb, "conflict-right", "Identifier Conflict", ProviderNames.Imdb, "nm-right", "shared");
        AddObservedCredit(left, "shared", "Actor", "Clerk");
        AddObservedCredit(right, "shared", "Actor", "Clerk");
        var input = BaseInput(left, right);
        input.ProviderCredits.AddRange(left.Credits.Concat(right.Credits));
        input.LocalPeople.Add(new LocalPerson { EmbyId = 810, Name = "Identifier Conflict", TmdbId = "conflict-left", TvdbId = "conflict-right" });
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(input, new ResolutionSettings());
        True(engine.PairEvaluations.Single().Score.IdentifierConflict);
        True(engine.PairEvaluations.Single().Score.Score >= 0.40);
        True(engine.PairEvaluations.Single().Score.Score < 0.75);
        True(decisions.Any(x => x.Status == "CONFLATION" && x.Evidence.Any(e => e.SignalType == "EXTERNAL_ID" && e.Verdict == "conflicts")));
        True(!decisions.Any(x => x.Status == "SPLIT"));
    }

    private static void DerekLuhCrosswalkOutweighsImdbConflict()
    {
        var tmdb = Person(ProviderNames.Tmdb, "2498030", "Derek Luh", ProviderNames.Imdb, "nm11221274", "gen-v");
        tmdb.ExternalIds[ProviderNames.Wikidata] = "Q33124975";
        tmdb.Birthday = "1992-06-24";
        var tvdb = Person(ProviderNames.Tvdb, "9116159", "Derek Luh", ProviderNames.Imdb, "nm1122127", "gen-v");
        tvdb.ExternalIds[ProviderNames.Tmdb] = "2498030";
        AddObservedCredit(tmdb, "gen-v", "Actor", "Jordan Li");
        AddObservedCredit(tvdb, "gen-v", "Actor", "Jordan Li (Boy)");
        var input = BaseInput(tmdb, tvdb);
        input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits));
        input.LocalPeople.Add(new LocalPerson { EmbyId = 40557, Name = "Derek Luh", TmdbId = "2498030", TvdbId = "9116159", ImdbId = "nm11221274" });
        input.Media.Add(Media(25324, "Gen V")); input.LocalCredits.Add(Credit(40557, 25324));
        var engine = new ResolutionEngine();
        var decision = engine.Resolve(input, new ResolutionSettings()).Single(x => x.Status == "MATCH_WITH_CONFLICT");
        var score = engine.PairEvaluations.Single().Score;
        True(score.NativeProviderCrosswalkMatch);
        True(score.IdentifierConflict);
        Equal("mixed", score.ExternalIdState);
        True(score.Score >= 0.75);
        Equal("CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT", decision.Action);
        True(decision.Evidence.Any(x => x.SignalType == "EXTERNAL_ID" && x.Verdict == "mixed"));
        True(decision.Evidence.Any(x => x.SignalType == "EXTERNAL_ID" && x.Metric.Contains("nm11221274") && x.Metric.Contains("nm1122127")));
        True(!engine.PairEvaluations.Any(x => x.Disposition == "constraint-blocked"));
    }

    private static void CompetingAttributionRequiresReview()
    {
        var left = Person(ProviderNames.Tmdb, "primary-left", "Jordan Credit", null, null, "shared", "exclusive");
        var right = Person(ProviderNames.Tvdb, "primary-right", "Jordan Credit", null, null, "shared");
        var competitor = Person(ProviderNames.Tvdb, "competitor", "Jordan Credit", null, null, "exclusive");
        AddObservedCredit(left, "shared", "Actor", "A"); AddObservedCredit(left, "exclusive", "Actor", "B");
        AddObservedCredit(right, "shared", "Actor", "A"); AddObservedCredit(competitor, "exclusive", "Actor", "B");
        var input = BaseInput(left, right, competitor);
        input.ProviderCredits.AddRange(left.Credits.Concat(right.Credits).Concat(competitor.Credits));
        var engine = new ResolutionEngine(); engine.Resolve(input, new ResolutionSettings());
        var pair = engine.PairEvaluations.Single(x => x.LeftProviderId == "primary-left" && x.RightProviderId == "primary-right");
        Equal(1, pair.Score.CompetingAttributionCount);
        Equal("human-review", pair.Disposition);
    }

    private static void BirthdayConflictRequiresReview()
    {
        var tmdb = Person(ProviderNames.Tmdb, "12", "Chris Example", null, null, "m:1"); tmdb.Birthday = "1970-01-01";
        var tvdb = Person(ProviderNames.Tvdb, "22", "Chris Example", null, null, "m:1"); tvdb.Birthday = "1980-01-01";
        var input = BaseInput(tmdb, tvdb);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 300, Name = "Chris Example", TmdbId = "12", TvdbId = "22" });
        input.Media.Add(Media(1, "Shared")); input.LocalCredits.Add(Credit(300, 1));
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        var review = decisions.Single(x => x.Status == "CONFLATION");
        True(review.Evidence.Any(x => x.SignalType == "BIRTHDAY" && x.Verdict == "conflicts"));
        True(!decisions.Any(x => x.Action == "AUTO_MERGE_SHADOW"));
        True(!decisions.Any(x => x.Status == "SPLIT"));
    }

    private static void GeraldSimBirthdayConflictRetainsIdentity()
    {
        var tmdb = Person(ProviderNames.Tmdb, "91663", "Gerald Sim", ProviderNames.Imdb, "nm0799245", "ryans-daughter");
        tmdb.ExternalIds[ProviderNames.Wikidata] = "Q5549581";
        tmdb.Birthday = "1925-02-04";
        var tvdb = Person(ProviderNames.Tvdb, "282419", "Gerald Sim", ProviderNames.Imdb, "nm0799245", "ryans-daughter", "to-the-manor-born");
        tvdb.Birthday = "1925-06-04";
        AddObservedCredit(tmdb, "ryans-daughter", "Actor", "Captain");
        AddObservedCredit(tvdb, "ryans-daughter", "Actor", "Captain");
        AddObservedCredit(tvdb, "to-the-manor-born", "Actor", "The Rector");
        var input = BaseInput(tmdb, tvdb);
        input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits));
        input.LocalPeople.Add(new LocalPerson { EmbyId = 23430, Name = "Gerald Sim", TmdbId = "91663", TvdbId = "282419", ImdbId = "nm0799245" });
        input.Media.AddRange(new[] { Media(298340, "Ryan's Daughter"), Media(113, "To the Manor Born") });
        input.LocalCredits.AddRange(new[] { Credit(23430, 298340), Credit(23430, 113) });
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(input, new ResolutionSettings());
        var decision = decisions.Single(x => x.Status == "MATCH_WITH_CONFLICT");
        var score = engine.PairEvaluations.Single().Score;
        True(score.BirthdayConflict);
        True(score.HardIdentifierMatch);
        True(score.Score >= 0.75);
        Equal("CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT", decision.Action);
        True(decision.Evidence.Any(x => x.SignalType == "BIRTHDAY" && x.Narrative.Contains("not by itself proof")));
        True(decision.Evidence.Any(x => x.SignalType == "BIRTHDAY" && x.Metric.Contains("1925-02-04") && x.Metric.Contains("1925-06-04")));
        True(!decisions.Any(x => x.Status == "SPLIT"));
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
        var review = decisions.Single(x => x.Status == "CONFLATION");
        Equal(800L, review.AnchorEmbyPersonId.Value);
        True(!decisions.Any(x => x.Status == "SPLIT"));
    }

    private static void StableSingletonIsNotProviderMatch()
    {
        var provider = Person(ProviderNames.Tmdb, "singleton", "Stable Singleton", null, null, "tmdb:movie:1");
        var input = BaseInput(provider);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 820, Name = "Stable Singleton", TmdbId = "singleton" });
        input.Media.Add(Media(1, "Only title")); input.LocalCredits.Add(Credit(820, 1));
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        True(!decisions.Any(x => x.Status == "MATCH"));
    }

    private static void SameProviderCollisionIsBlocked()
    {
        var leftOne = Person(ProviderNames.Tmdb, "same-provider-1", "Collision Name", null, null, "shared");
        var leftTwo = Person(ProviderNames.Tmdb, "same-provider-2", "Collision Name", null, null, "shared");
        var right = Person(ProviderNames.Tvdb, "same-provider-right", "Collision Name", null, null, "shared");
        AddObservedCredit(leftOne, "shared", "Actor", "Role"); AddObservedCredit(leftTwo, "shared", "Actor", "Role"); AddObservedCredit(right, "shared", "Actor", "Role");
        var input = BaseInput(leftOne, leftTwo, right); input.ProviderCredits.AddRange(leftOne.Credits.Concat(leftTwo.Credits).Concat(right.Credits));
        var engine = new ResolutionEngine(); engine.Resolve(input, new ResolutionSettings());
        Equal(1, engine.Diagnostics.ConstraintBlockedCandidates);
        Equal(1, engine.PairEvaluations.Count(x => x.Disposition == "automatic"));
        Equal(1, engine.PairEvaluations.Count(x => x.Disposition == "constraint-blocked"));
    }

    private static void ManualRejectionSurvivesTransitivePath()
    {
        var a = Person(ProviderNames.Tmdb, "a", "Path Person", null, null, "a-d");
        var d = Person(ProviderNames.Tvdb, "d", "Path Person", null, null, "a-d", "c-d");
        var c = Person(ProviderNames.Tmdb, "c", "Path Person", ProviderNames.Imdb, "nm-path", "c-d", "b-c");
        var b = Person(ProviderNames.Tvdb, "b", "Path Person", ProviderNames.Imdb, "nm-path", "b-c");
        AddObservedCredit(c, "b-c", "Actor", "Role"); AddObservedCredit(b, "b-c", "Actor", "Role");
        var input = BaseInput(a, d, c, b); input.ProviderCredits.AddRange(a.Credits.Concat(d.Credits).Concat(c.Credits).Concat(b.Credits));
        input.Bridges.Add(new ManualBridge { ProviderA = ProviderNames.Tmdb, ProviderIdA = "a", ProviderB = ProviderNames.Tvdb, ProviderIdB = "d" });
        input.Bridges.Add(new ManualBridge { ProviderA = ProviderNames.Tmdb, ProviderIdA = "c", ProviderB = ProviderNames.Tvdb, ProviderIdB = "d" });
        input.Bridges.Add(new ManualBridge { ProviderA = ProviderNames.Tmdb, ProviderIdA = "a", ProviderB = ProviderNames.Tvdb, ProviderIdB = "b", IsRejected = true });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 830, Name = "Path Person", TmdbId = "a", TvdbId = "b" });
        var engine = new ResolutionEngine(); var decisions = engine.Resolve(input, new ResolutionSettings());
        var bc = engine.PairEvaluations.Single(x => x.LeftProviderId == "c" && x.RightProviderId == "b");
        Equal("constraint-blocked", bc.Disposition);
        True(decisions.Any(x => x.Status == "SPLIT"));
    }

    private static void MediaMassSurvivesIdDrift()
    {
        var provider = Person(ProviderNames.Tmdb, "new-id", "Taylor Example", null, null, "tmdb:movie:1");
        var input = BaseInput(provider);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 400, Name = "Taylor Example", TmdbId = "old-id" });
        input.PersonAcquisitions.Add(Acquisition(ProviderNames.Tmdb, "old-id", AcquisitionStates.Absent));
        input.Media.Add(Media(1, "Stable title")); input.LocalCredits.Add(Credit(400, 1));
        var decisions = new ResolutionEngine().Resolve(input, new ResolutionSettings());
        var drift = decisions.Single(x => x.Status == "DRIFT");
        Equal(400L, drift.AnchorEmbyPersonId.Value);
        True(drift.Headline.Contains("confirmed the current ID is absent"));
        True(!decisions.Any(x => x.Status == "ORPHAN"));
    }

    private static void ProviderCorrectionSuppressesAttribution()
    {
        var tmdb = Person(ProviderNames.Tmdb, "8323", "Daniel Newman", ProviderNames.Imdb, "nm0628054", "imdb:tt0102798");
        var tvdb = Person(ProviderNames.Tvdb, "331984", "Daniel Newman", ProviderNames.Imdb, "nm1649096", "imdb:tt0102798");
        AddObservedCredit(tmdb, "imdb:tt0102798", "Actor", "Wulf"); AddObservedCredit(tvdb, "imdb:tt0102798", "Actor", "Wulf");
        var input = BaseInput(tmdb, tvdb); input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits));
        var correction = new ProviderCorrection { CorrectionId = 1, Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, MediaType = MediaTypes.Movie, ProviderMediaId = "2035", ProviderPersonId = "331984", CurrentValue = "Actor: Wulf", Reason = "PROVIDER_MISMATCH" };
        input.ProviderCredits.Single(x => x.Provider == ProviderNames.Tvdb).MediaType = MediaTypes.Movie;
        input.ProviderCredits.Single(x => x.Provider == ProviderNames.Tvdb).ProviderMediaId = "2035";
        input.ProviderCredits.Single(x => x.Provider == ProviderNames.Tmdb).MediaType = MediaTypes.Movie;
        input.ProviderCredits.Single(x => x.Provider == ProviderNames.Tmdb).ProviderMediaId = "8367";
        var tracker = new CorrectionApplicationTracker(new[] { correction });
        ProviderCorrectionOverlay.Apply(input, tracker);
        Equal(1, input.ProviderCredits.Count);
        Equal(ProviderNames.Tmdb, input.ProviderCredits[0].Provider);
        True(!input.ProviderPeople.Any(x => x.Key == ProviderNames.Tvdb + ":331984"));
        Equal(1, tracker.Results.Single().MatchedCount);
    }

    private static void ProviderCorrectionSuppressesMediaCrosswalk()
    {
        var tmdb = ProviderMedia(ProviderNames.Tmdb, "8367", External(ProviderNames.Imdb, "tt0102798"));
        var tvdb = ProviderMedia(ProviderNames.Tvdb, "2035", External(ProviderNames.Imdb, "tt0102798"));
        var correction = new ProviderCorrection { CorrectionId = 2, Kind = CorrectionKinds.MediaExternalId, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, MediaType = MediaTypes.Movie, ProviderMediaId = "2035", FieldName = ProviderNames.Imdb, CurrentValue = "tt0102798", Reason = "PROVIDER_MISMATCH" };
        var tracker = new CorrectionApplicationTracker(new[] { correction });
        ProviderCorrectionOverlay.ApplyMediaIdentities(new[] { tmdb, tvdb }, tracker);
        var keys = MediaIdentityResolver.Resolve(new[] { tmdb, tvdb });
        True(keys[MediaIdentityResolver.RecordKey(ProviderNames.Tmdb, MediaTypes.Movie, "8367")] != keys[MediaIdentityResolver.RecordKey(ProviderNames.Tvdb, MediaTypes.Movie, "2035")]);
        Equal(1, tracker.Results.Single().MatchedCount);
    }

    private static void ProviderCorrectionsReplaceFactsAndBindings()
    {
        var person = Person(ProviderNames.Tvdb, "331984", "Daniel Newman", ProviderNames.Imdb, "nm1649096", "shared"); person.Birthday = "1981-06-14";
        AddObservedCredit(person, "shared", "Actor", "Wulf"); person.Credits[0].MediaType = MediaTypes.Movie; person.Credits[0].ProviderMediaId = "2035";
        var input = BaseInput(person); input.ProviderCredits.AddRange(person.Credits); input.LocalPeople.Add(new LocalPerson { EmbyId = 41636, Name = "Daniel Newman", TvdbId = "331984" });
        var corrections = new[]
        {
            new ProviderCorrection { CorrectionId = 3, Kind = CorrectionKinds.PersonField, Operation = CorrectionOperations.Replace, Provider = ProviderNames.Tvdb, ProviderPersonId = "331984", FieldName = "birthday", CurrentValue = "1981-06-14", ReplacementValue = "1976-05-12", Reason = "PROVIDER_MISMATCH" },
            new ProviderCorrection { CorrectionId = 4, Kind = CorrectionKinds.PersonExternalId, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, ProviderPersonId = "331984", FieldName = ProviderNames.Imdb, CurrentValue = "nm1649096", Reason = "PROVIDER_MISMATCH" },
            new ProviderCorrection { CorrectionId = 5, Kind = CorrectionKinds.LocalPersonBinding, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, EmbyId = 41636, CurrentValue = "331984", Reason = "PROVIDER_MISMATCH" }
        };
        ProviderCorrectionOverlay.Apply(input, new CorrectionApplicationTracker(corrections));
        Equal("1976-05-12", input.ProviderPeople.Single().Birthday);
        True(!input.ProviderPeople.Single().ExternalIds.ContainsKey(ProviderNames.Imdb));
        True(input.LocalPeople.Single().TvdbId == null);
    }

    private static void ProviderIdentityCorrectionBecomesBridge()
    {
        var first = Person(ProviderNames.Tmdb, "duplicate-a", "Duplicate Person", null, null, "shared");
        var second = Person(ProviderNames.Tmdb, "duplicate-b", "Duplicate Person", null, null, "shared");
        AddObservedCredit(first, "shared", "Actor", "Role"); AddObservedCredit(second, "shared", "Actor", "Role");
        var input = BaseInput(first, second); input.ProviderCredits.AddRange(first.Credits.Concat(second.Credits));
        var correction = new ProviderCorrection { CorrectionId = 6, Kind = CorrectionKinds.IdentityRelation, Operation = CorrectionOperations.Same, Provider = ProviderNames.Tmdb, ProviderPersonId = "duplicate-a", SecondaryProvider = ProviderNames.Tmdb, SecondaryId = "duplicate-b", Reason = "PROVIDER_DUPLICATE" };
        correction.NormalizeAndValidate();
        ProviderCorrectionOverlay.Apply(input, new CorrectionApplicationTracker(new[] { correction }));
        Equal(1, input.Bridges.Count);
        True(!input.Bridges[0].IsRejected);
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
        True(orphan.ProviderKeys.Contains("No hydrated"));
    }

    private static void AuthoritativeAbsenceRecommendsBindingRemoval()
    {
        var input = new ResolutionInput();
        input.LocalPeople.Add(new LocalPerson { EmbyId = 701, Name = "Stale Binding", TmdbId = "missing-id" });
        input.Media.Add(Media(8, "Local title")); input.LocalCredits.Add(Credit(701, 8));
        input.PersonAcquisitions.Add(Acquisition(ProviderNames.Tmdb, "missing-id", AcquisitionStates.Absent));
        var orphan = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "ORPHAN");
        Equal("REVIEW_REMOVE_STALE_PROVIDER_ID", orphan.Action);
        Equal(1.0, orphan.Confidence);
        Equal("No hydrated provider identity", orphan.ProviderKeys);
        True(orphan.Evidence.Any(x => x.SignalType == "CURRENT_ID_ACQUISITION" && x.Verdict == "absent"));
    }

    private static void PresentUnsupportedBindingRemainsReview()
    {
        var input = new ResolutionInput();
        input.LocalPeople.Add(new LocalPerson { EmbyId = 702, Name = "Unsupported Binding", TmdbId = "existing-id" });
        input.Media.Add(Media(9, "Local title")); input.LocalCredits.Add(Credit(702, 9));
        input.PersonAcquisitions.Add(Acquisition(ProviderNames.Tmdb, "existing-id", AcquisitionStates.Present));
        var orphan = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "ORPHAN");
        Equal("HUMAN_REVIEW", orphan.Action);
        Equal(0.0, orphan.Confidence);
        True(orphan.Headline.Contains("exists"));
    }

    private static void UnavailableBindingWithholdsDecision()
    {
        var input = new ResolutionInput();
        input.LocalPeople.Add(new LocalPerson { EmbyId = 703, Name = "Offline Binding", TmdbId = "unknown-id" });
        input.Media.Add(Media(10, "Local title")); input.LocalCredits.Add(Credit(703, 10));
        input.PersonAcquisitions.Add(Acquisition(ProviderNames.Tmdb, "unknown-id", AcquisitionStates.Unavailable));
        True(!new ResolutionEngine().Resolve(input, new ResolutionSettings()).Any());
    }

    private static void UnavailableMediaWithholdsDecision()
    {
        var input = new ResolutionInput { AcquisitionTrackingEnabled = true };
        input.LocalPeople.Add(new LocalPerson { EmbyId = 704, Name = "Incomplete Media", TmdbId = "existing-id" });
        input.Media.Add(Media(11, "Unavailable title")); input.LocalCredits.Add(Credit(704, 11));
        input.PersonAcquisitions.Add(Acquisition(ProviderNames.Tmdb, "existing-id", AcquisitionStates.Present));
        input.MediaAcquisitions.Add(new MediaAcquisition { Provider = ProviderNames.Tmdb, MediaType = MediaTypes.Movie, ProviderId = "11", State = AcquisitionStates.Unavailable });
        True(!new ResolutionEngine().Resolve(input, new ResolutionSettings()).Any());
    }

    private static ResolutionInput BaseInput(params ProviderPerson[] people) => new ResolutionInput { ProviderPeople = people.ToList() };
    private static PersonAcquisition Acquisition(string provider, string id, string state) => new PersonAcquisition { Provider = provider, ProviderId = id, State = state, Source = "test" };
    private static ProviderPerson Person(string provider, string id, string name, string externalProvider, string externalId, params string[] media)
    {
        var result = new ProviderPerson { Provider = provider, ProviderId = id, Name = name, CanonicalMediaKeys = new HashSet<string>(media) };
        if (externalProvider != null) result.ExternalIds[externalProvider] = externalId;
        return result;
    }
    private static void AddObservedCredit(ProviderPerson person, string media, string category, string role)
    {
        person.Credits.Add(new ObservedProviderCredit { Provider = person.Provider, ProviderPersonId = person.ProviderId, PersonName = person.Name, CanonicalMediaKey = media, RoleCategory = category, RoleName = role, Role = category + ": " + role });
    }
    private static MediaSeed Media(long id, string name) => new MediaSeed { EmbyId = id, MediaType = MediaTypes.Movie, Name = name, TmdbId = id.ToString() };
    private static LocalCredit Credit(long person, long media) => new LocalCredit { PersonEmbyId = person, MediaEmbyId = media, Role = "Actor" };
    private static ProviderMediaIdentity ProviderMedia(string provider, string id, params MediaExternalIdentity[] externalIds) => new ProviderMediaIdentity { Provider = provider, MediaType = MediaTypes.Movie, ProviderMediaId = id, ExternalIds = externalIds.ToList() };
    private static MediaExternalIdentity External(string provider, string id) => new MediaExternalIdentity { Provider = provider, Id = id };
    private static void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); } }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected '" + expected + "' but got '" + actual + "'."); }
}
