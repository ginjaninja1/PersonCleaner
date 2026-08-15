using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using System.Collections.Generic;
using System.ComponentModel;

namespace PersonCleaner.UI.Config
{
    public class ConfigUI : EditableOptionsBase
    {
        public override string EditorTitle => "TVDB Archive - Configuration";

        public override string EditorDescription =>
            "Archives TVDB identity and filmography data to tvdb-archive.db in Emby's data directory.";

        public CaptionItem GeneralHeading { get; set; } = new CaptionItem("General");

        [DisplayName("Enable Plugin")]
        [Description("When disabled, the scheduled task exits immediately without processing any items.")]
        [AutoPostBack("updateconfig", nameof(EnablePlugin))]
        public bool EnablePlugin { get; set; } = true;

        [DisplayName("TVDB v4 API key")]
        [Description("Stored in Emby's plugin configuration; never written to the archive database or logs.")]
        public string TvdbApiKey { get; set; }

        [DisplayName("TVDB subscriber PIN (optional)")]
        public string TvdbSubscriberPin { get; set; }

        [DisplayName("Successful response cache (days)")]
        public int SuccessCacheDays { get; set; } = 30;

        [DisplayName("Failed/timeout retry delay (minutes)")]
        public int FailureRetryMinutes { get; set; } = 30;

        [DisplayName("Minimum request interval (milliseconds)")]
        public int MinimumRequestIntervalMilliseconds { get; set; } = 100;

        [DisplayName("Preview items per media type")]
        public int PreviewItemLimit { get; set; } = 5;

        [DisplayName("Require preview before full export")]
        public bool RequireSuccessfulPreview { get; set; } = true;

        [DisplayName("Resolution evaluation items per type")]
        [Description("Known-good TVDB IDs are withheld from this many movies, series, episodes and in-scope people to measure resolver accuracy.")]
        public int ResolutionEvaluationItemsPerType { get; set; } = 25;

        [DisplayName("Automatic resolution minimum confidence")]
        [Description("Used after evaluation; 0.95 means only predictions scoring at least 95% may be treated as inferred TVDB identities.")]
        public double AutoResolutionMinimumConfidence { get; set; } = 0.95;

        public CaptionItem LibraryFilterHeading { get; set; } =
            new CaptionItem("Library / Path Filter");


        /// <summary>
        /// Persistent configuration data.
        /// </summary>
        [Browsable(false)]
        public List<LibraryPathFilterItem> LibraryPaths { get; set; } =
            new List<LibraryPathFilterItem>();


        /// <summary>
        /// GenericUI representation of LibraryPaths.
        /// </summary>
        public GenericItemList LibraryList { get; set; } =
            new GenericItemList();

        /*
        public GenericItemList ScheduledTaskLink { get; set; } = new GenericItemList
        {
            new GenericListItem
            {
                PrimaryText = "Configure Scheduled Task",
                SecondaryText = "",
                Icon = IconNames.link,
                Status = ItemStatus.Succeeded,
                HyperLink = "/scheduledtasks",
                HyperLinkTargetExternal = true
            }
        };
        */
        public GenericItemList ScheduledTaskLink { get; set; } = new GenericItemList();

        public GenericItemList ForumLink { get; set; } = new GenericItemList
        {
            new GenericListItem
            {
                PrimaryText = "Community Forum",
                SecondaryText = "Issues, Suggestions and Updates",
                Icon = IconNames.link,
                Status = ItemStatus.Succeeded,
                HyperLink = "https://emby.media/community/topic/148589-plugin-poster-to-folder/",
                HyperLinkTargetExternal = true
            }
        };

        public GenericItemList GithubLink { get; set; } = new GenericItemList
        {
            new GenericListItem
            {
                PrimaryText = "Github repository",
                SecondaryText = "",
                Icon = IconNames.link,
                Status = ItemStatus.Succeeded,
                HyperLink = "https://github.com/ginjaninja1/PersonCleaner",
                HyperLinkTargetExternal = true
            }
        };
    }
}
