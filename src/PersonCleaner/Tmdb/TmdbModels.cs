using System.Collections.Generic;

namespace PersonCleaner.Tmdb
{
    internal sealed class TmdbEntity
    {
        public int id { get; set; }
        public string name { get; set; }
        public string title { get; set; }
        public string original_name { get; set; }
        public string original_title { get; set; }
        public string biography { get; set; }
        public string birthday { get; set; }
        public string deathday { get; set; }
        public string place_of_birth { get; set; }
        public List<string> also_known_as { get; set; } = new List<string>();
        public string first_air_date { get; set; }
        public string release_date { get; set; }
        public string air_date { get; set; }
        public int? season_number { get; set; }
        public int? episode_number { get; set; }
        public int? show_id { get; set; }
        public TmdbExternalIds external_ids { get; set; }
        public TmdbCredits credits { get; set; }
        public List<TmdbCredit> guest_stars { get; set; } = new List<TmdbCredit>();
        public TmdbCredits aggregate_credits { get; set; }
        public TmdbCredits combined_credits { get; set; }
        public TmdbAliases alternative_names { get; set; }
        public TmdbAliases alternative_titles { get; set; }
    }

    internal sealed class TmdbExternalIds
    {
        public string imdb_id { get; set; }
        public string tvdb_id { get; set; }
        public string wikidata_id { get; set; }
        public string facebook_id { get; set; }
        public string instagram_id { get; set; }
        public string twitter_id { get; set; }
        public string tiktok_id { get; set; }
        public string youtube_id { get; set; }
    }

    internal sealed class TmdbAliases
    {
        public List<TmdbAlias> results { get; set; } = new List<TmdbAlias>();
        public List<TmdbAlias> titles { get; set; } = new List<TmdbAlias>();
    }
    internal sealed class TmdbAlias { public string name { get; set; } public string title { get; set; } public string iso_3166_1 { get; set; } public string type { get; set; } }

    internal sealed class TmdbCredits
    {
        public List<TmdbCredit> cast { get; set; } = new List<TmdbCredit>();
        public List<TmdbCredit> guest_stars { get; set; } = new List<TmdbCredit>();
        public List<TmdbCredit> crew { get; set; } = new List<TmdbCredit>();
    }

    internal sealed class TmdbCredit
    {
        public int id { get; set; }
        public string media_type { get; set; }
        public string name { get; set; }
        public string title { get; set; }
        public string original_name { get; set; }
        public string original_title { get; set; }
        public string character { get; set; }
        public string job { get; set; }
        public string department { get; set; }
        public string credit_id { get; set; }
        public string first_air_date { get; set; }
        public string release_date { get; set; }
        public int? episode_count { get; set; }
        public List<TmdbRole> roles { get; set; } = new List<TmdbRole>();
        public List<TmdbJob> jobs { get; set; } = new List<TmdbJob>();
    }

    internal sealed class TmdbRole { public string credit_id { get; set; } public string character { get; set; } public int? episode_count { get; set; } }
    internal sealed class TmdbJob { public string credit_id { get; set; } public string job { get; set; } public int? episode_count { get; set; } }

    internal sealed class TmdbFindResponse
    {
        public List<TmdbEntity> movie_results { get; set; } = new List<TmdbEntity>();
        public List<TmdbEntity> tv_results { get; set; } = new List<TmdbEntity>();
        public List<TmdbEntity> tv_episode_results { get; set; } = new List<TmdbEntity>();
        public List<TmdbEntity> person_results { get; set; } = new List<TmdbEntity>();
    }
    internal sealed class TmdbPersonSearchResponse { public List<TmdbEntity> results { get; set; } = new List<TmdbEntity>(); }
}
