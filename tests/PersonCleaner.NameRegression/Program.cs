using System;
using System.Collections.Generic;
using PersonCleaner.Housekeeping;
using PersonCleaner.Tmdb;

internal static class Program
{
    private static int Main()
    {
        DonBarryProviderConsensusNameCompatibility();
        ConfiguredGivenNameEquivalenceIsDirectAndOptional();
        ConfiguredGivenNameEquivalenceAppliesToAliases();
        KimberlyHidalgoIdentityEnvelopeIsCompatible();
        PlausibleLeadIsConservative();
        SubstringsDoNotMatch();
        EquivalencePairsAreNotTransitivelyExpanded();
        AnnieKarstensCachedEpisodeGuestsAreMerged();
        Console.WriteLine("Person name regression fixtures passed.");
        return 0;
    }

    private static void AnnieKarstensCachedEpisodeGuestsAreMerged()
    {
        var cached=new TmdbEntity{id=1944821,guest_stars=new List<TmdbCredit>{new TmdbCredit{id=1137005,name="Annie Karstens",character="DMV Lady"}},credits=new TmdbCredits{cast=new List<TmdbCredit>{new TmdbCredit{id=110927,name="Penn Badgley"}}}};
        var merged=TmdbCreditMerger.Cast(cached);
        Require(merged.Exists(x=>x.id==1137005&&x.character=="DMV Lady"),"Emby 47116 regression: cached A Fresh Start root guest stars must add TMDB 1137005 Annie Karstens.");
        Require(merged.Count==2,"Root guest stars and appended episode cast must be combined without losing either collection.");
    }

    private static void DonBarryProviderConsensusNameCompatibility()
    {
        var match = PersonNameCompatibility.Compare("Don Barry", "Don 'Red' Barry", new[] { "Donald 'Red' Barry", "Donald Barry", "Donald Barry De Acosta", "Donald M. Barry", "Milton Poimboeuf" }, "Don=Donald");
        Require(match.Compatible, "Emby 12148 Don Barry must be compatible with the shared TMDB/TVDB canonical name Don 'Red' Barry.");
        Require(match.Reason.IndexOf("optional", StringComparison.OrdinalIgnoreCase) >= 0, "Don Barry must match by safe optional-nickname removal, not substring matching.");
    }

    private static void ConfiguredGivenNameEquivalenceIsDirectAndOptional()
    {
        Require(PersonNameCompatibility.Compare("Don Barry", "Donald Barry", new List<string>(), "Don=Donald").Compatible, "Configured Don=Donald must corroborate identical remaining tokens.");
        Require(!PersonNameCompatibility.Compare("Don Barry", "Donald Barry", new List<string>(), string.Empty).Compatible, "Removing Don=Donald must disable that equivalence.");
        Require(!PersonNameCompatibility.Compare("Don Smith", "Donald Barry", new List<string>(), "Don=Donald").Compatible, "Given-name equivalence must not override a surname conflict.");
    }

    private static void ConfiguredGivenNameEquivalenceAppliesToAliases()
    {
        var match = PersonNameCompatibility.Compare("Kimberly Hidalgo", "Kimberly Daugherty", new[] { "Kim Hidalgo" }, "Kim=Kimberly");
        Require(match.Compatible, "Emby 439699 regression: configured Kim=Kimberly must apply to the TVDB alias Kim Hidalgo.");
        Require(match.Reason.IndexOf("provider-alias", StringComparison.OrdinalIgnoreCase) >= 0, "The evidence must identify that the configured pair matched a provider alias.");
    }

    private static void KimberlyHidalgoIdentityEnvelopeIsCompatible()
    {
        var match = PersonNameCompatibility.CompareIdentityEnvelope("Kimberly Hidalgo", "Kimberly Daugherty", new[] { "Kim Hidalgo" }, string.Empty);
        Require(match.Compatible, "Recommendation 1833 / Emby 439699: canonical Kimberly plus alias family name Hidalgo must nominate TVDB 393526 when exact media evidence exists.");
        Require(match.Reason.IndexOf("identity envelope", StringComparison.OrdinalIgnoreCase) >= 0, "Composite canonical/alias evidence must be explicitly explained.");
    }

    private static void PlausibleLeadIsConservative()
    {
        Require(PersonNameCompatibility.IsPlausibleLead("Kimberly Hidalgo", "Kimberly Daugherty", "Kim=Kimberly"), "A shared full given name must nominate a linked-media candidate.");
        Require(PersonNameCompatibility.IsPlausibleLead("Don Barry", "Donald Barry", "Don=Donald"), "A configured given-name pair with the same family name must nominate a candidate.");
        Require(!PersonNameCompatibility.IsPlausibleLead("Kimberly Hidalgo", "Maz Jobrani", "Kim=Kimberly"), "An unrelated co-star must not become a person-detail request.");
    }

    private static void SubstringsDoNotMatch()
    {
        Require(!PersonNameCompatibility.Compare("Ann Smith", "Joanne Smith", new List<string>(), string.Empty).Compatible, "Name substrings must not create compatibility.");
        Require(!PersonNameCompatibility.Compare("Don Barry", "London Barry", new List<string>(), string.Empty).Compatible, "Embedded substrings must not create compatibility.");
    }

    private static void EquivalencePairsAreNotTransitivelyExpanded()
    {
        Require(!PersonNameCompatibility.Compare("Steven Smith", "Stephen Smith", new List<string>(), "Steve=Steven;Steve=Stephen").Compatible, "Configured pairs must not be transitively expanded.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
