using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.Tasks.Offline;
using log4net;

//using Mobile.Resources;
//using Newtonsoft.Json;
//using Newtonsoft.Json.Linq;
//using Prism.Commands;
//using Ram.Mobile.Framework;
//using Ram.Mobile.Framework.Events;


namespace Mobile.SyncService
{
    public class SyncDataViewModel
    {
        #region Private Fields

        private bool _isConnected;
        private bool _isSynching;
        private readonly string _attachmentOutFolder;
        private readonly string _attachmentSourceFolder;
        private bool _canStartSync = default(bool);
        private bool _updateDownloadDate = true;
        private bool _initialDownloadComplete;
        private bool _startupErrorOccured;
        private Exception _startupException;

        #endregion

        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected Credential Credential { get; set; }
        public static CancellationToken CancellationToken { get; set; } = default(CancellationToken);

        public Uri ImageUri { get; set; }
        public ToolbarCommand ToolbarCommand { set; private get; }

        #region Constructor

        public SyncDataViewModel()
        {
            try
            {
                _attachmentSourceFolder = Path.Combine(Settings.MobileFolder, "Attachments");
                _attachmentOutFolder = Settings.AttachmentSyncFolder;

                DateTime lastDownload = DataFileTracker.ReadLastDownloadSyncDate();
                TimeSpan period = TimeSpan.FromMinutes(int.Parse(Settings.SyncFrequency));


                Task.Run(() => ContinousSyncAsync(period));

                if (DateTime.Now.Date > lastDownload.Date)
                {
                    Task.Run(DailyDeltaSyncAsync);
                }
                else
                {
                    _initialDownloadComplete = true;
                }
            }
            catch (Exception e)
            {
                //Cannot log here because Log import not available
                _startupErrorOccured = true;
                _startupException = e;
            }
        }

        #endregion

        #region ManualSyncCommand

        public async void ExecuteManualSync()
        {
            try
            {
                Log.Debug("Manual Sync Started");
                if (_isSynching)
                {
                    Log.Debug("Already in sync process, skipping manual sync");
                }

                _isSynching = true;

                //EventAggregator.GetEvent<NotifyEvent>().Publish(new NotifyEventArgs("Manual Sync Started"));

                using (var gdbMonitor = await GeodatabaseMonitor.CreateAsync(Log, EventAggregator, false))
                {
                    await SyncDataAsync(gdbMonitor.BidirectionalGeodatabases, SyncDirection.Upload);
                    await SyncDataAsync(gdbMonitor.BidirectionalGeodatabases, SyncDirection.Download);
                }

                _isSynching = false;

                await DeltasSyncAsync();
                DataFileTracker.UpdateLastDownloadSyncDate();

                Log.Debug("Manual Sync Completed");

                //EventAggregator.GetEvent<TableChangedEvent>().Publish(TableChangedEventArgs.Empty);
            }
            catch (Exception e)
            {
                Log.Error("Something went wrong....", e);
            }
        }

        #endregion

        #region DownloadSync

        private async Task DailyDeltaSyncAsync()
        {
            while (true)
            {
                //Pause
                await Task.Delay(TimeSpan.FromSeconds(30));
                if (!_canStartSync)
                {
                    Log.Info("Map not loaded, delaying download sync");
                    continue;
                }

                if (!_isConnected)
                {
                    Log.Info("Not connected to server, skipping initial download sync");
                    //don't try again if not connected at startup
                    _initialDownloadComplete = true;
                    return;
                }

                await DeltasSyncAsync();

                _initialDownloadComplete = true;

                //EventAggregator.GetEvent<TableChangedEvent>().Publish(TableChangedEventArgs.Empty);

                break;
            }

            if (_updateDownloadDate)
            {
                //DataFileTracker.UpdateLastDownloadSyncDate();
            }
        }

