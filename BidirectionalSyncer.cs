using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Tasks.Offline;
using log4net;

namespace Mobile.SyncService
{
    public class BidirectionalSyncer
    {
        private bool _isConnected;
        private bool _isSynching;
        private readonly string _attachmentOutFolder;
        private readonly string _attachmentSourceFolder;
        private bool _canStartSync = default(bool);

        private TaskCompletionSource<bool> _startTcs;
        private TaskCompletionSource<bool> _stopTcs;
        private CancellationTokenSource _cancellationTokenSource;

        public bool Started { get; private set; }
        public static CancellationToken CancellationToken { get; set; } = default(CancellationToken);

        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public Task StartAsync()
        {
            if (_startTcs == null)
            {
                _stopTcs = null;
                _startTcs = new TaskCompletionSource<bool>();
                _cancellationTokenSource = new CancellationTokenSource();

                Started = true;

                //Task.Run(() => StartSync());
            }


            return _startTcs.Task;
        }

        public Task StopAsync()
        {
            if (_stopTcs == null)
            {
                _startTcs = null;
                _stopTcs = new TaskCompletionSource<bool>();

                _cancellationTokenSource?.Cancel(true);

                Started = false;
            }
            return _stopTcs.Task;
        }


        private async Task ContinousSyncAsync(TimeSpan period)
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                await Task.Delay(period, CancellationToken);

                if (CancellationToken.IsCancellationRequested)
                {
                    Log.Info("Sync was cancelled");
                    return;
                }
                if (!_isConnected)
                {
                    Log.Info("Not connected to server, skipping sync");
                    continue;
                }

                if (!_canStartSync)
                {
                    Log.Info("Map not loaded, delaying sync");
                    continue;
                }

                if (_isSynching)
                {
                    Log.Debug("Sync in progress - skipping background sync");
                    continue;
                }

                Log.Debug("Background sync beginning");
                _isSynching = true;

                await SyncGeodatabaes(SyncDirection.Bidirectional);

                _isSynching = false;
                Log.Debug("Background sync completed");

                FullFileSync(BidirectionalGdbs);

