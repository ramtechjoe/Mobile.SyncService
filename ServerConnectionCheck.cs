using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ram.Mobile.Framework.Shared;

namespace Mobile.SyncService
{
    public class ServerConnectionCheck : IConnectionCheck
    {
        private bool _isStarted;

        public ServerConnectionCheck()
        {
            IsConnected = false;
            CancellationToken = CancellationTokenSource.Token;
        }

        public CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();

        private CancellationToken CancellationToken { get; }


        public Task StartAsync()
        {
            if ( _isStarted ) return Task.FromResult(true);

            try
            {
                TimeSpan delay = TimeSpan.FromSeconds(30);

                Task.Run(() => PeriodicConnectionCheck(CheckIsConnectedAsync, delay), CancellationTokenSource.Token);

                _isStarted = true;

                return Task.FromResult(true);
            }
            catch (Exception e)
            {
                throw new Exception("", e);
            }
        }

        public Task StopAsync()
        {
            if ( CancellationToken.CanBeCanceled )
            {
                CancellationTokenSource.Cancel();
            }

            _isStarted = false;

            return Task.FromResult(true);
        }

        private async Task PeriodicConnectionCheck(Func<Task<bool>> function, TimeSpan delay)
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, CancellationToken);

                if (CancellationToken.IsCancellationRequested) return;

                IsConnected = await function();
            }
        }


        public async Task<bool> CheckIsConnectedAsync()
        {
            try
            {
                var tokenUri = new Uri($"{Settings.ServerUrl}/rest/info?f=json");

                HttpClient client = new HttpClient {Timeout = TimeSpan.FromSeconds(7)};
                var result = await client.GetStringAsync(tokenUri);

                var jsonObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

                return jsonObject.ContainsKey("currentVersion");
            }
            catch (TaskCanceledException)
            {
                //timeout - server down
                return false;
            }
            catch (Exception)
            {
                //Can connect but server not fully up and running
                return false;
            }

        }

        public bool IsConnected { get; private set; }

        public bool IsDomainConnected => Directory.Exists(Settings.AttachmentSyncFolder);

    }
}