        private async Task DeltasSyncAsync()
        {
            Log.Debug("Starting download sync");
            Log.Debug("Start delta download");

            await DownloadDeltasAsync();

            Log.Debug("Delta download complete");
            Log.Debug("Start merge deltas");

            foreach (var fileName in Directory.EnumerateFiles(TempFolder()))
            {
                var gdbpath = GetGeodatabasePath(fileName);
                if (gdbpath == null) continue;

                Log.Debug($"Start delta sync {gdbpath} with {fileName}");

                IReadOnlyList<SyncLayerResult> syncLayerResults = null;
                try
                {
                    syncLayerResults = await GeodatabaseSyncTask.ImportGeodatabaseDeltaAsync(gdbpath, fileName);
                }
                catch (Exception e)
                {
                    Log.Error(e.Message, e);
                }

                Log.Debug($"Complete delta sync {gdbpath} with {fileName}");

                LogResults(syncLayerResults);

                try
                {
                    File.Delete(fileName);
                }
                catch (Exception e)
                {
                    Log.Error(e.Message, e);
                }
            }

            Log.Debug("End merge deltas");
            Log.Debug("Completed download sync");
        }

        private string GetGeodatabasePath(string fileName)
        {
            string shortName = Path.GetFileNameWithoutExtension(fileName);
            if (shortName == null) return null;

            int index = shortName.LastIndexOf('-');
            string serviceName = shortName.Substring(index + 1);
            string gdbpath = Path.Combine(Constants.DataFolder, Constants.DownloadOnlyFolder, $"{serviceName}.geodatabase");
            return gdbpath;
        }

        private async Task DownloadDeltasAsync()
        {
            try
            {
                IEnumerable<string> filenames = await GetDeltaFileNamesAsync();

                using (var outputStream = await GetZipFileStreamAsync(filenames))
                {
                    await UnzipDeltaFilesAsync(outputStream);
                }
            }
            catch (Exception e)
            {
                _updateDownloadDate = false;
                Log.Error(e.Message, e);
            }
        }

        private async Task<IEnumerable<string>> GetDeltaFileNamesAsync()
        {
            DateTime lastDownload = DataFileTracker.ReadLastDownloadSyncDate();
            string dateQuery = $"{lastDownload.Year}{lastDownload.Month:00}{lastDownload.Day:00}";
            string downloadUrl = $"{Constants.Settings.WebApiUrl}/api/deltas/{dateQuery}?f=json";

            HttpClient client = new HttpClient();
            string json = await client.GetStringAsync(downloadUrl);

            var jObject = (JObject)JsonConvert.DeserializeObject(json);
            var array = (JArray)jObject["files"];

            var filenames = array.Select(s => s.ToString()).ToArray();
            Log.Debug($"Found {filenames.Length} requiring download");

            return filenames;
        }

        private async Task<Stream> GetZipFileStreamAsync(IEnumerable<string> filenames)
        {
            var client = new HttpClient();
            HttpContent content = new StringContent(JsonConvert.SerializeObject(filenames), Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponseMessage =
                await client.PostAsync($"{Constants.Settings.WebApiUrl}/api/deltas", content);

            var outputStream = await httpResponseMessage.Content.ReadAsStreamAsync();
            Log.Debug($"Delta file zip downloaded.  Size: {outputStream.Length / 1000:F} Kb");

            return outputStream;
        }

        private async Task UnzipDeltaFilesAsync(Stream outputStream)
        {
            ZipArchive archive = new ZipArchive(outputStream);

            foreach (var entry in archive.Entries)
            {
                var zipStream = entry.Open();
                using (var fileStream = new FileStream(Path.Combine(TempFolder(), entry.Name), FileMode.Create))
                {
                    await zipStream.CopyToAsync(fileStream);
                }

                zipStream.Close();
            }

            Log.Debug("Delta files unzipped");
        }

        private string TempFolder()
        {
            string rootFolder = Settings.MobileFolder;
            string tempDbFolder = Path.Combine(rootFolder, "Temp");

            if (!Directory.Exists(tempDbFolder))
            {
                Directory.CreateDirectory(tempDbFolder);
            }

            return tempDbFolder;
        }

        #endregion

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

                if (!_initialDownloadComplete)
                {
                    Log.Info("Bidirectional sync waiting for completion of download");
                    continue;
                }

                using (var gdbMonitor = await GeodatabaseMonitor.CreateAsync(Log, EventAggregator))
                {
                    if (_isSynching)
                    {
                        Log.Debug("Sync in progress - skipping background sync");
                        continue;
                    }
                    Log.Debug("Background sync beginning");
                    _isSynching = true;

                    await SyncDataAsync(gdbMonitor.BidirectionalGeodatabases, SyncDirection.Bidirectional);

                    _isSynching = false;
                    Log.Debug("Background sync completed");

                    FullFileSync(gdbMonitor.BidirectionalGeodatabases);

                    EventAggregator.GetEvent<TableChangedEvent>().Publish(TableChangedEventArgs.Empty);
                }
            }

        }

