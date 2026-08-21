using System.Collections.Generic;
using System.Linq;

namespace PersonCleaner.Tmdb
{
    internal static class TmdbCreditMerger
    {
        public static List<TmdbCredit> Cast(TmdbEntity entity,TmdbCredits dedicated=null)
        {
            var embedded=entity?.credits??entity?.aggregate_credits;
            return (embedded?.cast??new List<TmdbCredit>())
                .Concat(embedded?.guest_stars??new List<TmdbCredit>())
                .Concat(entity?.guest_stars??new List<TmdbCredit>())
                .Concat(dedicated?.cast??new List<TmdbCredit>())
                .Concat(dedicated?.guest_stars??new List<TmdbCredit>())
                .GroupBy(x=>x.id).Select(x=>x.First()).ToList();
        }
    }
}
