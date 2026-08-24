using System.Collections.Generic;

namespace PersonCleaner.V2.Providers
{
    internal sealed class TmdbMedia
    {
        public int id { get; set; }
        public string name { get; set; }
        public string title { get; set; }
        public TmdbExternalIds external_ids { get; set; }
        public TmdbCredits credits { get; set; }
        public TmdbCredits aggregate_credits { get; set; }
    }

    internal sealed class TmdbPerson
    {
        public int id { get; set; }
        public string name { get; set; }
        public string birthday { get; set; }
        public List<string> also_known_as { get; set; } = new List<string>();
        public TmdbExternalIds external_ids { get; set; }
    }

    internal sealed class TmdbExternalIds
    {
        public string imdb_id { get; set; }
        public string tvdb_id { get; set; }
        public string wikidata_id { get; set; }
    }

    internal sealed class TmdbCredits
    {
        public List<TmdbCredit> cast { get; set; } = new List<TmdbCredit>();
        public List<TmdbCredit> crew { get; set; } = new List<TmdbCredit>();
    }

    internal sealed class TmdbCredit
    {
        public int id { get; set; }
        public string name { get; set; }
        public string character { get; set; }
        public string job { get; set; }
        public string department { get; set; }
        public List<TmdbRole> roles { get; set; } = new List<TmdbRole>();
        public List<TmdbJob> jobs { get; set; } = new List<TmdbJob>();
    }

    internal sealed class TmdbRole { public string character { get; set; } }
    internal sealed class TmdbJob { public string job { get; set; } }

    internal sealed class TvdbResponse<T> { public string status { get; set; } public T data { get; set; } }
    internal sealed class TvdbLoginRequest { public string apikey { get; set; } public string pin { get; set; } }
    internal sealed class TvdbLogin { public string token { get; set; } }
    internal sealed class TvdbEntity
    {
        public int id { get; set; }
        public string name { get; set; }
        public string slug { get; set; }
        public string birth { get; set; }
        public List<TvdbRemoteId> remoteIds { get; set; } = new List<TvdbRemoteId>();
        public List<TvdbAlias> aliases { get; set; } = new List<TvdbAlias>();
        public List<TvdbCharacter> characters { get; set; } = new List<TvdbCharacter>();
    }
    internal sealed class TvdbRemoteId { public string id { get; set; } public string sourceName { get; set; } public int type { get; set; } }
    internal sealed class TvdbAlias { public string name { get; set; } public string language { get; set; } }
    internal sealed class TvdbCharacter
    {
        public int peopleId { get; set; }
        public string personName { get; set; }
        public string peopleType { get; set; }
        public string name { get; set; }
    }
}
