using System;
using System.Configuration;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.ServiceProcess;
using log4net;
using Mobile.SyncService.Wcf;

namespace Mobile.SyncService
{
    partial class SyncService : ServiceBase
    {
        private static ServiceHost _host;
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);


        public SyncService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                LaunchWcfSyncService();
            }
            catch (Exception e)
            {
                Log.Error(e.Message, e);
            }
        }

        protected override void OnStop()
        {
            _host.Close();
        }


        private void LaunchWcfSyncService()
        {
            try
            {
                string serviceUrl = ConfigurationManager.AppSettings["syncWcfUrl"];

                if (string.IsNullOrEmpty(serviceUrl)) return;

                ISyncWcfService service = new SyncWcfService();
                Uri baseAddress = new Uri(serviceUrl);

                _host = new ServiceHost(service, baseAddress);
            }
            catch (Exception)
            {
                Log.Error("failed launching Sync Wcf Service, is other application open");
            }

            try
            {
                _host.AddServiceEndpoint(typeof(ISyncWcfService), new BasicHttpBinding(), "SyncWcfService");

                ServiceMetadataBehavior behavior = new ServiceMetadataBehavior { HttpGetEnabled = true };
                _host.Description.Behaviors.Add(behavior);

                //Start the service.
                _host.Open();
            }
            catch (CommunicationException)
            {
                Log.Warn("failed Sync Service");
                Log.Warn("Run cmd:  netsh http add urlacl url=http://+:8000/Sync user=Everyone");
                _host.Abort();
            }
        }
    }
}