        private async Task SyncDataAsync(IEnumerable<Geodatabase> syncGeodatabases, SyncDirection syncDirection)
        {
            foreach (var geodatabase in syncGeodatabases)
            {
                IReadOnlyList<SyncLayerResult> results = null;
                try
                {
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
                    Log.Error($"{geodatabase.Path} did not sync");
                    if (results != null) LogResults(results);
                }
            }

            if (syncDirection == SyncDirection.Bidirectional || syncDirection == SyncDirection.Download)
            {
                EventAggregator.GetEvent<SyncCompleteEvent>().Publish(SyncCompleteEventArgs.Empty);
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

            string gpsFolder = Path.Combine(Constants.Settings.MobileFolder, "GpsOut");
            if (!Directory.Exists(gpsFolder)) return;

            int count = 0;
            foreach (string sourceFileName in Directory.EnumerateFiles(gpsFolder).Where(f => f.EndsWith(".txt")))
            {
                FileInfo file = new FileInfo(sourceFileName);
                if (IsFileinUse(file)) continue;

                string copyToFolder = Path.Combine(Constants.Settings.AttachmentSyncFolder, "Gps");
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

        private async void OnSyncData(SyncDataEventArgs obj)
        {
            if (obj.SyncGpsOnly) //this is called when complete survey
            {
                SyncBreadCrumbs();
                return;
            }

            ToolbarCommand.ImageUri = ProcessingImage;
            ToolbarCommand.ToolTip = "Sync in progress";

            using (var monitor = await GeodatabaseMonitor.CreateAsync(Log, EventAggregator))
            {
                var gdbList = obj.GdbPaths.Select(gdbPath => monitor[gdbPath]).ToList();

                await SyncDataAsync(gdbList, obj.SyncDirection);

                if (obj.RequireFileSync)
                {
                    FullFileSync(gdbList);
                }
            }

            if (obj.CloseOnCompletion)
            {
                EventAggregator.GetEvent<CloseApplicationEvent>().Publish(CloseApplicationEventArgs.Empty);
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

        private void OnConnectionStatusChanged(ConnectionStatusChangedEventArgs obj)
        {
            if (obj.ConnectionChangeType == ConnectionChangeType.Network)
            {
                _isConnected = obj.NetworkConnectionStatus == NetworkConnectionStatus.Connected;
            }
        }

        protected override void OnMapLoaded()
        {
            base.OnMapLoaded();
            _canStartSync = true;
        }

        public override void OnImportsSatisfied()
        {
            base.OnImportsSatisfied();

            EventAggregator.GetEvent<ConnectionStatusChangedEvent>().Subscribe(OnConnectionStatusChanged);
            EventAggregator.GetEvent<SyncDataEvent>().Subscribe(OnSyncData);

            if (!_startupErrorOccured) return;

            Log.Error("Error occured initializing Sync");
            Log.Error(_startupException?.Message, _startupException);
        }


    }
}