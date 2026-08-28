using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using PersonCleaner.V2.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

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
        Run("role-aware media dominance outweighs an external id conflict", RoleAwareMediaDominanceOutweighsExternalIdConflict);
        Run("Derek Luh native crosswalk outweighs a secondary IMDb conflict", DerekLuhCrosswalkOutweighsImdbConflict);
        Run("uncorroborated native crosswalk cannot establish identity", UncorroboratedNativeCrosswalkRequiresReview);
        Run("same-title competing attribution prevents media dominance", SameTitleCompetitorPreventsDominance);
        Run("competing same-name attribution prevents automatic merge", CompetingAttributionRequiresReview);
        Run("specific competing attribution suggests a provider credit replacement", CompetingAttributionSuggestsProviderCorrection);
        Run("identity conflicts name the disagreeing provider IDs", IdentityConflictNamesProviderIds);
        Run("unopposed birthday conflict remains reviewable negative evidence", BirthdayConflictRequiresReview);
        Run("same-year birthday disagreement stays informational", GeraldSimBirthdayConflictRetainsIdentity);
        Run("Kyle Hebert role-aware media dominance outweighs correlated TVDB conflicts", KyleHebertMediaDominanceOutweighsTvdbConflicts);
        Run("local media mass survives provider id drift", MediaMassSurvivesIdDrift);
        Run("out-of-scope global provider owner withholds drift action", OutOfScopeProviderOwnerWithholdsDrift);
        Run("explicitly in-scope provider owner participates in merge", InScopeProviderOwnerParticipatesInMerge);
        Run("Samantha Kelly mixed local credits become one exact realignment", SamanthaKellyCreditsRealignExactly);
        Run("ambiguous Samantha Kelly credit withholds realignment", SamanthaKellyAmbiguityWithholdsMutation);
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
        Run("Emby change planner scopes shadow credit moves and missing bindings", ChangePlannerScopesMerge);
        Run("Emby change planner labels an already aligned match as requiring no changes", ChangePlannerLabelsAlignedMatch);
        Run("Emby change planner exposes present-ID drift as a manual replacement", ChangePlannerExposesManualDrift);
        Run("Emby change planner carries proposed IMDb identity with TMDB drift", ChangePlannerCarriesExternalIdentity);
        Run("Emby change planner removes only provider-confirmed stale bindings", ChangePlannerScopesStaleRemoval);
        Run("Emby change planner exposes unsupported orphan bindings for manual removal", ChangePlannerExposesOrphanRemoval);
        Run("offline resolution reports bounded stage progress", ResolutionReportsProgress);
        Run("offline resolution observes cancellation", ResolutionObservesCancellation);
        Run("review cases group connected provider relationships", ReviewCasesGroupConnectedRelationships);
        Run("review case automation requires one converged correction", ReviewCaseAutomationRequiresConvergence);
        Run("review case automation respects incomplete scope", ReviewCaseAutomationRespectsScope);
        Run("review cases never group by display name alone", ReviewCasesDoNotGroupByName);
        Run("holistic Lily plan creates one provider-identified person and moves two credits", HolisticLilyPlan);
        Run("holistic Donna plan treats unopposed disconnected providers as a warning", HolisticDonnaPlan);
        Run("holistic drift plan reuses its unique media-backed Emby anchor", HolisticDriftReusesAnchor);
        Run("holistic orphan plan preserves IDs on a retained Emby person", HolisticOrphanPreservesIds);
        Run("holistic existing-person result preserves the current Emby name", HolisticExistingNameIsHonest);
        Run("holistic provider agreement identifies pending Emby ID alignment", HolisticProviderAgreementShowsPendingEmbyAlignment);
        Run("holistic metadata conflict exposes the birthday warning", HolisticBirthdayConflictWarning);
        Run("holistic plan persists a genuine ambiguous media question", HolisticAmbiguousMediaQuestion);
        Run("holistic crosswalk conflict does not become a title-credit dispute", HolisticCrosswalkConflictDoesNotDisputeCredit);
        Run("holistic IMDb conflict retains the current Emby ID and provider-owner matrix", HolisticImdbConflictRetainsCurrentId);
        Run("holistic planner keeps compatible current IDs together beside a conflicting alternative", HolisticCurrentIdentitySubsetRemainsTogether);
        Run("holistic planner remains bounded across 1600 cases", HolisticPlannerRemainsBounded);
        Run("person builder records only the provider rule selected by a no-op layout", PersonBuilderRecordsNoOpAdjudication);
        Run("person builder selects Tim-like exact provider credit replacement", PersonBuilderSelectsProviderReplacement);
        Run("person builder creates a suggested owner and selects one provider rule", PersonBuilderCreatesAndMoves);
        Run("person builder does not persist one-time new-person targets", PersonBuilderDoesNotPersistNewTarget);
        Run("person builder rejects duplicate provider IDs across final people", PersonBuilderRejectsDuplicateIds);
        Run("person builder requires a destination for an unresolved identity", PersonBuilderRequiresIdentityDestination);
        Run("person builder actions precede terminal grid content", PersonBuilderActionsPrecedeGrid);
        Run("person builder Create appends an empty row and retains the existing person", PersonBuilderCreateAppendsEmptyRow);
        Run("person builder planner notes remain transient across grid refreshes", PersonBuilderPlannerNotesRoundTrip);
        Run("case review episodes link both episode and series", CaseReviewEpisodeAndSeriesLinks);
        Run("case review out-of-scope media is enabled by default", CaseReviewMediaEnabledByDefault);
        Run("case review adds missing live credits and compiles only moved supplements", CaseReviewAddsMissingLiveCredits);
        Run("case planning does not scan 300000 unrelated global Emby people", CasePlanningIgnoresLargeGlobalPopulation);
        Run("large unrelated provider-credit sets remain bounded", LargeProviderCreditSetRemainsBounded);
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
        True(review.Headline.Contains("not enough to identify them as the same person automatically"));
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

    private static void RoleAwareMediaDominanceOutweighsExternalIdConflict()
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
        True(engine.PairEvaluations.Single().Score.MediaAttributionDominant);
        True(decisions.Any(x => x.Status == "MATCH_WITH_CONFLICT" && x.Evidence.Any(e => e.SignalType == "EXTERNAL_ID" && e.Verdict == "conflicts")));
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

    private static void UncorroboratedNativeCrosswalkRequiresReview()
    {
        var tmdb = Person(ProviderNames.Tmdb, "571225", "Unrelated Person", null, null, "unrelated-title");
        var tvdb = Person(ProviderNames.Tvdb, "505422", "Kyle Hebert", null, null, "alice");
        tvdb.ExternalIds[ProviderNames.Tmdb] = "571225";
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(BaseInput(tmdb, tvdb), new ResolutionSettings());
        var pair = engine.PairEvaluations.Single();
        True(pair.Score.NativeProviderCrosswalkMatch);
        True(!pair.Score.MediaAttributionDominant);
        Equal("human-review", pair.Disposition);
        True(decisions.Any(x => x.Status == "CONFLATION" && x.Headline.Contains("do not confirm")));
        True(!decisions.Any(x => x.Status == "MATCH" || x.Status == "MATCH_WITH_CONFLICT"));
    }

    private static void SameTitleCompetitorPreventsDominance()
    {
        var tmdb = Person(ProviderNames.Tmdb, "dominant-left", "Kyle Hebert", null, null, "alice");
        var tvdb = Person(ProviderNames.Tvdb, "dominant-right", "Kyle Hebert", null, null, "alice");
        var competitor = Person(ProviderNames.Tvdb, "same-title-competitor", "Kyle Hebert", null, null, "alice");
        AddObservedCredit(tmdb, "alice", "Actor", "Young Bayard");
        AddObservedCredit(tvdb, "alice", "Actor", "Young Bayard");
        AddObservedCredit(competitor, "alice", "Actor", "Young Bayard");
        var input = BaseInput(tmdb, tvdb, competitor);
        input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits).Concat(competitor.Credits));
        var engine = new ResolutionEngine(); engine.Resolve(input, new ResolutionSettings());
        var pair = engine.PairEvaluations.Single(x => x.LeftProviderId == "dominant-left" && x.RightProviderId == "dominant-right");
        True(pair.Score.CompetingAttributionCount > 0);
        True(!pair.Score.MediaAttributionDominant);
        Equal("human-review", pair.Disposition);
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

    private static void CompetingAttributionSuggestsProviderCorrection()
    {
        var tmdb = Person(ProviderNames.Tmdb, "15739", "Alex Jennings", ProviderNames.Imdb, "nm0421105", "shared", "shared-two", "your-christmas-2");
        var tvdb = Person(ProviderNames.Tvdb, "457122", "Alex Jennings", ProviderNames.Imdb, "nm0421105", "shared", "shared-two");
        tvdb.ExternalIds[ProviderNames.Tmdb] = "15739";
        var competitor = Person(ProviderNames.Tvdb, "8302951", "Alex Jennings", ProviderNames.Imdb, "nm4532245", "your-christmas-2");
        competitor.ExternalIds[ProviderNames.Tmdb] = "2276924";
        AddObservedCredit(tmdb, "shared", "Actor", "Horatio", MediaTypes.Movie, "9801");
        AddObservedCredit(tvdb, "shared", "Actor", "Horatio", MediaTypes.Movie, "2715");
        AddObservedCredit(tmdb, "shared-two", "Actor", "Alan Bennett", MediaTypes.Movie, "328589");
        AddObservedCredit(tvdb, "shared-two", "Actor", "Alan Bennett", MediaTypes.Movie, "6629");
        AddObservedCredit(tmdb, "your-christmas-2", "Actor", "Humphrey", MediaTypes.Movie, "1176139");
        AddObservedCredit(competitor, "your-christmas-2", "Actor", "Humphrey", MediaTypes.Movie, "351731");
        var input = BaseInput(tmdb, tvdb, competitor);
        input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits).Concat(competitor.Credits));
        input.Media.Add(new MediaSeed { EmbyId = 381067, MediaType = MediaTypes.Movie, Name = "Your Christmas or Mine 2", Year = 2023, TmdbId = "1176139", TvdbId = "351731" });
        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "CONFLATION" && x.ProviderKeys.Contains("tvdb:457122"));
        True(decision.Headline.Contains("Your Christmas or Mine 2"));
        True(decision.Headline.Contains("TVDB"));
        True(decision.Headline.Contains("8302951"));
        var plan = DecisionChangePlanner.Build(new DecisionChangeContext { Decision = decision, ProposedProviderPeople = input.ProviderPeople });
        Equal(CorrectionKinds.MediaCredit, plan.RecommendedCorrection.Kind);
        Equal(ProviderNames.Tvdb, plan.RecommendedCorrection.Provider);
        Equal("351731", plan.RecommendedCorrection.ProviderMediaId);
        Equal("8302951", plan.RecommendedCorrection.ProviderPersonId);
        Equal("457122", plan.RecommendedCorrection.ReplacementValue);
        True(plan.NoChangeExplanation.Contains("pre-filled provider correction"));
        var wrongDecision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "CONFLATION" && x.ProviderKeys.Contains("tvdb:8302951"));
        var wrongPlan = DecisionChangePlanner.Build(new DecisionChangeContext { Decision = wrongDecision, ProposedProviderPeople = input.ProviderPeople });
        Equal("351731", wrongPlan.RecommendedCorrection.ProviderMediaId);
        Equal("8302951", wrongPlan.RecommendedCorrection.ProviderPersonId);
        Equal("457122", wrongPlan.RecommendedCorrection.ReplacementValue);
    }

    private static void IdentityConflictNamesProviderIds()
    {
        var tmdb = Person(ProviderNames.Tmdb, "15739", "Alex Jennings", ProviderNames.Imdb, "nm0421105", "your-christmas-2");
        var tvdb = Person(ProviderNames.Tvdb, "8302951", "Alex Jennings", ProviderNames.Imdb, "nm4532245", "your-christmas-2");
        var competitor = Person(ProviderNames.Tvdb, "457122", "Alex Jennings", ProviderNames.Imdb, "nm0421105", "your-christmas-2");
        tvdb.ExternalIds[ProviderNames.Tmdb] = "2276924";
        AddObservedCredit(tmdb, "your-christmas-2", "Actor", "Humphrey");
        AddObservedCredit(tvdb, "your-christmas-2", "Actor", "Humphrey");
        AddObservedCredit(competitor, "your-christmas-2", "Actor", "Humphrey");
        var input = BaseInput(tmdb, tvdb, competitor); input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits).Concat(competitor.Credits));
        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "CONFLATION" && x.ProviderKeys.Contains("tvdb:8302951"));
        True(decision.Headline.Contains("TMDB person 15739"));
        True(decision.Headline.Contains("TVDB person 8302951"));
        True(decision.Headline.Contains("nm0421105"));
        True(decision.Headline.Contains("nm4532245"));
        True(decision.Headline.Contains("2276924"));
    }

    private static void BirthdayConflictRequiresReview()
    {
        var tmdb = Person(ProviderNames.Tmdb, "12", "Chris Example", null, null, "m:1"); tmdb.Birthday = "1970-01-01";
        var tvdb = Person(ProviderNames.Tvdb, "22", "Chris Example", null, null, "m:1"); tvdb.Birthday = "1980-01-01";
        var input = BaseInput(tmdb, tvdb);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 300, Name = "Chris Example", TmdbId = "12", TvdbId = "22" });
        input.Media.Add(Media(1, "Shared")); input.LocalCredits.Add(Credit(300, 1));
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(input, new ResolutionSettings());
        var review = decisions.Single(x => x.Status == "CONFLATION");
        True(engine.PairEvaluations.Single().Score.BirthdayYearConflict);
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
        var decision = decisions.Single(x => x.Status == "MATCH");
        var score = engine.PairEvaluations.Single().Score;
        True(score.BirthdayConflict);
        True(!score.BirthdayYearConflict);
        True(score.HardIdentifierMatch);
        True(score.Score >= 0.75);
        Equal("CROSS_PROVIDER_IDENTITY", decision.Action);
        True(decision.Evidence.Any(x => x.SignalType == "BIRTHDAY" && x.Verdict == "informational" && x.Narrative.Contains("same birth year")));
        True(decision.Evidence.Any(x => x.SignalType == "BIRTHDAY" && x.Metric.Contains("1925-02-04") && x.Metric.Contains("1925-06-04")));
        True(!decisions.Any(x => x.Status == "SPLIT"));
    }

    private static void KyleHebertMediaDominanceOutweighsTvdbConflicts()
    {
        var tmdb = Person(ProviderNames.Tmdb, "114061", "Kyle Hebert", ProviderNames.Imdb, "nm1035500", "alice");
        tmdb.ExternalIds[ProviderNames.Wikidata] = "Q1750744";
        tmdb.Birthday = "1969-06-14";
        var tvdb = Person(ProviderNames.Tvdb, "505422", "Kyle Hebert", ProviderNames.Tmdb, "571225", "alice");
        tvdb.Birthday = "1969-07-14";
        AddObservedCredit(tmdb, "alice", "Actor", "Bayard Hamar (Young) (voice)");
        AddObservedCredit(tvdb, "alice", "Actor", "Young Bayard (voice)");
        var input = BaseInput(tmdb, tvdb);
        input.ProviderCredits.AddRange(tmdb.Credits.Concat(tvdb.Credits));
        input.LocalPeople.Add(new LocalPerson { EmbyId = 141817, Name = "Kyle Hebert", TmdbId = "114061", TvdbId = "505422", ImdbId = "nm1035500" });
        input.Media.Add(Media(297178, "Alice Through the Looking Glass")); input.LocalCredits.Add(Credit(141817, 297178));
        var engine = new ResolutionEngine();
        var decisions = engine.Resolve(input, new ResolutionSettings());
        var decision = decisions.Single(x => x.Status == "MATCH_WITH_CONFLICT");
        var score = engine.PairEvaluations.Single().Score;
        True(score.MediaAttributionDominant);
        True(score.IdentifierConflict && score.BirthdayConflict);
        True(!score.BirthdayYearConflict);
        True(score.PositiveEvidenceScore >= 0.75);
        Equal(0.15, score.MetadataConflictPenalty);
        True(score.Score < 0.75);
        Equal("automatic", engine.PairEvaluations.Single().Disposition);
        True(decision.Evidence.Any(x => x.SignalType == "MEDIA_ATTRIBUTION_DOMINANCE" && x.Verdict == "supports"));
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
        var leftOne = Person(ProviderNames.Tmdb, "same-provider-1", "Collision One", ProviderNames.Imdb, "nm-collision", "shared");
        var leftTwo = Person(ProviderNames.Tmdb, "same-provider-2", "Collision Two", ProviderNames.Wikidata, "Q-collision", "shared");
        var right = Person(ProviderNames.Tvdb, "same-provider-right", "Bridge Profile", ProviderNames.Imdb, "nm-collision", "shared");
        right.ExternalIds[ProviderNames.Wikidata] = "Q-collision";
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
        var input = BaseInput(person); input.ProviderCredits.AddRange(person.Credits); input.LocalPeople.Add(new LocalPerson { EmbyId = 41636, Name = "Daniel Newman", TvdbId = "331984", ImdbId = "nm1649096" });
        var corrections = new[]
        {
            new ProviderCorrection { CorrectionId = 3, Kind = CorrectionKinds.PersonField, Operation = CorrectionOperations.Replace, Provider = ProviderNames.Tvdb, ProviderPersonId = "331984", FieldName = "birthday", CurrentValue = "1981-06-14", ReplacementValue = "1976-05-12", Reason = "PROVIDER_MISMATCH" },
            new ProviderCorrection { CorrectionId = 4, Kind = CorrectionKinds.PersonExternalId, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, ProviderPersonId = "331984", FieldName = ProviderNames.Imdb, CurrentValue = "nm1649096", Reason = "PROVIDER_MISMATCH" },
            new ProviderCorrection { CorrectionId = 5, Kind = CorrectionKinds.LocalPersonBinding, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, EmbyId = 41636, CurrentValue = "331984", Reason = "PROVIDER_MISMATCH" },
            new ProviderCorrection { CorrectionId = 6, Kind = CorrectionKinds.LocalPersonBinding, Operation = CorrectionOperations.Replace, Provider = ProviderNames.Imdb, EmbyId = 41636, CurrentValue = "nm1649096", ReplacementValue = "nm0000001", Reason = "PROVIDER_MISMATCH" }
        };
        ProviderCorrectionOverlay.Apply(input, new CorrectionApplicationTracker(corrections));
        Equal("1976-05-12", input.ProviderPeople.Single().Birthday);
        True(!input.ProviderPeople.Single().ExternalIds.ContainsKey(ProviderNames.Imdb));
        True(input.LocalPeople.Single().TvdbId == null);
        Equal("nm0000001", input.LocalPeople.Single().ImdbId);
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

    private static void ChangePlannerScopesMerge()
    {
        var context = new DecisionChangeContext
        {
            Decision = new ResolutionDecision { DecisionId = "merge", Status = "MATCH", Action = "AUTO_MERGE_SHADOW", DisplayName = "Example", AnchorEmbyPersonId = 10, ProviderKeys = "tmdb:100, tvdb:200", Headline = "Merge the shadow." },
            LocalPeople = new List<LocalPerson>
            {
                new LocalPerson { EmbyId = 10, Name = "Example", TmdbId = "100" },
                new LocalPerson { EmbyId = 11, Name = "Example", TvdbId = "200" }
            },
            LocalCredits = new List<LocalCredit> { new LocalCredit { PersonEmbyId = 11, MediaEmbyId = 20, Role = "Actor: Lead" } },
            CreditAssignments = new List<ResolutionCreditAssignment> { new ResolutionCreditAssignment { SourcePersonEmbyId = 11, TargetPersonEmbyId = 10, MediaEmbyId = 20, Role = "Actor: Lead", Disposition = "MOVE", ComponentKey = "tmdb:100, tvdb:200", Rationale = "Persisted test assignment." } }
        };
        var plan = DecisionChangePlanner.Build(context);
        Equal(3, plan.Changes.Count);
        True(plan.Changes.Any(x => x.Kind == EmbyChangeKinds.SetPersonProviderId && x.SourcePersonId == 10 && x.Provider == ProviderNames.Tvdb && x.ProposedValue == "200"));
        True(plan.Changes.Any(x => x.Kind == EmbyChangeKinds.RemovePersonProviderId && x.SourcePersonId == 11 && x.Provider == ProviderNames.Tvdb && x.CurrentValue == "200"));
        True(plan.Changes.Any(x => x.Kind == EmbyChangeKinds.MoveCredit && x.SourcePersonId == 11 && x.TargetPersonId == 10 && x.MediaId == 20));
    }

    private static void ChangePlannerLabelsAlignedMatch()
    {
        var context = new DecisionChangeContext
        {
            Decision = new ResolutionDecision { DecisionId = "match", Status = "MATCH", Action = "CROSS_PROVIDER_IDENTITY", DisplayName = "Tom Taylor", AnchorEmbyPersonId = 118815, ProviderKeys = "tmdb:1696753, tvdb:431476", Headline = "Two provider profiles resolve to one identity." },
            LocalPeople = new List<LocalPerson> { new LocalPerson { EmbyId = 118815, Name = "Tom Taylor", TmdbId = "1696753", TvdbId = "431476", ImdbId = "nm6999211" } },
            ProposedProviderPeople = new List<ProviderPerson>
            {
                Person(ProviderNames.Tmdb, "1696753", "Tom Taylor", ProviderNames.Imdb, "nm6999211", "us"),
                Person(ProviderNames.Tvdb, "431476", "Tom Taylor", null, null, "us")
            }
        };
        var plan = DecisionChangePlanner.Build(context);
        Equal(0, plan.Changes.Count);
        Equal("Emby is already aligned; no changes are needed", plan.NoChangeSummary);
        True(plan.NoChangeExplanation.Contains("No update or operator action is required."));
        context.LocalPeople[0].TmdbId = "unexpected-live-value";
        var changedSinceRun = DecisionChangePlanner.Build(context);
        Equal(0, changedSinceRun.Changes.Count);
        True(changedSinceRun.NoChangeSummary == null);
    }

    private static void ChangePlannerExposesManualDrift()
    {
        var decision = new ResolutionDecision { DecisionId = "drift", Status = "DRIFT", Action = "HUMAN_REVIEW", DisplayName = "Example", AnchorEmbyPersonId = 10, ProviderKeys = "tmdb:new", Headline = "Replace a stale ID." };
        var context = new DecisionChangeContext { Decision = decision, LocalPeople = new List<LocalPerson> { new LocalPerson { EmbyId = 10, TmdbId = "old" } } };
        var manual = DecisionChangePlanner.Build(context);
        Equal(1, manual.Changes.Count);
        True(manual.Changes[0].ManualReviewOnly);
        context.Acquisitions.Add(Acquisition(ProviderNames.Tmdb, "old", AcquisitionStates.Absent));
        decision.Action = "RETAINED_BY_MASS_ID_DRIFT";
        var plan = DecisionChangePlanner.Build(context);
        Equal(1, plan.Changes.Count);
        Equal("new", plan.Changes[0].ProposedValue);
        True(!plan.Changes[0].ManualReviewOnly);
        Equal(CorrectionKinds.LocalPersonBinding, plan.RecommendedCorrection.Kind);
    }

    private static void ChangePlannerScopesStaleRemoval()
    {
        var context = new DecisionChangeContext
        {
            Decision = new ResolutionDecision { DecisionId = "orphan", Status = "ORPHAN", Action = "REVIEW_REMOVE_STALE_PROVIDER_ID", DisplayName = "Example", AnchorEmbyPersonId = 10, ProviderKeys = "No hydrated provider identity", Headline = "Remove confirmed stale bindings." },
            LocalPeople = new List<LocalPerson> { new LocalPerson { EmbyId = 10, TmdbId = "missing", TvdbId = "present" } },
            Acquisitions = new List<PersonAcquisition> { Acquisition(ProviderNames.Tmdb, "missing", AcquisitionStates.Absent), Acquisition(ProviderNames.Tvdb, "present", AcquisitionStates.Present) }
        };
        var plan = DecisionChangePlanner.Build(context);
        Equal(1, plan.Changes.Count);
        Equal(ProviderNames.Tmdb, plan.Changes[0].Provider);
        Equal(EmbyChangeKinds.RemovePersonProviderId, plan.Changes[0].Kind);
    }

    private static void ChangePlannerCarriesExternalIdentity()
    {
        var proposed = new ProviderPerson { Provider = ProviderNames.Tmdb, ProviderId = "3844231", Name = "Samantha Kelly" };
        proposed.ExternalIds[ProviderNames.Imdb] = "nm2841197";
        var context = new DecisionChangeContext
        {
            Decision = new ResolutionDecision { DecisionId = "samantha", Status = "DRIFT", Action = "HUMAN_REVIEW", DisplayName = "Samantha Kelly", AnchorEmbyPersonId = 402910, ProviderKeys = "tmdb:3844231", Headline = "Media-backed identity drift." },
            LocalPeople = new List<LocalPerson> { new LocalPerson { EmbyId = 402910, TmdbId = "3210679", ImdbId = "nm0446845" } },
            ProposedProviderPeople = new List<ProviderPerson> { proposed },
            Acquisitions = new List<PersonAcquisition> { Acquisition(ProviderNames.Tmdb, "3210679", AcquisitionStates.Present) }
        };
        var plan = DecisionChangePlanner.Build(context);
        Equal(2, plan.Changes.Count);
        True(plan.Changes.Any(x => x.Provider == ProviderNames.Tmdb && x.CurrentValue == "3210679" && x.ProposedValue == "3844231"));
        True(plan.Changes.Any(x => x.Provider == ProviderNames.Imdb && x.CurrentValue == "nm0446845" && x.ProposedValue == "nm2841197"));
        True(plan.Changes.All(x => x.ManualReviewOnly));
    }

    private static void OutOfScopeProviderOwnerWithholdsDrift()
    {
        var provider = Person(ProviderNames.Tmdb, "3844231", "Samantha Kelly", ProviderNames.Imdb, "nm2841197", "tmdb:movie:284293");
        var input = BaseInput(provider);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 402910, Name = "Samantha Kelly", TmdbId = "3210679", ImdbId = "nm0446845" });
        input.GlobalLocalPeople.AddRange(new[]
        {
            new LocalPerson { EmbyId = 402910, Name = "Samantha Kelly", TmdbId = "3210679", ImdbId = "nm0446845" },
            new LocalPerson { EmbyId = 402058, Name = "Samantha Kelly", TmdbId = "3844231", ImdbId = "nm2841197" }
        });
        input.Media.Add(Media(284293, "Still Alice"));
        input.LocalCredits.Add(Credit(402910, 284293));

        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "DRIFT");
        Equal(ResolutionActions.IncompleteScope, decision.Action);
        True(decision.Evidence.Any(x => x.SignalType == "GLOBAL_BINDING_OWNER" && x.Narrative.Contains("402058")));
        var plan = DecisionChangePlanner.Build(new DecisionChangeContext { Decision = decision, LocalPeople = input.LocalPeople, GlobalLocalPeople = input.GlobalLocalPeople, ProposedProviderPeople = input.ProviderPeople });
        Equal(0, plan.Changes.Count);
        decision.Action = "HUMAN_REVIEW";
        var defensivePlan = DecisionChangePlanner.Build(new DecisionChangeContext { Decision = decision, LocalPeople = input.LocalPeople, GlobalLocalPeople = input.GlobalLocalPeople, ProposedProviderPeople = input.ProviderPeople });
        Equal(0, defensivePlan.Changes.Count);
    }

    private static void InScopeProviderOwnerParticipatesInMerge()
    {
        var provider = Person(ProviderNames.Tmdb, "3844231", "Samantha Kelly", ProviderNames.Imdb, "nm2841197", "tmdb:movie:284293", "tmdb:movie:157849");
        var input = BaseInput(provider);
        input.LocalPeople.AddRange(new[]
        {
            new LocalPerson { EmbyId = 402910, Name = "Samantha Kelly", TmdbId = "3210679", ImdbId = "nm0446845" },
            new LocalPerson { EmbyId = 402058, Name = "Samantha Kelly", TmdbId = "3844231", ImdbId = "nm2841197" }
        });
        input.GlobalLocalPeople.AddRange(input.LocalPeople);
        input.Media.AddRange(new[] { Media(284293, "Still Alice"), Media(157849, "Before I Go to Sleep") });
        input.LocalCredits.AddRange(new[] { Credit(402910, 284293), Credit(402058, 157849) });

        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Action == "AUTO_MERGE_SHADOW");
        Equal("MERGE", decision.Status);
        Equal(402058L, decision.AnchorEmbyPersonId.Value);
        True(!decision.Evidence.Any(x => x.SignalType == "GLOBAL_BINDING_OWNER"));
    }

    private static void SamanthaKellyCreditsRealignExactly()
    {
        var input = SamanthaKellyInput();
        var engine = new ResolutionEngine();
        var decision = engine.Resolve(input, new ResolutionSettings()).Single(x => x.Status == "REALIGNMENT");
        Equal(ResolutionActions.AutoRealignCredits, decision.Action);
        Equal(1, decision.CreditAssignments.Count(x => x.Disposition == "MOVE"));
        var move = decision.CreditAssignments.Single(x => x.Disposition == "MOVE");
        Equal(402910L, move.SourcePersonEmbyId);
        Equal(402058L, move.TargetPersonEmbyId);
        Equal(296586L, move.MediaEmbyId);
        Equal(1, decision.ImpactedMediaCount);
        Equal("Still Alice", decision.ImpactedMedia.Single().DisplayName);

        var context = new DecisionChangeContext { Decision = decision, LocalPeople = input.LocalPeople, GlobalLocalPeople = input.GlobalLocalPeople, LocalCredits = input.LocalCredits, ProposedProviderPeople = input.ProviderPeople, CreditAssignments = decision.CreditAssignments };
        var plan = DecisionChangePlanner.Build(context);
        Equal(1, plan.Changes.Count);
        Equal(EmbyChangeKinds.MoveCredit, plan.Changes[0].Kind);
        True(!plan.Changes.Any(x => x.Kind == EmbyChangeKinds.SetPersonProviderId || x.Kind == EmbyChangeKinds.RemovePersonProviderId));

        input.LocalCredits.RemoveAll(x => x.PersonEmbyId == 402910 && x.MediaEmbyId == 296586);
        input.LocalCredits.Add(new LocalCredit { PersonEmbyId = 402058, MediaEmbyId = 296586, Role = "Actor: TV Reporter (uncredited)" });
        True(!new ResolutionEngine().Resolve(input, new ResolutionSettings()).Any(x => x.DisplayName.Contains("Samantha Kelly")));
    }

    private static void SamanthaKellyAmbiguityWithholdsMutation()
    {
        var input = SamanthaKellyInput();
        input.LocalCredits.Single(x => x.MediaEmbyId == 296586).Role = "Director";
        var decision = new ResolutionEngine().Resolve(input, new ResolutionSettings()).Single(x => x.Status == "REALIGNMENT");
        Equal("HUMAN_REVIEW", decision.Action);
        True(decision.Evidence.Any(x => x.SignalType == "LOCAL_RECONCILIATION" && x.Metric.Contains("ambiguous_credits=1")));
        var context = new DecisionChangeContext { Decision = decision, LocalPeople = input.LocalPeople, GlobalLocalPeople = input.GlobalLocalPeople, LocalCredits = input.LocalCredits, ProposedProviderPeople = input.ProviderPeople, CreditAssignments = decision.CreditAssignments };
        Equal(0, DecisionChangePlanner.Build(context).Changes.Count);
    }

    private static ResolutionInput SamanthaKellyInput()
    {
        var componentA = Person(ProviderNames.Tmdb, "3844231", "Samantha Kelly", ProviderNames.Imdb, "nm2841197", "tmdb:movie:204922", "tmdb:movie:284293");
        componentA.ExternalIds[ProviderNames.Wikidata] = "Q19819860";
        AddObservedCredit(componentA, "tmdb:movie:204922", "Actor", "Nurse with Austrian Accent (uncredited)");
        AddObservedCredit(componentA, "tmdb:movie:284293", "Actor", "TV Reporter (uncredited)");
        var componentB = Person(ProviderNames.Tmdb, "3210679", "Samantha Kelly", ProviderNames.Imdb, "nm0446845", "tmdb:movie:277216");
        AddObservedCredit(componentB, "tmdb:movie:277216", "Actor", "Pendleton's Girl (uncredited)");
        var input = BaseInput(componentA, componentB);
        input.ProviderCredits.AddRange(componentA.Credits.Concat(componentB.Credits));
        input.LocalPeople.AddRange(new[]
        {
            new LocalPerson { EmbyId = 402058, Name = "Samantha Kelly", TmdbId = "3844231", ImdbId = "nm2841197" },
            new LocalPerson { EmbyId = 402910, Name = "Samantha Kelly", TmdbId = "3210679", ImdbId = "nm0446845" }
        });
        input.GlobalLocalPeople.AddRange(input.LocalPeople);
        input.Media.AddRange(new[]
        {
            new MediaSeed { EmbyId = 296228, MediaType = MediaTypes.Movie, Name = "Before I Go to Sleep", TmdbId = "204922" },
            new MediaSeed { EmbyId = 296586, MediaType = MediaTypes.Movie, Name = "Still Alice", TmdbId = "284293" },
            new MediaSeed { EmbyId = 299100, MediaType = MediaTypes.Movie, Name = "Straight Outta Compton", TmdbId = "277216" }
        });
        input.LocalCredits.AddRange(new[]
        {
            new LocalCredit { PersonEmbyId = 402058, MediaEmbyId = 296228, Role = "Actor: Nurse with Austrian Accent (uncredited)" },
            new LocalCredit { PersonEmbyId = 402910, MediaEmbyId = 296586, Role = "Actor: TV Reporter (uncredited)" },
            new LocalCredit { PersonEmbyId = 402910, MediaEmbyId = 299100, Role = "Actor: Pendleton's Girl (uncredited)" }
        });

        return input;
    }

    private static void ChangePlannerExposesOrphanRemoval()
    {
        var context = new DecisionChangeContext
        {
            Decision = new ResolutionDecision { DecisionId = "orphan-review", Status = "ORPHAN", Action = "HUMAN_REVIEW", DisplayName = "Example", AnchorEmbyPersonId = 10, ProviderKeys = "No hydrated provider identity", Headline = "No media support." },
            LocalPeople = new List<LocalPerson> { new LocalPerson { EmbyId = 10, TmdbId = "present" } },
            Acquisitions = new List<PersonAcquisition> { Acquisition(ProviderNames.Tmdb, "present", AcquisitionStates.Present) }
        };
        var plan = DecisionChangePlanner.Build(context);
        Equal(1, plan.Changes.Count);
        True(plan.Changes[0].ManualReviewOnly);
        Equal(EmbyChangeKinds.RemovePersonProviderId, plan.Changes[0].Kind);
        Equal(CorrectionKinds.LocalPersonBinding, plan.RecommendedCorrection.Kind);
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

    private static void ResolutionReportsProgress()
    {
        var reports = new List<ResolutionProgress>();
        new ResolutionEngine().Resolve(new ResolutionInput(), new ResolutionSettings(), reports.Add, CancellationToken.None);
        True(reports.Count > 1);
        Equal("Preparing provider-credit index", reports.First().Stage);
        Equal("Offline resolution complete", reports.Last().Stage);
        Equal(1.0, reports.Last().Fraction);
    }

    private static void ResolutionObservesCancellation()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            new ResolutionEngine().Resolve(new ResolutionInput(), new ResolutionSettings(), null, cancellation.Token);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ReviewCasesGroupConnectedRelationships()
    {
        var correction = "provider=TVDB;media_type=Movie;media_id=351731;old_person_id=8302951;new_person_id=457122";
        var rows = new[]
        {
            DashboardRow("alex-1", "Alex Jennings", "TMDB:15739, TVDB:457122", correction, 351731),
            DashboardRow("alex-2", "Alex Jennings", "TMDB:15739, TVDB:8302951", correction, 351731)
        };

        var reviewCase = DashboardCaseBuilder.Build(rows).Single();
        Equal(2, reviewCase.Relationships);
        Equal(3, reviewCase.ProviderRecords);
        Equal(2, reviewCase.UnderlyingDecisionIds.Length);
        Equal("Would auto-resolve", reviewCase.Automation);
        Equal("Suggested provider correction", reviewCase.Action);
        Equal("Provider attribution disagreement", reviewCase.Status);
        Equal(1, reviewCase.Details.Count(x => x.Section == "Affected titles"));
        True(reviewCase.Details.Any(x => x.Signal == "SUGGESTED_CORRECTION" && x.Verdict == "converged"));
    }

    private static void ReviewCaseAutomationRequiresConvergence()
    {
        var rows = new[]
        {
            DashboardRow("sandy-1", "Sandy Johnson", "TMDB:1211879, TVDB:296716"),
            DashboardRow("sandy-2", "Sandy Johnson", "TMDB:15518, TVDB:296716")
        };

        var reviewCase = DashboardCaseBuilder.Build(rows).Single();
        Equal(2, reviewCase.Relationships);
        Equal(3, reviewCase.ProviderRecords);
        Equal("Review required", reviewCase.Automation);
        True(reviewCase.AutomationReason.Contains("one exact, safe correction"));
    }

    private static void ReviewCaseAutomationRespectsScope()
    {
        var row = DashboardRow("paul-1", "Paul Simon", "TMDB:100, TVDB:200", "provider=TVDB;media_type=Movie;media_id=1;old_person_id=300;new_person_id=200");
        row.Action = "INCOMPLETE_SCOPE";
        row.Details = row.Details.Concat(new[] { new DashboardDetail { DetailId = "owner", Signal = "GLOBAL_BINDING_OWNER", Verdict = "blocked", Explanation = "Another Emby person owns the provider ID." } }).ToArray();

        var reviewCase = DashboardCaseBuilder.Build(new[] { row }).Single();
        Equal("Blocked", reviewCase.Automation);
        Equal("Blocked — incomplete scope", reviewCase.Action);
    }

    private static void ReviewCasesDoNotGroupByName()
    {
        var rows = new[]
        {
            DashboardRow("same-name-1", "Alex Example", "TMDB:1, TVDB:2"),
            DashboardRow("same-name-2", "Alex Example", "TMDB:3, TVDB:4")
        };

        Equal(2, DashboardCaseBuilder.Build(rows).Length);
    }

    private static void HolisticLilyPlan()
    {
        var input = new ResolutionInput();
        var oldTmdb = Person(ProviderNames.Tmdb, "12539", "Lily Knight", ProviderNames.Imdb, "nm0460990");
        var oldTvdb = Person(ProviderNames.Tvdb, "252656", "Lily Knight", ProviderNames.Tmdb, "12539");
        var newTmdb = Person(ProviderNames.Tmdb, "2548051", "Lily Knight", ProviderNames.Imdb, "nm8424509");
        var newTvdb = Person(ProviderNames.Tvdb, "9096777", "Lily Knight", ProviderNames.Imdb, "nm8424509");
        input.ProviderPeople.AddRange(new[] { oldTmdb, oldTvdb, newTmdb, newTvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 13932, Name = "Lily Knight", TmdbId = "12539", TvdbId = "252656", ImdbId = "nm0460990" });
        AddPlanMedia(input, 1, "A.I.", "644", "1062", "Actor: Voice", oldTmdb, oldTvdb);
        AddPlanMedia(input, 2, "Secretary", "11013", null, "Actor: Paralegal", oldTmdb);
        AddPlanMedia(input, 3, "The Artist", "74643", null, "Actor: Nurse", oldTmdb);
        AddPlanMedia(input, 4, "Saint Maud", "575776", "99291", "Actor: Joy", newTmdb, newTvdb);
        AddPlanMedia(input, 5, "Their Finest", "340101", "6563", "Actor: Rose", newTmdb, newTvdb);
        var decision = new ResolutionDecision { DecisionId = "lily-split", Status = "SPLIT", Action = "FORCE_SPLIT_REVIEW", DisplayName = "Lily Knight", AnchorEmbyPersonId = 13932, ProviderKeys = "tmdb:12539,tvdb:252656,tmdb:2548051,tvdb:9096777" };
        var clusters = new[] { Cluster("old", 13932, "tmdb:12539", "tvdb:252656"), Cluster("new", 13932, "tmdb:2548051", "tvdb:9096777") };
        var plan = IdentityCasePlanner.Build(1, input, new[] { decision }, clusters).Single();
        Equal(IdentityPlanStates.Complete, plan.State);
        Equal(2, plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.New || x.TargetKind == IdentityTargetKinds.Existing));
        Equal(1, plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.New));
        Equal(2, plan.Credits.Count(x => x.Disposition == "MOVE"));
        True(plan.ApplyCaption.Contains("create 1 person") && plan.ApplyCaption.Contains("move 2 credits"));
    }

    private static void HolisticDonnaPlan()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "1996180", "Donna Ewin", null, null);
        var tvdb = Person(ProviderNames.Tvdb, "259110", "Donna Ewin", null, null);
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 63839, Name = "Donna Ewin", TmdbId = "1996180", TvdbId = "259110" });
        AddPlanMedia(input, 10, "Eyes Wide Shut", "345", null, "Actor: Principal", tmdb);
        AddPlanMedia(input, 11, "The Fast Show", null, "70415", "Actor", tvdb);
        var decision = new ResolutionDecision { DecisionId = "donna-split", Status = "SPLIT", Action = "FORCE_SPLIT_REVIEW", DisplayName = "Donna Ewin", AnchorEmbyPersonId = 63839, ProviderKeys = "tmdb:1996180,tvdb:259110" };
        var plan = IdentityCasePlanner.Build(2, input, new[] { decision }, new[] { Cluster("tmdb", 63839, "tmdb:1996180"), Cluster("tvdb", 63839, "tvdb:259110") }).Single();
        Equal(IdentityPlanStates.Complete, plan.State);
        Equal(1, plan.Outcomes.Count);
        Equal(0, plan.Credits.Count(x => x.Disposition == "MOVE"));
        True(plan.Warning.Contains("no counter-evidence"));
    }

    private static void HolisticDriftReusesAnchor()
    {
        var input = new ResolutionInput();
        var replacement = Person(ProviderNames.Tmdb, "1171527", "Alexander Terentyev", ProviderNames.Imdb, "nm2765435");
        input.ProviderPeople.Add(replacement);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 127919, Name = "Alexander Terentyev", TmdbId = "3876434" });
        AddPlanMedia(input, 297633, "Wonder Woman", "297762", null, "Actor: German Lieutenant", replacement);
        var decision = new ResolutionDecision { DecisionId = "drift", Status = "DRIFT", Action = "RETAINED_BY_MASS_ID_DRIFT", DisplayName = "Alexander Terentyev", AnchorEmbyPersonId = 127919, ProviderKeys = "tmdb:1171527" };

        var plan = IdentityCasePlanner.Build(4, input, new[] { decision }, new[] { Cluster("replacement", 127919, "tmdb:1171527") }).Single();

        Equal(IdentityTargetKinds.Existing, plan.Outcomes.Single().TargetKind);
        Equal(127919L, plan.Outcomes.Single().TargetEmbyId.Value);
        Equal("KEEP", plan.Credits.Single().Disposition);
        True(!plan.ApplyCaption.Contains("create") && !plan.ApplyCaption.Contains("move") && plan.ApplyCaption.Contains("change 2 IDs"));
    }

    private static void HolisticOrphanPreservesIds()
    {
        var input = new ResolutionInput();
        input.LocalPeople.Add(new LocalPerson { EmbyId = 44939, Name = "Roy Beck", TmdbId = "1809473", ImdbId = "nm8745734" });
        input.Media.Add(new MediaSeed { EmbyId = 294293, MediaType = MediaTypes.Movie, Name = "Indiana Jones and the Last Crusade", TmdbId = "89" });
        input.LocalCredits.Add(new LocalCredit { PersonEmbyId = 44939, MediaEmbyId = 294293, Role = "Actor: German Customs Official" });
        var decision = new ResolutionDecision { DecisionId = "orphan", Status = "ORPHAN", Action = "HUMAN_REVIEW", DisplayName = "Roy Beck", AnchorEmbyPersonId = 44939, ProviderKeys = "No hydrated provider identity" };

        var plan = IdentityCasePlanner.Build(5, input, new[] { decision }, new ResolutionClusterSnapshot[0]).Single();
        var outcome = plan.Outcomes.Single();

        Equal(IdentityTargetKinds.Existing, outcome.TargetKind);
        Equal("1809473", outcome.ProviderIds.Single(x => x.Provider == ProviderNames.Tmdb).ProviderId);
        Equal("nm8745734", outcome.ProviderIds.Single(x => x.Provider == ProviderNames.Imdb).ProviderId);
        Equal("No Emby changes required", plan.ApplyCaption);
    }

    private static void HolisticExistingNameIsHonest()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "90658", "Kristin Bauer", ProviderNames.Imdb, "nm0061877", "shared");
        var tvdb = Person(ProviderNames.Tvdb, "346553", "Kristin Bauer van Straten", ProviderNames.Imdb, "nm0061877", "shared");
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 16398, Name = "Kristin Bauer van Straten", TmdbId = "90658", TvdbId = "346553", ImdbId = "nm0061877" });
        AddPlanMedia(input, 81382, "True Blood", "1399", "82283", "Actor: Pam De Beaufort", tmdb, tvdb);
        var decision = new ResolutionDecision { DecisionId = "match", Status = "MATCH", Action = "CROSS_PROVIDER_IDENTITY", DisplayName = "Kristin Bauer van Straten", AnchorEmbyPersonId = 16398, ProviderKeys = "tmdb:90658,tvdb:346553" };

        var plan = IdentityCasePlanner.Build(6, input, new[] { decision }, new[] { Cluster("identity", 16398, "tmdb:90658", "tvdb:346553") }).Single();

        Equal("Kristin Bauer van Straten", plan.Outcomes.Single().DisplayName);
    }

    private static void HolisticBirthdayConflictWarning()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "1576541", "Tomiwa Edun", ProviderNames.Imdb, "nm3643989", "shared"); tmdb.Birthday = "1984-01-01";
        var tvdb = Person(ProviderNames.Tvdb, "7891155", "Tomiwa Edun", ProviderNames.Imdb, "nm3643989", "shared"); tvdb.Birthday = "1984-03-04";
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 38308, Name = "Tomiwa Edun", TmdbId = "1576541", TvdbId = "7891155", ImdbId = "nm3643989" });
        AddPlanMedia(input, 66626, "Merlin", "7225", "83123", "Actor: Sir Elyan", tmdb, tvdb);
        var decision = new ResolutionDecision { DecisionId = "match", Status = "MATCH", Action = "CROSS_PROVIDER_IDENTITY", DisplayName = "Tomiwa Edun", AnchorEmbyPersonId = 38308, ProviderKeys = "tmdb:1576541,tvdb:7891155" };
        decision.Evidence.Add(new EvidenceLine { SignalType = "BIRTHDAY", Verdict = "informational", Narrative = "Both providers supplied different month/day values in the same birth year (tmdb:1984-01-01;tvdb:1984-03-04)." });

        var plan = IdentityCasePlanner.Build(7, input, new[] { decision }, new[] { Cluster("identity", 38308, "tmdb:1576541", "tvdb:7891155") }).Single();

        True(plan.Warning.Contains("Informational metadata warning") && plan.Warning.Contains("1984-01-01") && plan.Warning.Contains("1984-03-04"));
    }

    private static void HolisticProviderAgreementShowsPendingEmbyAlignment()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "238303", "Tom Hanson", ProviderNames.Imdb, "nm6596413", "shared");
        var tvdb = Person(ProviderNames.Tvdb, "424547", "Tom Hanson", ProviderNames.Imdb, "nm6596413", "shared");
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 36092, Name = "Tom Hanson", TmdbId = "238303", TvdbId = "424547" });
        AddPlanMedia(input, 25295, "Brassic", "64513", "361537", "Actor: Cardi", tmdb, tvdb);
        var decision = new ResolutionDecision { DecisionId = "match", Status = "MATCH", Action = "CROSS_PROVIDER_IDENTITY", DisplayName = "Tom Hanson", AnchorEmbyPersonId = 36092, ProviderKeys = "tmdb:238303,tvdb:424547" };

        var plan = IdentityCasePlanner.Build(8, input, new[] { decision }, new[] { Cluster("identity", 36092, "tmdb:238303", "tvdb:424547") }).Single();

        True(plan.ApplyCaption.Contains("change 1 ID"));
        True(plan.Warning.Contains("provider records agree") && plan.Warning.Contains("current Emby person still differs"));
    }

    private static void HolisticAmbiguousMediaQuestion()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "1", "Alex Example", ProviderNames.Imdb, "nm1");
        var tvdb = Person(ProviderNames.Tvdb, "2", "Alex Example", ProviderNames.Imdb, "nm2");
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 50, Name = "Alex Example", TmdbId = "1" });
        AddPlanMedia(input, 20, "Disputed title", "20", "200", "Actor: Lead", tmdb, tvdb);
        var decision = new ResolutionDecision { DecisionId = "alex-split", Status = "SPLIT", Action = "FORCE_SPLIT_REVIEW", DisplayName = "Alex Example", AnchorEmbyPersonId = 50, ProviderKeys = "tmdb:1,tvdb:2" };
        var plan = IdentityCasePlanner.Build(3, input, new[] { decision }, new[] { Cluster("one", 50, "tmdb:1"), Cluster("two", 50, "tvdb:2") }).Single();
        Equal(IdentityPlanStates.CorrectionRequired, plan.State);
        True(plan.Questions.Any(x => x.Kind == CorrectionKinds.LocalCreditTarget && x.Choices.Count == 2));
        True(plan.Credits.Single().CorrectionRequired);
    }

    private static void HolisticCrosswalkConflictDoesNotDisputeCredit()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "216444", "Michael Rogers", ProviderNames.Imdb, "nm0737089");
        var tvdb = Person(ProviderNames.Tvdb, "271293", "Michael Rogers", ProviderNames.Tmdb, "134708");
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 178672, Name = "Michael Rogers", TmdbId = "216444", TvdbId = "271293" });
        AddPlanMedia(input, 299048, "The Mosquito Coast", "11120", "6697", "Actor: Francis Lungley", tmdb, tvdb);
        var decision = new ResolutionDecision { DecisionId = "michael-conflict", Status = "MATCH_WITH_CONFLICT", Action = "CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT", DisplayName = "Michael Rogers", AnchorEmbyPersonId = 178672, ProviderKeys = "tmdb:216444,tvdb:271293" };
        var clusters = new[] { Cluster("michael", 178672, "tmdb:216444", "tvdb:271293") };

        var plan = IdentityCasePlanner.Build(12, input, new[] { decision }, clusters).Single();

        Equal(1, plan.Outcomes.Count);
        Equal(IdentityTargetKinds.Existing, plan.Outcomes.Single().TargetKind);
        Equal(178672L, plan.Outcomes.Single().TargetEmbyId.Value);
        Equal("216444", IdentityCasePlanner.PreferredProviderId(plan.Outcomes.Single(), ProviderNames.Tmdb));
        Equal("271293", IdentityCasePlanner.PreferredProviderId(plan.Outcomes.Single(), ProviderNames.Tvdb));
        Equal(IdentityPlanStates.Complete, plan.State);
        Equal(0, plan.Questions.Count);
        Equal("KEEP", plan.Credits.Single().Disposition);
        True(!plan.Credits.Single().CorrectionRequired);
        Equal(2, plan.Credits.Single().Attributions.Count);
        True(plan.Credits.Single().Rationale.Contains("providers agree", StringComparison.OrdinalIgnoreCase));
        True(plan.Warning.Contains("TVDB person 271293 claims TMDB person 134708") && plan.Warning.Contains("native TMDB person is 216444"));
        True(plan.ApplyCaption.Contains("change 1 ID") && !plan.ApplyCaption.Contains("move") && !plan.ApplyCaption.Contains("create"));
    }

    private static void HolisticImdbConflictRetainsCurrentId()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "1846245", "Julie Cohen", ProviderNames.Imdb, "nm0169528");
        var tvdb = Person(ProviderNames.Tvdb, "436876", "Julie Cohen", ProviderNames.Imdb, "nm3792517");
        tvdb.ExternalIds[ProviderNames.Tmdb] = "196334";
        input.ProviderPeople.AddRange(new[] { tmdb, tvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 164032, Name = "Julie Cohen", TmdbId = "1846245", TvdbId = "436876", ImdbId = "nm0169528" });
        AddPlanMedia(input, 297300, "Once Upon a Time in America", "311", "873", "Actor: Young Peggy", tmdb, tvdb);
        var decision = new ResolutionDecision { DecisionId = "julie-conflict", Status = "MATCH_WITH_CONFLICT", Action = "CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT", DisplayName = "Julie Cohen", AnchorEmbyPersonId = 164032, ProviderKeys = "tmdb:1846245,tvdb:436876" };

        var plan = IdentityCasePlanner.Build(12, input, new[] { decision }, new[] { Cluster("julie", 164032, "tmdb:1846245", "tvdb:436876") }).Single();
        var outcome = plan.Outcomes.Single();
        var credit = plan.Credits.Single();

        Equal(IdentityTargetKinds.Existing, outcome.TargetKind);
        Equal("1846245", IdentityCasePlanner.PreferredProviderId(outcome, ProviderNames.Tmdb));
        Equal("436876", IdentityCasePlanner.PreferredProviderId(outcome, ProviderNames.Tvdb));
        Equal("nm0169528", IdentityCasePlanner.PreferredProviderId(outcome, ProviderNames.Imdb));
        Equal(IdentityPlanStates.Complete, plan.State);
        Equal(0, plan.Questions.Count);
        Equal("KEEP", credit.Disposition);
        Equal(2, credit.Attributions.Count);
        True(credit.Attributions.Any(x => x.Provider == ProviderNames.Tmdb && x.ProviderPersonId == "1846245" && x.Role == "Actor: Young Peggy"));
        True(credit.Attributions.Any(x => x.Provider == ProviderNames.Tvdb && x.ProviderPersonId == "436876" && x.Role == "Actor: Young Peggy"));
        True(plan.Warning.Contains("claims TMDB person 196334") && plan.Warning.Contains("claim different IMDb IDs") && plan.Warning.Contains("nm0169528 is retained"));
        Equal("No Emby changes required", plan.ApplyCaption);
    }

    private static void HolisticCurrentIdentitySubsetRemainsTogether()
    {
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "15739", "Alex Jennings", ProviderNames.Imdb, "nm0421105"); tmdb.Birthday = "1957-05-10";
        var currentTvdb = Person(ProviderNames.Tvdb, "457122", "Alex Jennings", ProviderNames.Tmdb, "15739"); currentTvdb.Birthday = "1957-05-10"; currentTvdb.ExternalIds[ProviderNames.Imdb] = "nm0421105";
        var alternativeTvdb = Person(ProviderNames.Tvdb, "8302951", "Alex Jennings", ProviderNames.Tmdb, "2276924"); alternativeTvdb.ExternalIds[ProviderNames.Imdb] = "nm4532245";
        input.ProviderPeople.AddRange(new[] { tmdb, currentTvdb, alternativeTvdb });
        input.LocalPeople.Add(new LocalPerson { EmbyId = 44676, Name = "Alex Jennings", TmdbId = "15739", TvdbId = "457122", ImdbId = "nm0421105" });
        AddPlanMedia(input, 1, "The Crown", "tmdb-crown", "tvdb-crown", "Actor: Edward VIII", tmdb, currentTvdb);
        AddPlanMedia(input, 2, "Your Christmas or Mine 2", "1176139", "351731", "Actor: Humphrey", tmdb, alternativeTvdb);
        AddPlanMedia(input, 3, "Your Christmas or Mine?", "865559", "340802", "Actor: Humphrey", tmdb);
        AddPlanMedia(input, 4, "The Phoenician Scheme", "1137350", "357577", "Actor: Broadcloth", tmdb);
        var decisions = new[]
        {
            new ResolutionDecision { DecisionId = "current-pair", Status = "CONFLATION", Action = "HUMAN_REVIEW", DisplayName = "Alex Jennings", AnchorEmbyPersonId = 44676, ProviderKeys = "tmdb:15739,tvdb:457122" },
            new ResolutionDecision { DecisionId = "alternative-pair", Status = "CONFLATION", Action = "HUMAN_REVIEW", DisplayName = "Alex Jennings", AnchorEmbyPersonId = 44676, ProviderKeys = "tmdb:15739,tvdb:8302951" }
        };
        var clusters = new[] { Cluster("current-tmdb", 44676, "tmdb:15739"), Cluster("current-tvdb", 44676, "tvdb:457122"), Cluster("alternative-tvdb", 44676, "tvdb:8302951") };

        var plan = IdentityCasePlanner.Build(9, input, decisions, clusters).Single();

        var current = plan.Outcomes.Single(x => x.TargetKind == IdentityTargetKinds.Existing);
        True(current.ProviderIds.Any(x => x.Provider == ProviderNames.Tmdb && x.ProviderId == "15739" && x.Source == "native"));
        True(current.ProviderIds.Any(x => x.Provider == ProviderNames.Tvdb && x.ProviderId == "457122" && x.Source == "native"));
        True(!plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.New && x.ProviderIds.Any(y => y.Provider == ProviderNames.Tmdb && y.ProviderId == "15739")));
        Equal(IdentityTargetKinds.New, plan.Outcomes.Single(x => x.ProviderIds.Any(y => y.Provider == ProviderNames.Tvdb && y.ProviderId == "8302951" && y.Source == "native")).TargetKind);
        Equal(1, plan.Credits.Count(x => x.CorrectionRequired));
        True(plan.Credits.Single(x => x.MediaName == "Your Christmas or Mine 2").CorrectionRequired);
        True(plan.Credits.Where(x => x.MediaName != "Your Christmas or Mine 2").All(x => x.Disposition == "KEEP" && !x.CorrectionRequired));
        Equal(1, plan.Questions.Count(x => x.Kind == CorrectionKinds.LocalCreditTarget));
        var providerChoice = plan.Questions.Single().Choices.Single(x => x.Caption.Contains("Emby 44676"));
        Equal(CorrectionKinds.MediaCredit, providerChoice.Correction.Kind);
        Equal(ProviderNames.Tvdb, providerChoice.Correction.Provider);
        Equal("351731", providerChoice.Correction.ProviderMediaId);
        Equal("8302951", providerChoice.Correction.ProviderPersonId);
        Equal("457122", providerChoice.Correction.ReplacementValue);
        True(plan.Warning.Contains("already bound to Emby person 44676"));

        var disputed = plan.Credits.Single(x => x.MediaName == "Your Christmas or Mine 2");
        input.ActiveCorrections.Add(new ProviderCorrection
        {
            Kind = CorrectionKinds.LocalCreditTarget, Operation = CorrectionOperations.Replace, EmbyId = disputed.MediaEmbyId,
            CurrentValue = disputed.SourcePersonEmbyId + "|" + disputed.Role, ReplacementValue = "existing:44676", Reason = "OPERATOR_MEDIA_ASSIGNMENT", Enabled = true
        });
        var locallyPinned = IdentityCasePlanner.Build(10, input, decisions, clusters).Single();
        Equal(1, locallyPinned.Questions.Count);
        Equal(IdentityPlanStates.CorrectionRequired, locallyPinned.State);
        Equal(CorrectionKinds.MediaCredit, locallyPinned.Questions.Single().Choices.Single(x => x.Caption.Contains("Emby 44676")).Correction.Kind);

        var providerCorrection = locallyPinned.Questions.Single().Choices.Single(x => x.Caption.Contains("Emby 44676")).Correction;
        var tracker = new CorrectionApplicationTracker(new[] { providerCorrection });
        ProviderCorrectionOverlay.Apply(input, tracker);
        Equal(1, tracker.Results.Single().ChangedCount);
        True(!input.ProviderCredits.Any(x => x.Provider == ProviderNames.Tvdb && x.ProviderMediaId == "351731" && x.ProviderPersonId == "8302951"));
        input.ProviderPeople.Add(alternativeTvdb); // Preserve a stale provider-only alternative to prove the planner prunes it.
        var providerCorrected = IdentityCasePlanner.Build(11, input, decisions, clusters).Single();
        Equal(0, providerCorrected.Questions.Count);
        Equal(IdentityPlanStates.Complete, providerCorrected.State);
        True(!providerCorrected.Outcomes.Any(x => x.ProviderIds.Any(y => y.Provider == ProviderNames.Tvdb && y.ProviderId == "8302951")));
        Equal("No Emby changes required", providerCorrected.ApplyCaption);
    }

    private static void HolisticPlannerRemainsBounded()
    {
        const int count = 1600;
        var input = new ResolutionInput(); var decisions = new List<ResolutionDecision>(); var clusters = new List<ResolutionClusterSnapshot>();
        for (var i = 1; i <= count; i++)
        {
            var providerId = i.ToString(); var embyId = 100000L + i;
            input.ProviderPeople.Add(Person(ProviderNames.Tmdb, providerId, "Person " + i, null, null));
            input.LocalPeople.Add(new LocalPerson { EmbyId = embyId, Name = "Person " + i, TmdbId = providerId });
            decisions.Add(new ResolutionDecision { DecisionId = "case-" + i, Status = "MATCH", Action = "CROSS_PROVIDER_IDENTITY", DisplayName = "Person " + i, AnchorEmbyPersonId = embyId, ProviderKeys = "tmdb:" + providerId });
            clusters.Add(Cluster("cluster-" + i, embyId, "tmdb:" + providerId));
        }
        var clock = Stopwatch.StartNew(); var plans = IdentityCasePlanner.Build(1, input, decisions, clusters); clock.Stop();
        Equal(count, plans.Count);
        True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    private static void PersonBuilderRecordsNoOpAdjudication()
    {
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        var compilation = IdentityCasePersonBuilder.Compile(plan, draft);

        Equal(IdentityPlanStates.Complete, compilation.Plan.State);
        Equal(0, compilation.EmbyChanges);
        Equal(1, compilation.Plan.Outcomes.Count);
        Equal("KEEP", compilation.Plan.Credits.Single().Disposition);
        Equal(0, compilation.Plan.Questions.Count);
        Equal(1, compilation.Corrections.Count);
        Equal(CorrectionKinds.MediaCredit, compilation.Corrections.Single().Kind);
        Equal(CorrectionOperations.Unusable, compilation.Corrections.Single().Operation);
        Equal(ProviderNames.Tvdb, compilation.Corrections.Single().Provider);
        Equal("No Emby changes required", compilation.Plan.ApplyCaption);
        True(ReviewCaseDialogUI.Build(plan, draft, "server", null).Apply != null);
    }

    private static void PersonBuilderSelectsProviderReplacement()
    {
        var plan = BuilderPlan();
        var existing = plan.Outcomes.Single(x => x.TargetKind == IdentityTargetKinds.Existing);
        var suggested = plan.Outcomes.Single(x => x.TargetKind == IdentityTargetKinds.New);
        existing.ProviderIds.Add(new IdentityProviderId { Provider = ProviderNames.Tvdb, ProviderId = "2", Source = "native" });
        suggested.ProviderIds = new List<IdentityProviderId> { new IdentityProviderId { Provider = ProviderNames.Tmdb, ProviderId = "3", Source = "native" }, new IdentityProviderId { Provider = ProviderNames.Imdb, ProviderId = "nm3", Source = "external" } };
        plan.Credits.Single().Attributions = new List<IdentityCreditAttribution>
        {
            new IdentityCreditAttribution { Provider = ProviderNames.Tmdb, ProviderMediaId = "20", ProviderPersonId = "3", Role = "Actor: Lead", RoleCategory = "Actor", OutcomeId = suggested.OutcomeId },
            new IdentityCreditAttribution { Provider = ProviderNames.Tvdb, ProviderMediaId = "200", ProviderPersonId = "2", Role = "Actor: Lead", RoleCategory = "Actor", OutcomeId = existing.OutcomeId }
        };
        var question = plan.Questions.Single();
        question.Choices = new List<IdentityQuestionChoice>
        {
            new IdentityQuestionChoice
            {
                ChoiceId = "question-credit-1:existing-replace",
                Correction = new ProviderCorrection { Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Replace, Provider = ProviderNames.Tmdb, MediaType = MediaTypes.Movie, ProviderMediaId = "20", ProviderPersonId = "3", CurrentValue = "Actor: Lead", ReplacementValue = "1", Reason = "OPERATOR_PROVIDER_ATTRIBUTION", Enabled = true }
            },
            new IdentityQuestionChoice
            {
                ChoiceId = "question-credit-1:new-unusable",
                Correction = new ProviderCorrection { Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, MediaType = MediaTypes.Movie, ProviderMediaId = "200", ProviderPersonId = "2", CurrentValue = "Actor: Lead", Reason = "OPERATOR_PROVIDER_ATTRIBUTION", Enabled = true }
            }
        };

        var compilation = IdentityCasePersonBuilder.Compile(plan, PersonBuilderDraft.FromPlan(plan));

        Equal(1, compilation.Corrections.Count);
        var correction = compilation.Corrections.Single();
        Equal(CorrectionKinds.MediaCredit, correction.Kind);
        Equal(CorrectionOperations.Replace, correction.Operation);
        Equal(ProviderNames.Tmdb, correction.Provider);
        Equal("20", correction.ProviderMediaId);
        Equal("3", correction.ProviderPersonId);
        Equal("1", correction.ReplacementValue);
    }

    private static void PersonBuilderCreatesAndMoves()
    {
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        var suggested = draft.People.Single(x => x.TargetKind == IdentityTargetKinds.New);
        draft.Credits.Single().TargetOutcomeId = suggested.OutcomeId;
        var compilation = IdentityCasePersonBuilder.Compile(plan, draft);

        Equal(2, compilation.EmbyChanges);
        Equal(2, compilation.Plan.Outcomes.Count);
        Equal("MOVE", compilation.Plan.Credits.Single().Disposition);
        True(compilation.Plan.ApplyCaption.Contains("create 1 person") && compilation.Plan.ApplyCaption.Contains("move 1 credit"));
        Equal(1, compilation.Corrections.Count);
        Equal(CorrectionKinds.MediaCredit, compilation.Corrections.Single().Kind);
        Equal(ProviderNames.Tmdb, compilation.Corrections.Single().Provider);
    }

    private static void PersonBuilderDoesNotPersistNewTarget()
    {
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        draft.Credits.Single().TargetOutcomeId = draft.People.Single(x => x.TargetKind == IdentityTargetKinds.New).OutcomeId;

        var compilation = IdentityCasePersonBuilder.Compile(plan, draft);

        True(!compilation.Corrections.Any(x => x.Kind == CorrectionKinds.IdentityTarget && x.ReplacementValue == "new"));
    }

    private static void PersonBuilderRejectsDuplicateIds()
    {
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        var suggested = draft.People.Single(x => x.TargetKind == IdentityTargetKinds.New);
        suggested.TmdbId = "1";
        draft.Credits.Single().TargetOutcomeId = suggested.OutcomeId;
        var failed = false;
        try { IdentityCasePersonBuilder.Compile(plan, draft); }
        catch (InvalidOperationException ex) { failed = ex.Message.Contains("cannot belong to more than one"); }
        True(failed);
    }

    private static void PersonBuilderRequiresIdentityDestination()
    {
        var plan = BuilderPlan();
        var existing = plan.Outcomes.Single(x => x.TargetKind == IdentityTargetKinds.Existing);
        existing.TargetKind = IdentityTargetKinds.Unresolved; existing.TargetEmbyId = null;
        var draft = PersonBuilderDraft.FromPlan(plan);
        var failed = false;
        try { IdentityCasePersonBuilder.Compile(plan, draft); }
        catch (InvalidOperationException ex) { failed = ex.Message.Contains("maintain an existing Emby person or be created"); }
        True(failed);
        var destination = draft.People.Single(x => x.OutcomeId == existing.OutcomeId);
        destination.TargetKind = IdentityTargetKinds.Existing; destination.TargetEmbyId = 50;
        Equal(IdentityPlanStates.Complete, IdentityCasePersonBuilder.Compile(plan, draft).Plan.State);
    }

    private static void PersonBuilderActionsPrecedeGrid()
    {
        var properties = typeof(ReviewCaseDialogUI).GetProperties().Where(x => x.DeclaringType == typeof(ReviewCaseDialogUI)).OrderBy(x => x.MetadataToken).Select(x => x.Name).ToList();
        True(properties.IndexOf(nameof(ReviewCaseDialogUI.Apply)) < properties.IndexOf(nameof(ReviewCaseDialogUI.PersonBuilder)));
        True(properties.IndexOf(nameof(ReviewCaseDialogUI.BackToAllCases)) < properties.IndexOf(nameof(ReviewCaseDialogUI.PersonBuilder)));
        True(properties.IndexOf(nameof(ReviewCaseDialogUI.LastAction)) < properties.IndexOf(nameof(ReviewCaseDialogUI.PersonBuilder)));
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        draft.Credits.Single().TargetOutcomeId = draft.People.Single(x => x.TargetKind == IdentityTargetKinds.New).OutcomeId;
        var ui = ReviewCaseDialogUI.Build(plan, draft, "server", null);
        var information = ui.Rows.Single(x => x.IsInformation);
        Equal("Information", information.Name);
        True(information.Media.Any(x => x.Media.Contains("TMDB") && x.Media.Contains("themoviedb.org/person/1")));
        True(information.Media.All(x => !x.Media.Contains("Alex Example")));
        Equal("spacer", information.Media.Last().Media);
        Equal("New 1", ui.Rows.Single(x => x.OutcomeId == "new:tvdb2").Name);
        var media = ui.Rows.SelectMany(x => x.Media).Single(x => x.AssignmentId == "credit-1");
        Equal("Lead", media.Role);
        True(media.CurrentPerson.Contains(">50</a>"));
        True(!media.TmdbOwner.Contains("Alex Example") && !media.TmdbOwner.Contains("Lead"));
        True(!media.TvdbOwner.Contains("Alex Example") && !media.TvdbOwner.Contains("Lead"));
        True(ui.Rows.Single(x => x.OutcomeId == "existing:50").Name.Contains("#!/item?id=50"));
        True(ui.Apply != null);
        True(ui.LastAction != null && !ui.LastAction.IsEnabled && ui.LastAction.Caption == string.Empty);

        ui.Rows.Single(x => x.OutcomeId == "new:tvdb2").PersonTarget = "remove";
        var rejectedRemoval = false;
        try { ReviewCaseDialogUI.Capture(draft, ui, false); }
        catch (InvalidOperationException ex) { rejectedRemoval = ex.Message.Contains("Move every media credit"); }
        True(rejectedRemoval);

        var emptyDraft = PersonBuilderDraft.FromPlan(plan);
        var emptyUi = ReviewCaseDialogUI.Build(plan, emptyDraft, "server", null);
        emptyUi.Rows.Single(x => x.OutcomeId == "new:tvdb2").PersonTarget = "remove";
        True(!ReviewCaseDialogUI.Capture(emptyDraft, emptyUi, false).People.Single(x => x.OutcomeId == "new:tvdb2").Include);
    }

    private static void PersonBuilderCreateAppendsEmptyRow()
    {
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        var ui = ReviewCaseDialogUI.Build(plan, draft, "server", null);
        var existingRow = ui.Rows.Single(x => x.OutcomeId == "existing:50");
        existingRow.PersonTarget = "new";

        var result = ReviewCaseDialogUI.ExpandCreateSelections(plan, draft, ui);

        Equal("existing:50", existingRow.PersonTarget);
        Equal(3, plan.Outcomes.Count);
        Equal(3, draft.People.Count);
        var added = draft.People.Single(x => x.OutcomeId.StartsWith("builder:new:", StringComparison.Ordinal));
        Equal(IdentityTargetKinds.New, added.TargetKind);
        Equal(string.Empty, added.TmdbId);
        Equal(string.Empty, added.TvdbId);
        Equal(string.Empty, added.ImdbId);
        True(!draft.Credits.Any(x => x.TargetOutcomeId == added.OutcomeId));
        True(ui.Rows.Any(x => x.OutcomeId == added.OutcomeId && x.Media.Length == 0));
        True(result.Contains("Created - Allocate IDs and Associate Media"));

        var captured = ReviewCaseDialogUI.Capture(draft, ui, true);
        Equal(IdentityTargetKinds.Existing, captured.People.Single(x => x.OutcomeId == "existing:50").TargetKind);
        Equal(50L, captured.People.Single(x => x.OutcomeId == "existing:50").TargetEmbyId.Value);
        True(captured.People.Single(x => x.OutcomeId == added.OutcomeId).Include);
    }

    private static void PersonBuilderPlannerNotesRoundTrip()
    {
        var plan = BuilderPlan();
        var draft = PersonBuilderDraft.FromPlan(plan);
        var ui = ReviewCaseDialogUI.Build(plan, draft, "server", null);
        ui.Rows.Single(x => x.OutcomeId == "existing:50").PlannerNotes = "TMDB identity is the older actor";

        var captured = ReviewCaseDialogUI.Capture(draft, ui, false);
        Equal("TMDB identity is the older actor", captured.People.Single(x => x.OutcomeId == "existing:50").PlannerNotes);
        var refreshed = ReviewCaseDialogUI.Build(plan, captured, "server", "Alex Example - Planner Note Updated");
        Equal("TMDB identity is the older actor", refreshed.Rows.Single(x => x.OutcomeId == "existing:50").PlannerNotes);
        Equal("Alex Example - Planner Note Updated", refreshed.LastAction.Caption);
        True(!IdentityCasePersonBuilder.Compile(plan, captured).Plan.Summary.Contains("older actor"));
    }

    private static void CaseReviewEpisodeAndSeriesLinks()
    {
        var plan = BuilderPlan();
        var credit = plan.Credits.Single();
        credit.MediaType = "episode";
        credit.MediaName = "The Reckoning";
        credit.MediaEmbyId = 321;
        credit.SeriesName = "Example Series";
        credit.SeriesEmbyId = 654;

        var ui = ReviewCaseDialogUI.Build(plan, PersonBuilderDraft.FromPlan(plan), "server", null);
        var media = ui.Rows.SelectMany(x => x.Media).Single(x => x.AssignmentId == credit.AssignmentId).Media;
        True(media.Contains(">The Reckoning</a> - <a"));
        True(media.Contains("#!/item?id=321"));
        True(media.Contains("#!/item?id=654"));
        True(media.Contains(">Example Series</a>"));
    }

    private static void CaseReviewMediaEnabledByDefault()
    {
        True(new PersonCleaner.Configuration.PluginConfiguration().PopulateCaseReviewWithOutOfScopeMediaItems);
    }

    private static void CaseReviewAddsMissingLiveCredits()
    {
        var plan = BuilderPlan();
        var rows = new[]
        {
            new ReviewLiveCredit { PersonEmbyId = 50, MediaEmbyId = 20, MediaType = MediaTypes.Movie, MediaName = "Disputed title", Role = "Actor: Lead" },
            new ReviewLiveCredit { PersonEmbyId = 50, MediaEmbyId = 21, MediaType = "episode", MediaName = "Out-of-scope episode", Role = "GuestStar: Witness" },
            new ReviewLiveCredit { PersonEmbyId = 50, MediaEmbyId = 21, MediaType = "episode", MediaName = "Out-of-scope episode", Role = "GuestStar: Witness" }
        };
        var missing = ReviewCaseCreditInventory.Missing(plan, rows);
        Equal(1, missing.Count);
        True(missing.Single().IsReviewSupplemental);
        Equal("existing:50", missing.Single().TargetOutcomeId);
        plan.Credits.AddRange(missing);

        var draft = PersonBuilderDraft.FromPlan(plan);
        var unchanged = IdentityCasePersonBuilder.Compile(plan, draft);
        Equal(1, unchanged.Plan.Credits.Count);
        True(unchanged.Plan.Credits.All(x => !x.IsReviewSupplemental));
        var ui = ReviewCaseDialogUI.Build(plan, draft, "server", null);
        Equal("Outside evidence scope", ui.Rows.SelectMany(x => x.Media).Single(x => x.AssignmentId == missing.Single().AssignmentId).Attribution);

        draft.Credits.Single(x => x.AssignmentId == missing.Single().AssignmentId).TargetOutcomeId = "new:tvdb2";
        var moved = IdentityCasePersonBuilder.Compile(plan, draft);
        var supplemental = moved.Plan.Credits.Single(x => x.IsReviewSupplemental);
        Equal("MOVE", supplemental.Disposition);
        Equal(21L, supplemental.MediaEmbyId);
    }

    private static IdentityCasePlan BuilderPlan()
    {
        var plan = new IdentityCasePlan
        {
            RunId = 1, CaseId = "builder-case", PlanHash = "reviewed", DisplayName = "Alex Example", CaseType = "Credits assigned to the wrong Emby person",
            Summary = "Review the proposed identities.", State = IdentityPlanStates.CorrectionRequired, ApplyCaption = "Correction required",
            DecisionIds = new List<string> { "builder-case" },
            CurrentPeople = new List<LocalPerson> { new LocalPerson { EmbyId = 50, Name = "Alex Example", TmdbId = "1", ImdbId = "nm1" } }
        };
        var existing = new IdentityOutcome
        {
            OutcomeId = "existing:50", SortOrder = 0, TargetKind = IdentityTargetKinds.Existing, TargetEmbyId = 50, DisplayName = "Alex Example", Outcome = "Retain Emby person 50",
            SourceEmbyIds = new List<long> { 50 }, ProviderIds = new List<IdentityProviderId> { new IdentityProviderId { Provider = ProviderNames.Tmdb, ProviderId = "1", Source = "native" }, new IdentityProviderId { Provider = ProviderNames.Imdb, ProviderId = "nm1", Source = "external" } }
        };
        var suggested = new IdentityOutcome
        {
            OutcomeId = "new:tvdb2", SortOrder = 1, TargetKind = IdentityTargetKinds.New, DisplayName = "Alex Example", Outcome = "Create provider-identified Emby person",
            ProviderIds = new List<IdentityProviderId> { new IdentityProviderId { Provider = ProviderNames.Tvdb, ProviderId = "2", Source = "native" }, new IdentityProviderId { Provider = ProviderNames.Imdb, ProviderId = "nm2", Source = "external" } }
        };
        plan.Outcomes.Add(existing); plan.Outcomes.Add(suggested);
        plan.Credits.Add(new IdentityCreditOutcome
        {
            AssignmentId = "credit-1", SourcePersonEmbyId = 50, TargetOutcomeId = existing.OutcomeId, MediaEmbyId = 20, MediaType = MediaTypes.Movie,
            MediaName = "Disputed title", Role = "Actor: Lead", Disposition = "KEEP", CorrectionRequired = true,
            Attributions = new List<IdentityCreditAttribution>
            {
                new IdentityCreditAttribution { Provider = ProviderNames.Tmdb, ProviderMediaId = "20", ProviderPersonId = "1", PersonName = "Alex Example", Role = "Actor: Lead", RoleCategory = "Actor", OutcomeId = existing.OutcomeId },
                new IdentityCreditAttribution { Provider = ProviderNames.Tvdb, ProviderMediaId = "200", ProviderPersonId = "2", PersonName = "Alex Example", Role = "Actor: Lead", RoleCategory = "Actor", OutcomeId = suggested.OutcomeId }
            }
        });
        var question = new IdentityQuestion { QuestionId = "question-credit-1", Kind = CorrectionKinds.LocalCreditTarget, AssignmentId = "credit-1", Narrative = "Who should receive the credit?" };
        question.Choices.Add(new IdentityQuestionChoice
        {
            ChoiceId = "question-credit-1:existing", Caption = "Provider credit belongs to existing person", Effect = "Ignore the conflicting TVDB assertion.",
            Correction = new ProviderCorrection { Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tvdb, MediaType = MediaTypes.Movie, ProviderMediaId = "200", ProviderPersonId = "2", CurrentValue = "Actor: Lead", Reason = "OPERATOR_PROVIDER_ATTRIBUTION", Enabled = true }
        });
        question.Choices.Add(new IdentityQuestionChoice
        {
            ChoiceId = "question-credit-1:new", Caption = "Provider credit belongs to new person", Effect = "Ignore the conflicting TMDB assertion.",
            Correction = new ProviderCorrection { Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Unusable, Provider = ProviderNames.Tmdb, MediaType = MediaTypes.Movie, ProviderMediaId = "20", ProviderPersonId = "1", CurrentValue = "Actor: Lead", Reason = "OPERATOR_PROVIDER_ATTRIBUTION", Enabled = true }
        });
        plan.Questions.Add(question);
        return plan;
    }

    private static void LargeProviderCreditSetRemainsBounded()
    {
        const int peoplePerProvider = 80;
        const string sharedMedia = "canonical:shared";
        var input = new ResolutionInput();
        for (var i = 0; i < peoplePerProvider; i++)
        {
            var tmdb = Person(ProviderNames.Tmdb, "large-tmdb-" + i, "Left Person " + i, null, null, sharedMedia);
            var tvdb = Person(ProviderNames.Tvdb, "large-tvdb-" + i, "Right Person " + i, null, null, sharedMedia);
            AddObservedCredit(tmdb, sharedMedia, "Actor", "Left Role " + i);
            AddObservedCredit(tvdb, sharedMedia, "Actor", "Right Role " + i);
            input.ProviderPeople.Add(tmdb);
            input.ProviderPeople.Add(tvdb);
            input.ProviderCredits.AddRange(tmdb.Credits);
            input.ProviderCredits.AddRange(tvdb.Credits);
        }
        for (var i = 0; i < 20000; i++)
            input.ProviderCredits.Add(new ObservedProviderCredit
            {
                Provider = (i & 1) == 0 ? ProviderNames.Tmdb : ProviderNames.Tvdb,
                ProviderPersonId = "unrelated-" + i,
                PersonName = "Unrelated Person " + i,
                CanonicalMediaKey = "canonical:unrelated:" + i,
                Role = "Actor",
                RoleCategory = "Actor",
                RoleName = "Unrelated Role"
            });

        var engine = new ResolutionEngine();
        var clock = Stopwatch.StartNew();
        engine.Resolve(input, new ResolutionSettings());
        clock.Stop();
        Equal(peoplePerProvider * peoplePerProvider, engine.Diagnostics.BlockedCrossProviderPairs);
        True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    private static void CasePlanningIgnoresLargeGlobalPopulation()
    {
        const int globalCount = 300000;
        var input = new ResolutionInput();
        var tmdb = Person(ProviderNames.Tmdb, "42", "Case Local Person", null, null);
        input.ProviderPeople.Add(tmdb);
        input.LocalPeople.Add(new LocalPerson { EmbyId = 42, Name = "Case Local Person", TmdbId = "42" });
        input.GlobalLocalPeople.Capacity = globalCount;
        for (var i = 0; i < globalCount; i++) input.GlobalLocalPeople.Add(new LocalPerson { EmbyId = 1000000L + i, Name = "Unrelated " + i, TmdbId = "unrelated-" + i });
        var decision = new ResolutionDecision { DecisionId = "case-local", Status = "MATCH", Action = "CROSS_PROVIDER_IDENTITY", DisplayName = "Case Local Person", AnchorEmbyPersonId = 42, ProviderKeys = "tmdb:42" };

        var clock = Stopwatch.StartNew();
        var plan = IdentityCasePlanner.Build(1, input, new[] { decision }, new[] { Cluster("case-local", 42, "tmdb:42") }).Single();
        clock.Stop();

        Equal(IdentityTargetKinds.Existing, plan.Outcomes.Single().TargetKind);
        True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static ResolutionInput BaseInput(params ProviderPerson[] people) => new ResolutionInput { ProviderPeople = people.ToList() };
    private static DashboardDecision DashboardRow(string id, string name, string providerIdentities, string correction = null, long mediaId = 0)
    {
        var details = new List<DashboardDetail>();
        if (!string.IsNullOrWhiteSpace(correction)) details.Add(new DashboardDetail { DetailId = id + ":correction", Section = "Evidence", Signal = "RECOMMENDED_PROVIDER_CORRECTION", Verdict = "recommended", Explanation = "TVDB credits this title to the other same-name record; replace that attribution with the record supported by the matching identity evidence.", RawMetric = correction });
        if (mediaId > 0) details.Add(new DashboardDetail { DetailId = id + ":media", Section = "Affected titles", Signal = "MEDIA", Verdict = "conflict", Explanation = "Example title", EmbyMediaId = mediaId, MediaType = MediaTypes.Movie });
        return new DashboardDecision
        {
            DecisionId = id, Status = "CONFLATION", Action = "HUMAN_REVIEW", Person = name, EmbyAnchor = "Emby 1", ProviderIdentities = providerIdentities,
            CurrentProviderIds = "TMDB 15739; TVDB 457122", Confidence = "85%", LocalAnchorConfidence = "90%", ImpactedTitles = mediaId > 0 ? 1 : 0,
            Decision = "Provider records disagree about a same-name credit.", Why = "The evidence is not yet safe to apply automatically.", Details = details.ToArray()
        };
    }
    private static PersonAcquisition Acquisition(string provider, string id, string state) => new PersonAcquisition { Provider = provider, ProviderId = id, State = state, Source = "test" };
    private static ProviderPerson Person(string provider, string id, string name, string externalProvider, string externalId, params string[] media)
    {
        var result = new ProviderPerson { Provider = provider, ProviderId = id, Name = name, CanonicalMediaKeys = new HashSet<string>(media) };
        if (externalProvider != null) result.ExternalIds[externalProvider] = externalId;
        return result;
    }
    private static void AddObservedCredit(ProviderPerson person, string media, string category, string role, string mediaType = null, string providerMediaId = null)
    {
        person.Credits.Add(new ObservedProviderCredit { Provider = person.Provider, ProviderPersonId = person.ProviderId, PersonName = person.Name, CanonicalMediaKey = media, MediaType = mediaType, ProviderMediaId = providerMediaId, RoleCategory = category, RoleName = role, Role = category + ": " + role });
    }
    private static MediaSeed Media(long id, string name) => new MediaSeed { EmbyId = id, MediaType = MediaTypes.Movie, Name = name, TmdbId = id.ToString() };
    private static ResolutionClusterSnapshot Cluster(string id, long anchor, params string[] keys) => new ResolutionClusterSnapshot { ClusterId = id, AnchorEmbyPersonId = anchor, ProviderKeys = keys.ToList() };
    private static void AddPlanMedia(ResolutionInput input, long id, string name, string tmdbId, string tvdbId, string role, params ProviderPerson[] people)
    {
        input.Media.Add(new MediaSeed { EmbyId = id, MediaType = MediaTypes.Movie, Name = name, TmdbId = tmdbId, TvdbId = tvdbId });
        input.LocalCredits.Add(new LocalCredit { PersonEmbyId = input.LocalPeople.Single().EmbyId, MediaEmbyId = id, Role = role });
        foreach (var person in people)
        {
            var providerMediaId = person.Provider == ProviderNames.Tmdb ? tmdbId : tvdbId;
            input.ProviderCredits.Add(new ObservedProviderCredit { Provider = person.Provider, ProviderPersonId = person.ProviderId, PersonName = person.Name, MediaType = MediaTypes.Movie, ProviderMediaId = providerMediaId, CanonicalMediaKey = "media:" + id, Role = role, RoleCategory = role.Split(':')[0], RoleName = role });
        }
    }
    private static LocalCredit Credit(long person, long media) => new LocalCredit { PersonEmbyId = person, MediaEmbyId = media, Role = "Actor" };
    private static ProviderMediaIdentity ProviderMedia(string provider, string id, params MediaExternalIdentity[] externalIds) => new ProviderMediaIdentity { Provider = provider, MediaType = MediaTypes.Movie, ProviderMediaId = id, ExternalIds = externalIds.ToList() };
    private static MediaExternalIdentity External(string provider, string id) => new MediaExternalIdentity { Provider = provider, Id = id };
    private static void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); } }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected '" + expected + "' but got '" + actual + "'."); }
}
