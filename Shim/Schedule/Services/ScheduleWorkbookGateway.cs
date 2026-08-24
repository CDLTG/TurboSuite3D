#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using TurboSuite.Abstractions;
using TurboSuite.Schedule.Models;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Schedule.Services
{
    /// <summary>
    /// Shim implementation of <see cref="IScheduleWorkbookGateway"/>. Owns the Revit/dialog/file half of
    /// the round-trip and keeps the correct thread discipline: file dialogs and message boxes run on the
    /// WPF thread, every Revit read/write (ES path load/save, collect, writer transaction) goes through the
    /// <see cref="IRevitWorkQueue"/> on the API thread. Callbacks land back on the WPF thread.
    ///
    /// <para><c>doc.PathName</c>/<c>Title</c> are captured at construction (on the API thread, in
    /// <c>ScheduleCommand</c>) so the wrong-project check and the Save-As default can run on the UI thread
    /// without a Revit call.</para>
    /// </summary>
    public class ScheduleWorkbookGateway : IScheduleWorkbookGateway
    {
        private readonly Document _doc;
        private readonly IRevitWorkQueue _workQueue;
        private readonly IScheduleWriter _writer;
        private readonly string _docPathName;
        private readonly string _docTitle;

        public ScheduleWorkbookGateway(Document doc, IRevitWorkQueue workQueue, IScheduleWriter writer)
        {
            _doc = doc;
            _workQueue = workQueue;
            _writer = writer;
            _docPathName = doc.PathName ?? "";
            _docTitle = doc.Title ?? "Schedule";
        }

        private class SyncStage { public SyncReport Report; public IReadOnlyList<FixtureTypeSpec> Refreshed; }

        // ── One bidirectional reconcile (the single "Sync workbook" button) ────────────────

        public void ReconcileWorkbook(Action<ReconcileResult> onDone, Action<string> onError, Action onCancelled)
        {
            // 1) API thread: read the stored path.
            _workQueue.Enqueue(
                () => (object)WorkbookPathStorageService.Load(_doc),
                stored =>
                {
                    // 2) UI thread: decide first-run/recreate (seed only) vs full reconcile.
                    string path = stored as string ?? "";

                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        SeedOnly(path, onDone, onError, onCancelled);
                        return;
                    }

                    if (FileLockHelper.IsFileLocked(path)) { onError(LockMessage(path)); return; }

                    WorkbookSnapshot snap;
                    try { snap = ScheduleWorkbookIo.Read(path); }
                    catch (Exception ex) { onError($"Couldn't read the workbook:\n{ex.Message}"); return; }

                    if (IsWrongProject(snap.Meta) && !ConfirmWrongProject(snap.Meta)) { onCancelled?.Invoke(); return; }

                    // 3) API thread: collect → plan → write model (one transaction) → re-collect.
                    string p2 = path;
                    _workQueue.Enqueue(
                        () =>
                        {
                            var current = ScheduleTypeCollector.Collect(_doc);
                            var (reqs, report) = ScheduleSyncPlanner.Plan(snap, current);
                            if (report.Blocking)
                                return (object)new SyncStage { Report = report, Refreshed = null };

                            if (reqs.Count > 0)
                            {
                                var wr = _writer.Write(reqs);
                                report.WriterSkipped.AddRange(wr.Skipped);
                            }
                            var refreshed = ScheduleTypeCollector.Collect(_doc);
                            return (object)new SyncStage { Report = report, Refreshed = refreshed };
                        },
                        stageObj =>
                        {
                            // 4) UI thread: a blocking plan writes nothing; otherwise refresh the workbook
                            // from the now-current model (append / flag / purge), then hand it all back.
                            if (stageObj is not SyncStage stage) { onError("Sync failed."); return; }
                            if (stage.Report.Blocking)
                            {
                                onDone(new ReconcileResult { SyncReport = stage.Report, SeededOnly = false });
                                return;
                            }

                            WorkbookUpdateResult upd;
                            try { upd = ScheduleWorkbookIo.WriteAddOnly(p2, stage.Refreshed, BuildMeta()); }
                            catch (Exception ex)
                            {
                                upd = new WorkbookUpdateResult();
                                stage.Report.WriterSkipped.Add($"(workbook refresh failed — {ex.Message})");
                            }
                            onDone(new ReconcileResult
                            {
                                SyncReport = stage.Report,
                                Update = upd,
                                Refreshed = stage.Refreshed,
                                SeededOnly = false,
                            });
                        });
                });
        }

        // First run (no stored path → Save-As) or recreate (stored path but file gone → reuse it): seed the
        // workbook from the model with nothing to pull.
        private void SeedOnly(string storedPath, Action<ReconcileResult> onDone, Action<string> onError, Action onCancelled)
        {
            string path = storedPath;
            bool newlyChosen = false;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = PromptSaveAs();
                if (path == null) { onCancelled?.Invoke(); return; }
                newlyChosen = true;
            }
            if (FileLockHelper.IsFileLocked(path)) { onError(LockMessage(path)); return; }

            string chosen = path;
            _workQueue.Enqueue(
                () =>
                {
                    if (newlyChosen) WorkbookPathStorageService.Save(_doc, chosen);
                    return (object)ScheduleTypeCollector.Collect(_doc);
                },
                modelObj =>
                {
                    if (modelObj is not List<FixtureTypeSpec> model) { onError("Sync failed."); return; }
                    try
                    {
                        var upd = ScheduleWorkbookIo.WriteAddOnly(chosen, model, BuildMeta());
                        onDone(new ReconcileResult { Update = upd, SeededOnly = true });
                    }
                    catch (Exception ex) { onError(WriteError(ex, chosen)); }
                });
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────

        private WorkbookMeta BuildMeta() => new WorkbookMeta
        {
            ProjectPath = _docPathName,
            RevitVersion = UpdateConstants.RevitVersion ?? "",
            LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        private string PromptSaveAs()
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Excel Workbook|*.xlsx",
                Title = "Choose where to save the schedule workbook",
                FileName = DefaultFileName(),
                AddExtension = true,
                DefaultExt = ".xlsx",
            };
            var dir = SafeDir(_docPathName);
            if (dir != null) dlg.InitialDirectory = dir;
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private string DefaultFileName()
        {
            var baseName = string.IsNullOrWhiteSpace(_docPathName)
                ? _docTitle
                : Path.GetFileNameWithoutExtension(_docPathName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Schedule";
            return baseName + "_Schedule.xlsx";
        }

        private static string SafeDir(string pathName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathName)) return null;
                var dir = Path.GetDirectoryName(pathName);
                return Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        private bool IsWrongProject(WorkbookMeta meta) =>
            !string.IsNullOrEmpty(meta.ProjectPath) && !string.IsNullOrEmpty(_docPathName) &&
            !string.Equals(meta.ProjectPath, _docPathName, StringComparison.OrdinalIgnoreCase);

        private bool ConfirmWrongProject(WorkbookMeta meta) =>
            MessageBox.Show(
                $"This workbook was last updated for a different project:\n\n{meta.ProjectPath}\n\n" +
                $"Current project:\n{_docPathName}\n\nSync it into the current project anyway?",
                "TurboSchedule — workbook/project mismatch",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        private static string LockMessage(string path) =>
            $"{Path.GetFileName(path)} is open in Excel (or by another user).\n\nClose it and try again.";

        private static string WriteError(Exception ex, string path) =>
            ex is IOException
                ? $"Couldn't write {Path.GetFileName(path)} — it may be open in Excel. Close it and try again."
                : $"Couldn't write the workbook:\n{ex.Message}";
    }
}
