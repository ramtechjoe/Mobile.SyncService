using System.ServiceProcess;
using log4net;

namespace Mobile.SyncService
{
    
    static class Program
    {
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        static void Main()
        {
            var servicesToRun = new ServiceBase[]
            {
                new SyncService()
            };

            ServiceBase.Run(servicesToRun);
        }
    }
}
