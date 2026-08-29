using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using PersonCleaner.V2.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.Tasks
{
    public sealed class MassCorrectionsTask : IScheduledTask
    {
        private readonly ILibraryManager library;
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;

        public string Name => "Person Cleaner - Apply high confidence corrections en masse";
        public string Key => "PersonCleanerMassCorrectionsV2";
        public string Description => "Backup database and \\programdata\\metadata\\people folder until tested or use a test server first.";
        public string Category => "GinjaNinja Tools";

        public MassCorrectionsTask(ILibraryManager library, IApplicationPaths paths, ILogManager logs)
        {
            this.library = library; this.paths = paths; logger = logs.GetLogger("PersonCleaner v2 mass corrections");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var configuration = Plugin.Instance.Configuration;
            if (!configuration.EnablePlugin) { logger.Info("PersonCleaner is disabled; Mass Corrections made no changes."); return Task.CompletedTask; }
            if (!configuration.EnableMassCorrectionsTask) { logger.Info("PersonCleaner Mass Corrections is disabled by configuration; no changes were made."); return Task.CompletedTask; }

            using (var repository = new ResolutionRepository(paths))
            {
                repository.Initialize();
                var caseIds = repository.AutoApplicableCaseIds();
                if (caseIds.Count == 0) { logger.Info("PersonCleaner Mass Corrections found no unapplied, satisfied changes in the latest completed evidence run."); progress.Report(100); return Task.CompletedTask; }

                var executor = new IdentityCaseExecutor(library);
                var failures = new List<string>(); var applied = 0; var skipped = 0;
                logger.Info("PersonCleaner Mass Corrections starting {0} persisted, satisfied case(s). Problem and no-change cases were excluded by the database query.", caseIds.Count);
                for (var index = 0; index < caseIds.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var caseId = caseIds[index];
                    try
                    {
                        var plan = repository.IdentityCase(caseId);
                        if (plan.State != IdentityPlanStates.Complete || plan.PresentationPurpose != CasePresentationPurposes.SatisfiedChange || !IdentityCasePlanner.HasMutations(plan))
                            throw new InvalidOperationException("The persisted case is no longer classified as a complete, satisfied change.");
                        var draft = PersonBuilderDraft.FromPlan(plan);
                        var duplicateIds = IdentityCasePersonBuilder.DuplicateProviderIdKeys(draft);
                        if (duplicateIds.Count > 0)
                        {
                            skipped++;
                            logger.Warn("PersonCleaner Mass Corrections skipped case {0} because its default layout assigns provider ID(s) {1} to multiple active people; review and apply this case manually.", caseId, string.Join(", ", duplicateIds));
                            progress.Report(100.0 * (index + 1) / caseIds.Count);
                            continue;
                        }
                        var compilation = IdentityCasePersonBuilder.Compile(plan, draft);
                        var before = IdentityApplyAudit.CaptureBefore(compilation.Plan, library);
                        var receipt = executor.Apply(compilation.Plan, committed => repository.CommitIdentityCase(compilation, committed));
                        IdentityApplyAudit.Log(compilation.Plan, receipt, before, library, logger);
                        applied++;
                        logger.Info("PersonCleaner Mass Corrections applied case {0}: {1}", caseId, receipt.Summary);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(caseId + ": " + ex.Message);
                        var rollbackFailed = ex.Message.IndexOf("rollback also failed", StringComparison.OrdinalIgnoreCase) >= 0;
                        logger.ErrorException("PersonCleaner Mass Corrections could not apply case " + caseId + (rollbackFailed ? "; processing will stop because rollback was not safe" : "; other independent satisfied cases will continue"), ex);
                        if (rollbackFailed)
                            throw new InvalidOperationException("Mass Corrections stopped immediately because case " + caseId + " failed and could not be rolled back safely.", ex);
                    }
                    progress.Report(100.0 * (index + 1) / caseIds.Count);
                }
                logger.Info("PersonCleaner Mass Corrections finished: {0} applied, {1} skipped for duplicate provider IDs, {2} failed, {3} selected.", applied, skipped, failures.Count, caseIds.Count);
                if (failures.Count > 0)
                    throw new InvalidOperationException("Mass Corrections applied " + applied + " case(s), but " + failures.Count + " case(s) failed live preflight or application: " + string.Join(" | ", failures.Take(10)));
            }
            return Task.CompletedTask;
        }
    }
}
