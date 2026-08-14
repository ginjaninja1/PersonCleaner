using System;
using System.Collections.Generic;

namespace PersonCleaner.Tvdb
{
    internal static class TvdbScope
    {
        private static readonly HashSet<string> ScreenCreditTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Actor", "Guest Star", "Director", "Writer", "Screenplay", "Producer",
            "Executive Producer", "Creator", "Showrunner"
        };

        public static bool IsScreenCredit(CharacterData credit) =>
            credit != null && !string.IsNullOrWhiteSpace(credit.peopleType) && ScreenCreditTypes.Contains(credit.peopleType.Trim());
    }

    internal sealed class TvdbResponse<T> { public string status { get; set; } public T data { get; set; } public TvdbLinks links { get; set; } }
    internal sealed class TvdbLinks { public int page_size { get; set; } public string next { get; set; } }
    internal sealed class LoginRequest { public string apikey { get; set; } public string pin { get; set; } }
    internal sealed class LoginData { public string token { get; set; } }
    internal sealed class EntityData
    {
        public int id { get; set; }
        public string name { get; set; }
        public string slug { get; set; }
        public string birth { get; set; }
        public string death { get; set; }
        public string birthPlace { get; set; }
        public string firstAired { get; set; }
        public string lastAired { get; set; }
        public string originalCountry { get; set; }
        public string originalLanguage { get; set; }
        public List<RemoteIdData> remoteIds { get; set; }
        public List<CharacterData> characters { get; set; }
    }
    internal sealed class EpisodeData
    {
        public int id { get; set; }
        public int seriesId { get; set; }
        public string name { get; set; }
        public int number { get; set; }
        public int seasonNumber { get; set; }
        public string aired { get; set; }
        public List<RemoteIdData> remoteIds { get; set; }
    }
    internal sealed class EpisodesData { public List<EpisodeData> episodes { get; set; } public List<CharacterData> characters { get; set; } }
    internal sealed class RemoteIdData { public string id { get; set; } public int type { get; set; } public string sourceName { get; set; } }
    internal sealed class CharacterData
    {
        public int id { get; set; }
        public string name { get; set; }
        public int peopleId { get; set; }
        public int? seriesId { get; set; }
        public int? movieId { get; set; }
        public int? episodeId { get; set; }
        public int type { get; set; }
        public int sort { get; set; }
        public bool isFeatured { get; set; }
        public string peopleType { get; set; }
        public string personName { get; set; }
    }
    internal sealed class SearchData
    {
        public string tvdb_id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public string primary_language { get; set; }
        public string country { get; set; }
        public string year { get; set; }
        public List<RemoteIdData> remote_ids { get; set; }
    }
    internal sealed class SearchByRemoteIdData
    {
        public SearchEntityData series { get; set; }
        public SearchEntityData people { get; set; }
        public SearchEntityData movie { get; set; }
        public SearchEntityData episode { get; set; }
    }
    internal sealed class SearchEntityData
    {
        public int id { get; set; }
        public string name { get; set; }
        public string year { get; set; }
    }
}