                EventAggregator.GetEvent<TableChangedEvent>().Publish(TableChangedEventArgs.Empty);
            }

        }

        private async Task SyncGeodatabaes(SyncDirection syncDirection)
        {
            foreach (var gdbPath in BidirectionalGdbs)
            {
                Geodatabase geodatabase = null;
                IReadOnlyList<SyncLayerResult> results = null;

                try
                {
                    geodatabase = await Geodatabase.OpenAsync(gdbPath);

                    Log.Debug($"{geodatabase.Path} about to start {syncDirection} sync");

                    if (!geodatabase.HasLocalEdits() && syncDirection == SyncDirection.Upload)
                    {
                        Log.Debug("No edits skipping sync");
                        continue;
                    }

                    Log.Debug($"ServiceUrl: {geodatabase.Source}");
                    GeodatabaseSyncTask syncTask = await GeodatabaseSyncTask.CreateAsync(geodatabase.Source);

                    SyncGeodatabaseParameters syncParameters = await syncTask.CreateDefaultSyncGeodatabaseParametersAsync(geodatabase);

                    syncParameters.GeodatabaseSyncDirection = syncDirection;

                    SyncGeodatabaseJob job = syncTask.SyncGeodatabase(syncParameters, geodatabase);

                    results = await job.GetResultAsync();

                    LogResults(results);
                }
                catch (Exception e)
                {
                    Log.Error(e.Message);
                    Log.Error($"{geodatabase?.Path} did not sync");
                    LogResults(results);
                }
            }
        }

        private void LogResults(IReadOnlyList<SyncLayerResult> results)
        {
            if (results == null) return;

            if (!results.Any())
            {
                Log.Debug("Sync completed successfully");
            }

            foreach (var syncLayerResult in results)
            {
                var editResults = syncLayerResult.EditResults;
                Log.Debug($"Result for {syncLayerResult.TableName}");
                foreach (var editResult in editResults)
                {
                    if (editResult.CompletedWithErrors)
                    {
                        Log.Warn($"Completed with error {editResult.EditOperation} - {editResult.GlobalId}", editResult.Error);
                    }
                    else
                    {
                        Log.Debug("Edit completed without error");
                    }
                }
            }
        }

        private void FullFileSync(IEnumerable<Geodatabase> geodatabases)
        {
            foreach (var gdb in geodatabases)
            {
                SyncFiles(gdb);
            }

            //Just do anytime do files, its fast
            SyncBreadCrumbs();
        }

        #region SyncFiles

        private void SyncFiles(Geodatabase gdb)
        {
            //Check if can connect to Network folder
            if (ConnectionCheck.IsConnected && !ConnectionCheck.IsDomainConnected)
            {
                ShowMessageIfRequired();

                //_userNotifiedRestart = true;
                return;
            }

            foreach (var table in gdb.GeodatabaseFeatureTables)
            {
                string clientAttachmentFolder = Path.Combine(_attachmentSourceFolder, table.TableName);
                if (!Directory.Exists(clientAttachmentFolder)) continue;

                int count = 0;
                foreach (string sourceFileName in Directory.GetFiles(clientAttachmentFolder))
                {
                    //Check indicator that file copied...
                    if (sourceFileName.EndsWith("_d")) continue;

                    FileInfo file = new FileInfo(sourceFileName);

                    string copyToFolder, copyToFile;
                    GetOutputLocations(table, file, out copyToFolder, out copyToFile);

                    // Shouldn't happen, but just in case
                    if (File.Exists(copyToFile)) continue;

                    if (!Directory.Exists(copyToFolder))
                    {
                        Directory.CreateDirectory(copyToFolder);
                    }

                    File.Copy(sourceFileName, copyToFile);
                    File.Move(sourceFileName, sourceFileName + "_d");

                    count++;
                }
                Log.Debug($"Attachments copied({table.TableName}): " + count);

            }
        }

        private void ShowMessageIfRequired(string folder = "Attachments")
        {
            //if ( _userNotifiedRestart ) return;
            string msg = "Machine is not properly connected to the network.";
            msg += $"\n{folder} can not be Syncronized.";
            msg += "\nPlease restart application while connected to the network to syncronize";
            const string caption = "Application Error";

            EventAggregator.GetEvent<NotifyEvent>().Publish(new NotifyEventArgs(msg) { Title = caption });

        }

        private void GetOutputLocations(GeodatabaseFeatureTable table, FileInfo file, out string copyToFolder, out string copyToFile)
        {
            copyToFolder = Path.Combine(_attachmentOutFolder, table.TableName);

            copyToFile = Path.Combine(copyToFolder, file.Name);
        }

        #endregion

        #region SyncBreadCrumbs

        private void SyncBreadCrumbs()
        {
            //Check if can connect to Network folder
            if (ConnectionCheck.IsConnected && !ConnectionCheck.IsDomainConnected)
            {
                ShowMessageIfRequired("Gps Data");
                return;
            }

            string gpsFolder = Path.Combine(Settings.MobileFolder, "GpsOut");
            if (!Directory.Exists(gpsFolder)) return;

            int count = 0;
            foreach (string sourceFileName in Directory.EnumerateFiles(gpsFolder).Where(f => f.EndsWith(".txt")))
            {
                FileInfo file = new FileInfo(sourceFileName);
                if (IsFileinUse(file)) continue;

                string copyToFolder = Path.Combine(Settings.AttachmentSyncFolder, "Gps");
                string copyToFile = Path.Combine(copyToFolder, file.Name);

                try
                {
                    if (File.Exists(copyToFile)) continue;
                    if (!Directory.Exists(copyToFolder))
                    {
                        Directory.CreateDirectory(copyToFolder);
                    }

                }
                catch (Exception e)
                {
                    //if cannot connect to server throws exception
                    Log.Error(e.Message, e);
                }
                try
                {
                    File.Copy(sourceFileName, copyToFile);
                    File.Move(sourceFileName, sourceFileName + "_d");
                }
                catch (Exception e)
                {
                    Log.Error("Copy/Move File", e);
                }

                count++;
            }
            Log.Debug("Breadcrumb files copied: " + count);
        }

        private bool IsFileinUse(FileInfo file)
        {
            FileStream stream = null;

            try
            {
                stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                //the file is unavailable because it is:
                //still being written to
                //or being processed by another thread
                //or does not exist (has already been processed)
                return true;
            }
            finally
            {
                stream?.Close();
            }
            return false;
        }

        #endregion

        private IEnumerable<string> BidirectionalGdbs
        {
            get
            {
                string mobileRootFolder = ConfigurationManager.AppSettings["mobileFolder"];
                string gdbFolder = Path.Combine(mobileRootFolder, "Operational", "Bidirectional");

                return Directory.EnumerateFiles(gdbFolder, ".geodatabase");
            }
        }
    }
}