using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class CorrectionChoiceDialogUI : EditableObjectBase
    {
        public override string EditorTitle => null;
        public CaptionItem ContextHeading { get; set; } = new CaptionItem("Correction context");
        [DisplayName("Affected record")]
        public LabelItem AffectedRecord { get; set; }
        [DisplayName("Missing fact")]
        public LabelItem Question { get; set; }
        [DisplayName("Current proposal")]
        public LabelItem CurrentProposal { get; set; }
        public CaptionItem RelevantRecordsHeading { get; set; } = new CaptionItem("Linked records") { };
        [DisplayName("Emby / provider record")]
        public LabelItem RelevantRecord1 { get; set; }
        [DisplayName("Emby / provider record")]
        public LabelItem RelevantRecord2 { get; set; }
        [DisplayName("Emby / provider record")]
        public LabelItem RelevantRecord3 { get; set; }
        [DisplayName("Emby / provider record")]
        public LabelItem RelevantRecord4 { get; set; }
        [DisplayName("Emby / provider record")]
        public LabelItem RelevantRecord5 { get; set; }
        [DisplayName("Emby / provider record")]
        public LabelItem RelevantRecord6 { get; set; }
        public CaptionItem ChoiceHeading { get; set; } = new CaptionItem("Correction choices");
        [DisplayName("Choose correction")]
        public ButtonItem Choices { get; set; }
        [DisplayName("Selected correction")]
        public LabelItem SelectedChoice { get; set; }
        [DisplayName("Effect")]
        public LabelItem SelectedEffect { get; set; }
        public ButtonItem Save { get; set; }
        public ButtonItem SaveWithoutRecalculation { get; set; }
        public LabelItem ValidationStatus { get; set; }

        internal static CorrectionChoiceDialogUI Build(CorrectionContext context, int selectedIndex, string status)
        {
            var buttons = context.Choices.Select((x, i) => new ButtonItem(x.Caption) { CommandId = CorrectionChoiceCommands.Select + i.ToString(CultureInfo.InvariantCulture) }).ToList();
            var selected = selectedIndex >= 0 && selectedIndex < context.Choices.Count ? context.Choices[selectedIndex] : null;
            var ui = new CorrectionChoiceDialogUI
            {
                AffectedRecord = new LabelItem(context.AffectedRecord), Question = new LabelItem(context.Question), CurrentProposal = new LabelItem(context.CurrentProposal),
                Choices = buttons.Count == 1 ? buttons[0] : new ButtonItem("Choose one of " + buttons.Count + " explicit outcomes") { SubMenuButtons = buttons },
                SelectedChoice = new LabelItem(selected == null ? "No correction selected." : selected.Caption), SelectedEffect = new LabelItem(selected == null ? "Select a correction to see its effect on the recalculated case." : selected.Effect),
                Save = selected == null ? null : new ButtonItem("Save correction and recalculate now") { CommandId = CorrectionChoiceCommands.Save, ConfirmationPrompt = "Save this durable correction and recalculate the complete identity case now?" },
                SaveWithoutRecalculation = selected == null ? null : new ButtonItem("Save correction for batch recalculation") { CommandId = CorrectionChoiceCommands.SaveWithoutRecalculation, ConfirmationPrompt = "Save this correction without recalculating yet? The case will remain unchanged until batch recalculation is run." },
                ValidationStatus = new LabelItem(status ?? "Nothing is saved until the Save button is pressed.")
            };
            var links = context.RelevantRecords.Take(6).ToArray();
            if (links.Length > 0) ui.RelevantRecord1 = links[0]; if (links.Length > 1) ui.RelevantRecord2 = links[1]; if (links.Length > 2) ui.RelevantRecord3 = links[2];
            if (links.Length > 3) ui.RelevantRecord4 = links[3]; if (links.Length > 4) ui.RelevantRecord5 = links[4]; if (links.Length > 5) ui.RelevantRecord6 = links[5];
            return ui;
        }
    }

    internal static class CorrectionChoiceCommands
    {
        public const string Select = "case-correction-choice:";
        public const string Save = "case-correction-save";
        public const string SaveWithoutRecalculation = "case-correction-save-only";
    }

    internal sealed class CorrectionContext
    {
        public string AffectedRecord { get; set; }
        public string Question { get; set; }
        public string CurrentProposal { get; set; }
        public List<IdentityQuestionChoice> Choices { get; set; } = new List<IdentityQuestionChoice>();
        public List<LabelItem> RelevantRecords { get; set; } = new List<LabelItem>();
        public long RunId { get; set; }
        public string CaseId { get; set; }
        public string QuestionId { get; set; }
    }

    internal sealed class CorrectionChoiceDialogView : DialogViewBase
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;
        private readonly IPluginUIView parent;
        private readonly Action rebuildParent;
        private readonly CorrectionContext context;
        private int selectedIndex = -1;
        private string status;

        private CorrectionChoiceDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, CorrectionContext context) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.logger = logger; this.parent = parent; this.rebuildParent = rebuildParent; this.context = context;
            AllowCancel = true; AllowOk = false; Rebuild();
        }

        public static CorrectionChoiceDialogView ForIdentity(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, IdentityCasePlan plan, string outcomeId)
        {
            var outcome = plan.Outcomes.First(x => x.OutcomeId == outcomeId);
            var question = plan.Questions.FirstOrDefault(x => x.OutcomeId == outcomeId);
            var context = new CorrectionContext
            {
                AffectedRecord = outcome.DisplayName + " — " + string.Join(", ", outcome.ProviderIds.Select(x => x.Provider.ToUpperInvariant() + " " + x.ProviderId)),
                Question = question?.Narrative ?? "Is this provider identity assigned to the correct Emby person, and are its provider records associated correctly?",
                CurrentProposal = outcome.Outcome,
                Choices = question == null ? IdentityChoices(plan, outcome) : question.Choices
            };
            context.RunId = plan.RunId; context.CaseId = plan.CaseId; context.QuestionId = question?.QuestionId ?? "operator-identity:" + outcome.OutcomeId;
            string serverId = null; try { serverId = host.GetPublicSystemInfo(CancellationToken.None).GetAwaiter().GetResult()?.Id; } catch { }
            foreach (var id in outcome.SourceEmbyIds.Concat(outcome.TargetEmbyId.HasValue ? new[] { outcome.TargetEmbyId.Value } : new long[0]).Distinct()) context.RelevantRecords.Add(Link("Emby person " + id, EmbyUrl(id, serverId)));
            foreach (var id in outcome.ProviderIds) context.RelevantRecords.Add(Link(id.Provider.ToUpperInvariant() + " person " + id.ProviderId, PersonUrl(id.Provider, id.ProviderId)));
            if (context.Choices.Count == 0) throw new InvalidOperationException("This identity has no valid correction choice in the persisted case.");
            return new CorrectionChoiceDialogView(plugin, host, logger, parent, rebuildParent, context);
        }

        public static CorrectionChoiceDialogView ForMedia(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, IdentityCasePlan plan, string assignmentId, bool role)
        {
            var credit = plan.Credits.First(x => x.AssignmentId == assignmentId);
            var target = plan.Outcomes.First(x => x.OutcomeId == credit.TargetOutcomeId);
            var context = new CorrectionContext
            {
                AffectedRecord = credit.MediaName + " — Emby " + credit.MediaEmbyId + " — " + credit.Role,
                Question = role ? "Which provider role attribution is correct?" : "Which Emby person should receive this media credit?",
                CurrentProposal = credit.CorrectionRequired ? "Correction required" : credit.Disposition + " to " + target.DisplayName,
                Choices = role ? RoleChoices(host, plan, credit) : AssignmentChoices(plan, credit)
            };
            context.RunId = plan.RunId; context.CaseId = plan.CaseId; context.QuestionId = role ? "operator-role:" + credit.AssignmentId : plan.Questions.FirstOrDefault(x => x.AssignmentId == assignmentId)?.QuestionId ?? "operator-assignment:" + credit.AssignmentId;
            string serverId = null; try { serverId = host.GetPublicSystemInfo(CancellationToken.None).GetAwaiter().GetResult()?.Id; } catch { }
            context.RelevantRecords.Add(Link("Emby media " + credit.MediaEmbyId + " — " + credit.MediaName, EmbyUrl(credit.MediaEmbyId, serverId)));
            if (!string.IsNullOrWhiteSpace(credit.TmdbId)) context.RelevantRecords.Add(Link("TMDB " + credit.MediaType + " " + credit.TmdbId, MediaUrl(ProviderNames.Tmdb, credit.MediaType, credit.TmdbId)));
            if (!string.IsNullOrWhiteSpace(credit.TvdbId)) context.RelevantRecords.Add(Link("TVDB " + credit.MediaType + " " + credit.TvdbId, MediaUrl(ProviderNames.Tvdb, credit.MediaType, credit.TvdbId, credit.TvdbSlug)));
            if (!string.IsNullOrWhiteSpace(credit.ImdbId)) context.RelevantRecords.Add(Link("IMDb title " + credit.ImdbId, MediaUrl(ProviderNames.Imdb, credit.MediaType, credit.ImdbId)));
            foreach (var id in target.ProviderIds.Where(x => x.Source == "native")) context.RelevantRecords.Add(Link(id.Provider.ToUpperInvariant() + " person " + id.ProviderId, PersonUrl(id.Provider, id.ProviderId)));
            var persisted = plan.Questions.FirstOrDefault(x => x.AssignmentId == assignmentId);
            if (!role && persisted != null) context.Choices = persisted.Choices;
            if (context.Choices.Count == 0) throw new InvalidOperationException(role ? "No alternative provider role attribution is present in this case." : "No valid media destination is present in this case.");
            return new CorrectionChoiceDialogView(plugin, host, logger, parent, rebuildParent, context);
        }

        public override string Caption => "Correct identity case";
        public override bool ShowDialogFullScreen => false;
        public override Task OnCancelCommand() => Task.CompletedTask;
        public override Task OnOkCommand(string providerId, string commandId, string data) => Task.CompletedTask;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, "DialogCancel", StringComparison.OrdinalIgnoreCase)) { rebuildParent(); return Task.FromResult(parent); }
            if ((commandId ?? string.Empty).StartsWith(CorrectionChoiceCommands.Select, StringComparison.Ordinal))
            {
                int index;
                if (int.TryParse(commandId.Substring(CorrectionChoiceCommands.Select.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < context.Choices.Count) selectedIndex = index;
                status = null; Rebuild(); Refresh(); return Task.FromResult<IPluginUIView>(this);
            }
            if (commandId == CorrectionChoiceCommands.Save || commandId == CorrectionChoiceCommands.SaveWithoutRecalculation)
            {
                try
                {
                    if (selectedIndex < 0 || selectedIndex >= context.Choices.Count) throw new InvalidOperationException("Select a correction before saving.");
                    var correction = context.Choices[selectedIndex].Correction;
                    using (var repository = new ResolutionRepository(paths))
                    {
                        repository.Initialize();
                        correction.CorrectionId = repository.SaveCorrectionChoice(correction, context.RunId, context.CaseId, context.QuestionId, context.Choices[selectedIndex].ChoiceId);
                        if (commandId == CorrectionChoiceCommands.Save) CorrectionRuntime.Recalculate(repository, logger);
                    }
                    logger.Info(commandId == CorrectionChoiceCommands.Save
                        ? "PersonCleaner saved contextual provider correction {0} ({1}) and recalculated its identity case."
                        : "PersonCleaner saved contextual provider correction {0} ({1}) for later batch recalculation.", correction.CorrectionId, correction.Kind);
                    rebuildParent(); return Task.FromResult(parent);
                }
                catch (Exception ex)
                {
                    status = "Correction was not saved: " + ex.Message; logger.ErrorException("Unable to save contextual PersonCleaner correction", ex); Rebuild(); Refresh();
                }
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        private void Rebuild() { ContentData = CorrectionChoiceDialogUI.Build(context, selectedIndex, status); }

        private static List<IdentityQuestionChoice> AssignmentChoices(IdentityCasePlan plan, IdentityCreditOutcome credit)
        {
            var result = new List<IdentityQuestionChoice>();
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind != IdentityTargetKinds.Unresolved && x.ProviderIds.Any()))
            {
                var replacement = outcome.TargetKind == IdentityTargetKinds.Existing ? "existing:" + outcome.TargetEmbyId : "outcome:" + outcome.OutcomeId;
                var caption = "Emby person: " + (outcome.TargetKind == IdentityTargetKinds.New ? "New — " : string.Empty) + outcome.DisplayName + (outcome.TargetEmbyId.HasValue ? " / " + outcome.TargetEmbyId : string.Empty);
                result.Add(Choice(caption, "The recalculated case will assign this credit to " + caption.Substring("Emby person: ".Length) + ".", new ProviderCorrection { Kind = CorrectionKinds.LocalCreditTarget, Operation = CorrectionOperations.Replace, EmbyId = credit.MediaEmbyId, CurrentValue = credit.SourcePersonEmbyId + "|" + credit.Role, ReplacementValue = replacement, Reason = "OPERATOR_MEDIA_ASSIGNMENT", Note = "Operator correction from case " + plan.CaseId, Enabled = true }));
                var native = outcome.ProviderIds.FirstOrDefault(x => x.Source == "native");
                if (outcome.TargetKind == IdentityTargetKinds.Existing && native != null)
                    result.Add(Choice("Emby person: New — " + outcome.DisplayName + " / " + native.Provider.ToUpperInvariant() + " " + native.ProviderId, "Recalculate this provider identity as a provider-identified new person; every affected media assignment will be shown for review before Apply.", new ProviderCorrection { Kind = CorrectionKinds.IdentityTarget, Operation = CorrectionOperations.Replace, Provider = native.Provider, ProviderPersonId = native.ProviderId, ReplacementValue = "new", Reason = "OPERATOR_IDENTITY_TARGET", Note = "Selected while correcting media in case " + plan.CaseId, Enabled = true }));
            }
            return result;
        }

        private static List<IdentityQuestionChoice> IdentityChoices(IdentityCasePlan plan, IdentityOutcome outcome)
        {
            var result = new List<IdentityQuestionChoice>();
            var native = outcome.ProviderIds.Where(x => x.Source == "native").ToList();
            var key = native.FirstOrDefault();
            if (key != null)
            {
                foreach (var id in plan.Outcomes.SelectMany(x => x.SourceEmbyIds.Concat(x.TargetEmbyId.HasValue ? new[] { x.TargetEmbyId.Value } : new long[0])).Distinct().OrderBy(x => x))
                {
                    var candidate = plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == id || x.SourceEmbyIds.Contains(id));
                    result.Add(Choice("Emby person: " + (candidate?.DisplayName ?? "Existing person") + " / " + id, "Assign this provider identity and its resulting credits to existing Emby person " + id + ".", new ProviderCorrection { Kind = CorrectionKinds.IdentityTarget, Operation = CorrectionOperations.Replace, Provider = key.Provider, ProviderPersonId = key.ProviderId, ReplacementValue = "existing:" + id, Reason = "OPERATOR_IDENTITY_TARGET", Note = "Operator correction from case " + plan.CaseId, Enabled = true }));
                }
                if (plan.Credits.Any(x => x.TargetOutcomeId == outcome.OutcomeId)) result.Add(Choice("Emby person: New provider-identified person", "Create a new person using this outcome's provider-native IDs and assigned media.", new ProviderCorrection { Kind = CorrectionKinds.IdentityTarget, Operation = CorrectionOperations.Replace, Provider = key.Provider, ProviderPersonId = key.ProviderId, ReplacementValue = "new", Reason = "OPERATOR_IDENTITY_TARGET", Note = "Operator correction from case " + plan.CaseId, Enabled = true }));
            }
            foreach (var pair in native.SelectMany((x, i) => native.Skip(i + 1).Where(y => y.Provider != x.Provider).Select(y => new { Left = x, Right = y })))
            {
                result.Add(Choice("Provider relationship: " + pair.Left.Provider.ToUpperInvariant() + " " + pair.Left.ProviderId + " and " + pair.Right.Provider.ToUpperInvariant() + " " + pair.Right.ProviderId + " are the same person", "The provider records will be joined durably before the case is recalculated.", Relation(pair.Left, pair.Right, CorrectionOperations.Same, plan.CaseId)));
                result.Add(Choice("Provider relationship: " + pair.Left.Provider.ToUpperInvariant() + " " + pair.Left.ProviderId + " and " + pair.Right.Provider.ToUpperInvariant() + " " + pair.Right.ProviderId + " are different people", "The provider records will remain separated through transitive clustering.", Relation(pair.Left, pair.Right, CorrectionOperations.Different, plan.CaseId)));
            }
            foreach (var owner in native)
            foreach (var replacement in plan.Outcomes.SelectMany(x => x.ProviderIds).Where(x => x.Source == "native" && x.Provider != owner.Provider).GroupBy(x => x.Provider + ":" + x.ProviderId).Select(x => x.First()))
                result.Add(Choice("External ID: " + owner.Provider.ToUpperInvariant() + " " + owner.ProviderId + " → " + replacement.Provider.ToUpperInvariant() + " " + replacement.ProviderId, "Replace this provider record's " + replacement.Provider.ToUpperInvariant() + " cross-reference and recalculate all identity relationships.", new ProviderCorrection { Kind = CorrectionKinds.PersonExternalId, Operation = CorrectionOperations.Replace, Provider = owner.Provider, ProviderPersonId = owner.ProviderId, FieldName = replacement.Provider, ReplacementValue = replacement.ProviderId, Reason = "OPERATOR_EXTERNAL_ID", Note = "Operator correction from case " + plan.CaseId, Enabled = true }));
            return result.GroupBy(x => x.Caption, StringComparer.Ordinal).Select(x => x.First()).ToList();
        }

        private static List<IdentityQuestionChoice> RoleChoices(IServerApplicationHost host, IdentityCasePlan plan, IdentityCreditOutcome credit)
        {
            var paths = host.Resolve<IApplicationPaths>();
            using (var repository = new ResolutionRepository(paths))
            {
                repository.Initialize();
                return repository.RoleCorrectionChoices(plan.CaseId, credit).Select(x => Choice(x.Caption, x.Effect, x.Correction)).ToList();
            }
        }

        private static ProviderCorrection Relation(IdentityProviderId a, IdentityProviderId b, string operation, string caseId) => new ProviderCorrection { Kind = CorrectionKinds.IdentityRelation, Operation = operation, Provider = a.Provider, ProviderPersonId = a.ProviderId, SecondaryProvider = b.Provider, SecondaryId = b.ProviderId, Reason = "OPERATOR_IDENTITY_RELATION", Note = "Operator correction from case " + caseId, Enabled = true };
        private static IdentityQuestionChoice Choice(string caption, string effect, ProviderCorrection correction) => new IdentityQuestionChoice { ChoiceId = Guid.NewGuid().ToString("N"), Caption = caption, Effect = effect, Correction = correction };
        private static LabelItem Link(string text, string url) => new LabelItem(text) { HyperLink = url };
        private static string EmbyUrl(long id, string serverId) => string.IsNullOrWhiteSpace(serverId) ? null : "#!/item?id=" + id + "&serverId=" + Uri.EscapeDataString(serverId);
        private static string PersonUrl(string provider, string id) => provider == ProviderNames.Tmdb ? "https://www.themoviedb.org/person/" + Uri.EscapeDataString(id) : provider == ProviderNames.Tvdb ? "https://thetvdb.com/people/" + Uri.EscapeDataString(id) : "https://www.imdb.com/name/" + Uri.EscapeDataString(id) + "/";
        private static string MediaUrl(string provider, string type, string id, string slug = null) => provider == ProviderNames.Tmdb ? "https://www.themoviedb.org/" + (type == MediaTypes.Series ? "tv/" : "movie/") + Uri.EscapeDataString(id) : provider == ProviderNames.Tvdb ? string.IsNullOrWhiteSpace(slug) ? "https://thetvdb.com/search?query=" + Uri.EscapeDataString(id) : "https://thetvdb.com/" + (type == MediaTypes.Series ? "series/" : "movies/") + Uri.EscapeDataString(slug) : "https://www.imdb.com/title/" + Uri.EscapeDataString(id) + "/";
    }
}
