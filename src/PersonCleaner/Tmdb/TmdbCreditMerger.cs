using System.Collections.Generic;
using System.Linq;

namespace PersonCleaner.Tmdb
{
    internal static class TmdbCreditMerger
    {
        public static List<TmdbCredit> Cast(TmdbEntity entity,TmdbCredits additional=null)
        {
            var embedded=entity?.combined_credits??entity?.aggregate_credits??entity?.credits;
            return (embedded?.cast??new List<TmdbCredit>())
                .Concat(embedded?.guest_stars??new List<TmdbCredit>())
                .Concat(entity?.guest_stars??new List<TmdbCredit>())
                .Concat(additional?.cast??new List<TmdbCredit>())
                .Concat(additional?.guest_stars??new List<TmdbCredit>())
                .GroupBy(x=>x.id).Select(x=>x.First()).ToList();
        }
    }
}
