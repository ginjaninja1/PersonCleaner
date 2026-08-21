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
        SubstringsDoNotMatch();
        EquivalencePairsAreNotTransitivelyExpanded();
        AnnieKarstensDedicatedEpisodeGuestsAreMerged();
        Console.WriteLine("Person name regression fixtures passed.");
        return 0;
    }

    private static void AnnieKarstensDedicatedEpisodeGuestsAreMerged()
    {
        var partial=new TmdbEntity{id=1944821,credits=new TmdbCredits{cast=new List<TmdbCredit>{new TmdbCredit{id=110927,name="Penn Badgley"}}}};
        var dedicated=new TmdbCredits{guest_stars=new List<TmdbCredit>{new TmdbCredit{id=1137005,name="Annie Karstens",character="DMV Lady"}}};
        var merged=TmdbCreditMerger.Cast(partial,dedicated);
        Require(merged.Exists(x=>x.id==1137005&&x.character=="DMV Lady"),"Emby 47116 regression: dedicated A Fresh Start guest credits must add TMDB 1137005 Annie Karstens.");
        Require(merged.Count==2,"Embedded and dedicated episode credits must be combined without losing regular cast.");
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
