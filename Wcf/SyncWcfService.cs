using log4net;
using System.ServiceModel;

namespace Mobile.SyncService.Wcf
{
    
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class SyncWcfService : ISyncWcfService
    {
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public void ExecuteManualSync()
        {
            
        }
    }
}