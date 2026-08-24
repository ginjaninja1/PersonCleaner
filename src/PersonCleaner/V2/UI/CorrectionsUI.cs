using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class CorrectionsUI : EditableOptionsBase
    {
        public override string EditorTitle => "Provider corrections";
        public override string EditorDescription => "Persistent operator-owned rules change only the effective evidence used for analysis. Raw provider payloads and flattened source facts remain untouched.";
        public CaptionItem Summary { get; set; }
        public GenericItemList AddCorrection { get; set; } = new GenericItemList();
        public GenericItemList Corrections { get; set; } = new GenericItemList();
    }

    public abstract class CorrectionEditorUIBase : EditableOptionsBase
    {
        public override string EditorTitle => null;
        public override string EditorDescription => null;
        [DisplayName("Reason tag")]
        [Description("A short durable classification, for example PROVIDER_MISMATCH.")]
        public string Reason { get; set; } = "PROVIDER_MISMATCH";
        [DisplayName("Review note")]
        [Description("Optional explanation or provider issue reference.")]
        public string Note { get; set; }
        [Browsable(false)]
        public IEnumerable<EditorRadioOption> ProviderChoices { get; set; } = new[]
        {
            new EditorRadioOption(ProviderNames.Tvdb, "TVDB", "TheTVDB provider data"),
            new EditorRadioOption(ProviderNames.Tmdb, "TMDB", "The Movie Database provider data")
        };
        [Browsable(false)]
        public IEnumerable<EditorRadioOption> MediaTypeChoices { get; set; } = new[]
        {
            new EditorRadioOption(MediaTypes.Movie, "Movie", "A movie provider record"),
            new EditorRadioOption(MediaTypes.Series, "Series", "A television series provider record")
        };
        public ButtonItem SaveCorrection { get; set; } = new ButtonItem("Save correction") { CommandId = CorrectionCommands.Save };
        public CaptionItem ValidationStatus { get; set; } = new CaptionItem(string.Empty);
    }

    public sealed class MediaAttributionCorrectionUI : CorrectionEditorUIBase
    {
        [DisplayName("Provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("Media type")]
        [SelectItemsSource(nameof(MediaTypeChoices))]
        [SelectShowRadioGroup]
        public string MediaType { get; set; }
        [DisplayName("Provider media ID")]
        public string ProviderMediaId { get; set; }
        [DisplayName("Currently assigned person ID")]
        public string ProviderPersonId { get; set; }
        [DisplayName("Credit or role (optional)")]
        [Description("When supplied, only that exact provider credit is corrected.")]
        public string CurrentValue { get; set; }
        [DisplayName("Correct person ID (optional)")]
        [Description("Leave blank when the provider has no usable person identity for this credit.")]
        public string ReplacementValue { get; set; }
    }

    public sealed class MediaRoleCorrectionUI : CorrectionEditorUIBase
    {
        [DisplayName("Provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("Media type")]
        [SelectItemsSource(nameof(MediaTypeChoices))]
        [SelectShowRadioGroup]
        public string MediaType { get; set; }
        [DisplayName("Provider media ID")]
        public string ProviderMediaId { get; set; }
        [DisplayName("Provider person ID")]
        public string ProviderPersonId { get; set; }
        [DisplayName("Current role")]
        public string CurrentValue { get; set; }
        [DisplayName("Correct role (optional)")]
        [Description("Leave blank when the provider role is unusable but the person attribution is still valid.")]
        public string ReplacementValue { get; set; }
    }

    public sealed class PersonExternalIdCorrectionUI : CorrectionEditorUIBase
    {
        [DisplayName("Provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("Provider person ID")]
        public string ProviderPersonId { get; set; }
        [DisplayName("Cross-reference provider")]
        [Description("For example imdb, wikidata, tmdb or tvdb.")]
        public string FieldName { get; set; }
        [DisplayName("Current cross-reference (optional)")]
        public string CurrentValue { get; set; }
        [DisplayName("Correct cross-reference (optional)")]
        [Description("Leave blank when the current cross-reference is unusable and no replacement is known.")]
        public string ReplacementValue { get; set; }
    }

    public sealed class MediaExternalIdCorrectionUI : CorrectionEditorUIBase
    {
        [DisplayName("Provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("Media type")]
        [SelectItemsSource(nameof(MediaTypeChoices))]
        [SelectShowRadioGroup]
        public string MediaType { get; set; }
        [DisplayName("Provider media ID")]
        public string ProviderMediaId { get; set; }
        [DisplayName("Cross-reference provider")]
        public string FieldName { get; set; }
        [DisplayName("Current cross-reference (optional)")]
        public string CurrentValue { get; set; }
        [DisplayName("Correct cross-reference (optional)")]
        [Description("Leave blank when the current production cross-reference is unusable.")]
        public string ReplacementValue { get; set; }
    }

    public sealed class PersonFieldCorrectionUI : CorrectionEditorUIBase
    {
        [DisplayName("Provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("Provider person ID")]
        public string ProviderPersonId { get; set; }
        [DisplayName("Person field")]
        [Description("Enter name or birthday.")]
        public string FieldName { get; set; }
        [DisplayName("Current value (optional)")]
        public string CurrentValue { get; set; }
        [DisplayName("Correct value (optional)")]
        [Description("Leave blank when the provider value is unusable and no replacement is known.")]
        public string ReplacementValue { get; set; }
    }

    public sealed class LocalBindingCorrectionUI : CorrectionEditorUIBase
    {
        [DisplayName("Local subject")]
        [Description("Enter person or media.")]
        public string Subject { get; set; }
        [DisplayName("Emby ID")]
        public long EmbyId { get; set; }
        [DisplayName("Provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("Current provider ID (optional)")]
        public string CurrentValue { get; set; }
        [DisplayName("Correct provider ID (optional)")]
        [Description("Leave blank when the local provider binding is unusable.")]
        public string ReplacementValue { get; set; }
    }

    public sealed class IdentityRelationCorrectionUI : CorrectionEditorUIBase
    {
        [Browsable(false)]
        public string Relation { get; set; }
        [DisplayName("First provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string Provider { get; set; }
        [DisplayName("First person ID")]
        public string ProviderPersonId { get; set; }
        [DisplayName("Second provider")]
        [SelectItemsSource(nameof(ProviderChoices))]
        [SelectShowRadioGroup]
        public string SecondaryProvider { get; set; }
        [DisplayName("Second person ID")]
        public string SecondaryId { get; set; }
    }

    internal static class CorrectionCommands
    {
        public const string Save = "correction-save";
        public const string AddCredit = "correction-add-credit";
        public const string AddRole = "correction-add-role";
        public const string AddPersonExternal = "correction-add-person-external";
        public const string AddMediaExternal = "correction-add-media-external";
        public const string AddPersonField = "correction-add-person-field";
        public const string AddLocalBinding = "correction-add-local-binding";
        public const string AddSame = "correction-add-same";
        public const string AddDifferent = "correction-add-different";
        public static string Edit(long id) => "correction-edit:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Toggle(long id) => "correction-toggle:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Delete(long id) => "correction-delete:" + id.ToString(CultureInfo.InvariantCulture);
        public static bool TryId(string command, string prefix, out long id) => long.TryParse((command ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal) ? command.Substring(prefix.Length) : string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    internal sealed class CorrectionsPageView : PageViewBase
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;
        private string result;

        public CorrectionsPageView(PluginInfo plugin, IServerApplicationHost host, ILogger logger) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.logger = logger; ShowSave = false; Rebuild();
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            try
            {
                var kind = AddKind(commandId);
                if (kind != null) return Task.FromResult<IPluginUIView>(new CorrectionDialogView(plugin, host, logger, this, Rebuild, NewRule(kind, commandId)));
                if (CorrectionCommands.TryId(commandId, "correction-edit:", out var id))
                {
                    using (var repository = Open())
                    {
                        var correction = repository.GetCorrection(id); if (correction == null) throw new InvalidOperationException("Correction " + id + " no longer exists.");
                        return Task.FromResult<IPluginUIView>(new CorrectionDialogView(plugin, host, logger, this, Rebuild, correction));
                    }
                }
                if (CorrectionCommands.TryId(commandId, "correction-toggle:", out id))
                {
                    using (var repository = Open())
                    {
                        var correction = repository.GetCorrection(id); if (correction == null) throw new InvalidOperationException("Correction " + id + " no longer exists.");
                        repository.SetCorrectionEnabled(id, !correction.Enabled); CorrectionRuntime.Recalculate(repository, logger);
                        result = "Correction " + id + (correction.Enabled ? " disabled." : " enabled and applied to cached evidence.");
                    }
                }
                else if (CorrectionCommands.TryId(commandId, "correction-delete:", out id))
                {
                    using (var repository = Open()) { repository.DeleteCorrection(id); CorrectionRuntime.Recalculate(repository, logger); }
                    result = "Correction " + id + " removed and cached evidence recalculated.";
                }
            }
            catch (Exception ex) { result = "Correction operation failed: " + ex.Message; logger.ErrorException("Unable to manage PersonCleaner provider corrections", ex); }
            Rebuild(); Refresh(); return Task.FromResult<IPluginUIView>(this);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { Rebuild(); Refresh(); }

        private ResolutionRepository Open() { var repository = new ResolutionRepository(paths); repository.Initialize(); return repository; }
        private void Rebuild()
        {
            try
            {
                using (var repository = Open()) ContentData = Build(repository.Corrections(), result);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to load PersonCleaner provider corrections", ex);
                ContentData = Build(new CorrectionReviewRow[0], "Corrections could not be loaded: " + ex.Message);
            }
        }

        private static CorrectionsUI Build(IEnumerable<CorrectionReviewRow> source, string result)
        {
            var rows = source.ToList(); var active = rows.Count(x => x.Correction.Enabled); var triggered = rows.Count(x => x.Correction.Enabled && x.LastMatchedCount > 0);
            var ui = new CorrectionsUI
            {
                Summary = new CaptionItem((string.IsNullOrWhiteSpace(result) ? string.Empty : result + " ") + active + " active correction(s); " + triggered + " triggered in their most recently recorded run."),
                AddCorrection = new GenericItemList
                {
                    new GenericListItem
                    {
                        PrimaryText = "Add a provider correction",
                        SecondaryText = "Choose the simple provider fact that should be ignored or replaced.", Icon = IconNames.person,
                        Button1 = new ButtonItem
                        {
                            Caption = "Add correction",
                            SubMenuButtons = new List<ButtonItem>
                            {
                                new ButtonItem("Media person attribution") { CommandId = CorrectionCommands.AddCredit },
                                new ButtonItem("Media credit role") { CommandId = CorrectionCommands.AddRole },
                                new ButtonItem("Person cross-reference") { CommandId = CorrectionCommands.AddPersonExternal },
                                new ButtonItem("Media cross-reference") { CommandId = CorrectionCommands.AddMediaExternal },
                                new ButtonItem("Person name or birthday") { CommandId = CorrectionCommands.AddPersonField },
                                new ButtonItem("Local Emby provider binding") { CommandId = CorrectionCommands.AddLocalBinding },
                                new ButtonItem("Profiles are the same person") { CommandId = CorrectionCommands.AddSame },
                                new ButtonItem("Profiles are different people") { CommandId = CorrectionCommands.AddDifferent }
                            }
                        }
                    }
                }
            };
            foreach (var row in rows)
            {
                var correction = row.Correction;
                var last = row.LastRunId.HasValue ? "Run " + row.LastRunId + ": " + (row.LastMatchedCount > 0 ? "triggered" : "did not match") + ", changed " + row.LastChangedCount + " fact(s)." : "Not evaluated by a run yet.";
                ui.Corrections.Add(new GenericListItem
                {
                    PrimaryText = "#" + correction.CorrectionId + " · " + Friendly(correction),
                    SecondaryText = (correction.Enabled ? "Active. " : "Disabled. ") + last + (string.IsNullOrWhiteSpace(correction.Note) ? string.Empty : " Note: " + correction.Note),
                    Icon = IconNames.person, Status = correction.Enabled && row.LastMatchedCount > 0 ? ItemStatus.Succeeded : ItemStatus.Unavailable,
                    Button1 = new ButtonItem("Edit") { CommandId = CorrectionCommands.Edit(correction.CorrectionId) },
                    Button2 = new ButtonItem
                    {
                        Caption = "Manage",
                        SubMenuButtons = new List<ButtonItem>
                        {
                            new ButtonItem(correction.Enabled ? "Disable" : "Enable") { CommandId = CorrectionCommands.Toggle(correction.CorrectionId) },
                            new ButtonItem("Remove") { CommandId = CorrectionCommands.Delete(correction.CorrectionId), ConfirmationPrompt = "Permanently remove this correction and recalculate cached decisions?" }
                        }
                    }
                });
            }
            if (rows.Count == 0) ui.Corrections.Add(new GenericListItem { PrimaryText = "No provider corrections", SecondaryText = "Raw provider evidence is currently used without an operator overlay.", Icon = IconNames.person, Status = ItemStatus.Unavailable });
            return ui;
        }

        private static string Friendly(ProviderCorrection c)
        {
            if (c.Kind == CorrectionKinds.MediaCredit) return c.Provider.ToUpperInvariant() + " has " + (c.Operation == CorrectionOperations.Unusable ? "no usable person identity" : "person " + c.ReplacementValue) + " for " + c.MediaType + ":" + c.ProviderMediaId + " credit " + (string.IsNullOrWhiteSpace(c.CurrentValue) ? c.ProviderPersonId : c.CurrentValue);
            if (c.Kind == CorrectionKinds.MediaCreditRole) return c.Provider.ToUpperInvariant() + " role for " + c.MediaType + ":" + c.ProviderMediaId + " is " + (c.Operation == CorrectionOperations.Unusable ? "unusable" : c.ReplacementValue);
            if (c.Kind == CorrectionKinds.PersonExternalId) return c.Provider.ToUpperInvariant() + " person " + c.ProviderPersonId + " has " + Value(c) + " " + c.FieldName + " cross-reference";
            if (c.Kind == CorrectionKinds.MediaExternalId) return c.Provider.ToUpperInvariant() + " " + c.MediaType + ":" + c.ProviderMediaId + " has " + Value(c) + " " + c.FieldName + " cross-reference";
            if (c.Kind == CorrectionKinds.PersonField) return c.Provider.ToUpperInvariant() + " person " + c.ProviderPersonId + " has " + Value(c) + " " + c.FieldName;
            if (c.Kind == CorrectionKinds.LocalPersonBinding || c.Kind == CorrectionKinds.LocalMediaBinding) return "Emby " + (c.Kind == CorrectionKinds.LocalPersonBinding ? "person " : "media ") + c.EmbyId + " has " + Value(c) + " " + c.Provider.ToUpperInvariant() + " binding";
            return c.Provider + ":" + c.ProviderPersonId + " and " + c.SecondaryProvider + ":" + c.SecondaryId + " are " + c.Operation;
        }
        private static string Value(ProviderCorrection c) => c.Operation == CorrectionOperations.Unusable ? "no usable" : "replacement " + c.ReplacementValue;
        private static string AddKind(string command) => command == CorrectionCommands.AddCredit ? CorrectionKinds.MediaCredit : command == CorrectionCommands.AddRole ? CorrectionKinds.MediaCreditRole : command == CorrectionCommands.AddPersonExternal ? CorrectionKinds.PersonExternalId : command == CorrectionCommands.AddMediaExternal ? CorrectionKinds.MediaExternalId : command == CorrectionCommands.AddPersonField ? CorrectionKinds.PersonField : command == CorrectionCommands.AddLocalBinding ? CorrectionKinds.LocalPersonBinding : command == CorrectionCommands.AddSame || command == CorrectionCommands.AddDifferent ? CorrectionKinds.IdentityRelation : null;
        private static ProviderCorrection NewRule(string kind, string command) => new ProviderCorrection { Kind = kind, Operation = command == CorrectionCommands.AddSame ? CorrectionOperations.Same : command == CorrectionCommands.AddDifferent ? CorrectionOperations.Different : CorrectionOperations.Unusable, Reason = "PROVIDER_MISMATCH", Enabled = true };
    }

    internal sealed class CorrectionDialogView : DialogViewBase
    {
        private readonly IApplicationPaths paths;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly IPluginUIView parent;
        private readonly Action rebuildParent;
        private ProviderCorrection correction;

        public CorrectionDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, ProviderCorrection correction) : base(plugin.Id)
        {
            this.paths = host.Resolve<IApplicationPaths>(); this.json = host.Resolve<IJsonSerializer>(); this.logger = logger; this.parent = parent; this.rebuildParent = rebuildParent; this.correction = correction;
            AllowCancel = true; AllowOk = false; ContentData = ToUI(correction);
        }
        public override bool ShowDialogFullScreen => false;
        public override string Caption => (correction.CorrectionId > 0 ? "Edit" : "Add") + " provider correction";

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, "DialogCancel", StringComparison.OrdinalIgnoreCase)) { rebuildParent(); return Task.FromResult(parent); }
            if (commandId != CorrectionCommands.Save) return Task.FromResult<IPluginUIView>(this);
            try
            {
                correction = FromUI(correction, data); correction.Enabled = correction.CorrectionId <= 0 || correction.Enabled;
                using (var repository = new ResolutionRepository(paths))
                {
                    repository.Initialize(); correction.CorrectionId = repository.SaveCorrection(correction); CorrectionRuntime.Recalculate(repository, logger);
                }
                logger.Info("PersonCleaner provider correction {0} saved: kind={1}, operation={2}, reason={3}.", correction.CorrectionId, correction.Kind, correction.Operation, correction.Reason);
                rebuildParent(); return Task.FromResult(parent);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to save PersonCleaner provider correction", ex);
                ContentData = ToUI(correction); ((CorrectionEditorUIBase)ContentData).ValidationStatus = new CaptionItem("Correction was not saved: " + ex.Message); Refresh(); return Task.FromResult<IPluginUIView>(this);
            }
        }

        private MediaBrowser.Model.GenericEdit.IEditableObject ToUI(ProviderCorrection c)
        {
            if (c.Kind == CorrectionKinds.MediaCredit) return Common(new MediaAttributionCorrectionUI { Provider = c.Provider, MediaType = c.MediaType, ProviderMediaId = c.ProviderMediaId, ProviderPersonId = c.ProviderPersonId, CurrentValue = c.CurrentValue, ReplacementValue = c.Operation == CorrectionOperations.Replace ? c.ReplacementValue : null }, c);
            if (c.Kind == CorrectionKinds.MediaCreditRole) return Common(new MediaRoleCorrectionUI { Provider = c.Provider, MediaType = c.MediaType, ProviderMediaId = c.ProviderMediaId, ProviderPersonId = c.ProviderPersonId, CurrentValue = c.CurrentValue, ReplacementValue = c.Operation == CorrectionOperations.Replace ? c.ReplacementValue : null }, c);
            if (c.Kind == CorrectionKinds.PersonExternalId) return Common(new PersonExternalIdCorrectionUI { Provider = c.Provider, ProviderPersonId = c.ProviderPersonId, FieldName = c.FieldName, CurrentValue = c.CurrentValue, ReplacementValue = c.Operation == CorrectionOperations.Replace ? c.ReplacementValue : null }, c);
            if (c.Kind == CorrectionKinds.MediaExternalId) return Common(new MediaExternalIdCorrectionUI { Provider = c.Provider, MediaType = c.MediaType, ProviderMediaId = c.ProviderMediaId, FieldName = c.FieldName, CurrentValue = c.CurrentValue, ReplacementValue = c.Operation == CorrectionOperations.Replace ? c.ReplacementValue : null }, c);
            if (c.Kind == CorrectionKinds.PersonField) return Common(new PersonFieldCorrectionUI { Provider = c.Provider, ProviderPersonId = c.ProviderPersonId, FieldName = c.FieldName, CurrentValue = c.CurrentValue, ReplacementValue = c.Operation == CorrectionOperations.Replace ? c.ReplacementValue : null }, c);
            if (c.Kind == CorrectionKinds.LocalPersonBinding || c.Kind == CorrectionKinds.LocalMediaBinding) return Common(new LocalBindingCorrectionUI { Subject = c.Kind == CorrectionKinds.LocalPersonBinding ? "person" : "media", EmbyId = c.EmbyId ?? 0, Provider = c.Provider, CurrentValue = c.CurrentValue, ReplacementValue = c.Operation == CorrectionOperations.Replace ? c.ReplacementValue : null }, c);
            return Common(new IdentityRelationCorrectionUI { Relation = c.Operation, Provider = c.Provider, ProviderPersonId = c.ProviderPersonId, SecondaryProvider = c.SecondaryProvider, SecondaryId = c.SecondaryId }, c);
        }

        private static T Common<T>(T ui, ProviderCorrection c) where T : CorrectionEditorUIBase { ui.Reason = c.Reason; ui.Note = c.Note; return ui; }
        private ProviderCorrection FromUI(ProviderCorrection original, string data)
        {
            var c = new ProviderCorrection { CorrectionId = original.CorrectionId, Kind = original.Kind, Enabled = original.CorrectionId <= 0 || original.Enabled, CreatedUtc = original.CreatedUtc, UpdatedUtc = original.UpdatedUtc };
            if (c.Kind == CorrectionKinds.MediaCredit)
            {
                var x = json.DeserializeFromString<MediaAttributionCorrectionUI>(data); SetCommon(c, x); c.Provider = x.Provider; c.MediaType = x.MediaType; c.ProviderMediaId = x.ProviderMediaId; c.ProviderPersonId = x.ProviderPersonId; c.CurrentValue = x.CurrentValue; c.ReplacementValue = x.ReplacementValue;
            }
            else if (c.Kind == CorrectionKinds.MediaCreditRole)
            {
                var x = json.DeserializeFromString<MediaRoleCorrectionUI>(data); SetCommon(c, x); c.Provider = x.Provider; c.MediaType = x.MediaType; c.ProviderMediaId = x.ProviderMediaId; c.ProviderPersonId = x.ProviderPersonId; c.CurrentValue = x.CurrentValue; c.ReplacementValue = x.ReplacementValue;
            }
            else if (c.Kind == CorrectionKinds.PersonExternalId)
            {
                var x = json.DeserializeFromString<PersonExternalIdCorrectionUI>(data); SetCommon(c, x); c.Provider = x.Provider; c.ProviderPersonId = x.ProviderPersonId; c.FieldName = x.FieldName; c.CurrentValue = x.CurrentValue; c.ReplacementValue = x.ReplacementValue;
            }
            else if (c.Kind == CorrectionKinds.MediaExternalId)
            {
                var x = json.DeserializeFromString<MediaExternalIdCorrectionUI>(data); SetCommon(c, x); c.Provider = x.Provider; c.MediaType = x.MediaType; c.ProviderMediaId = x.ProviderMediaId; c.FieldName = x.FieldName; c.CurrentValue = x.CurrentValue; c.ReplacementValue = x.ReplacementValue;
            }
            else if (c.Kind == CorrectionKinds.PersonField)
            {
                var x = json.DeserializeFromString<PersonFieldCorrectionUI>(data); SetCommon(c, x); c.Provider = x.Provider; c.ProviderPersonId = x.ProviderPersonId; c.FieldName = x.FieldName; c.CurrentValue = x.CurrentValue; c.ReplacementValue = x.ReplacementValue;
            }
            else if (c.Kind == CorrectionKinds.LocalPersonBinding || c.Kind == CorrectionKinds.LocalMediaBinding)
            {
                var x = json.DeserializeFromString<LocalBindingCorrectionUI>(data); SetCommon(c, x);
                if (!string.Equals(x.Subject, "person", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Subject, "media", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Local subject must be person or media.");
                c.Kind = string.Equals(x.Subject, "media", StringComparison.OrdinalIgnoreCase) ? CorrectionKinds.LocalMediaBinding : CorrectionKinds.LocalPersonBinding; c.EmbyId = x.EmbyId; c.Provider = x.Provider; c.CurrentValue = x.CurrentValue; c.ReplacementValue = x.ReplacementValue;
            }
            else
            {
                var x = json.DeserializeFromString<IdentityRelationCorrectionUI>(data); SetCommon(c, x); c.Operation = original.Operation; c.Provider = x.Provider; c.ProviderPersonId = x.ProviderPersonId; c.SecondaryProvider = x.SecondaryProvider; c.SecondaryId = x.SecondaryId; return c;
            }
            c.Operation = string.IsNullOrWhiteSpace(c.ReplacementValue) ? CorrectionOperations.Unusable : CorrectionOperations.Replace; return c;
        }
        private static void SetCommon(ProviderCorrection c, CorrectionEditorUIBase x) { if (x == null) throw new ArgumentException("Correction form data is missing."); c.Reason = x.Reason; c.Note = x.Note; }
    }

    internal static class CorrectionRuntime
    {
        public static void Recalculate(ResolutionRepository repository, ILogger logger)
        {
            var run = repository.LatestRun(); if (run == null || !string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)) return;
            var input = repository.LoadResolutionInput(run.RunId);
            foreach (var application in input.CorrectionApplications.Where(x => x.Triggered))
                logger.Info("PersonCleaner run {0} provider correction {1} triggered during cached recalculation: matched={2}, changed={3}. {4}", run.RunId, application.CorrectionId, application.MatchedCount, application.ChangedCount, application.Summary);
            var c = Plugin.Instance.Configuration;
            var engine = new ResolutionEngine(); var decisions = engine.Resolve(input, new ResolutionSettings { AutomaticMatchThreshold = c.AutomaticMatchThreshold, HumanReviewThreshold = c.HumanReviewThreshold, MaximumMediaExamples = c.MaximumMediaExamplesPerDecision });
            repository.SaveDecisions(run.RunId, decisions, engine.PairEvaluations, engine.Clusters);
        }
    }
}
