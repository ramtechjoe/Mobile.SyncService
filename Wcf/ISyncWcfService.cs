using System.ServiceModel;

namespace Mobile.SyncService.Wcf
{

    [ServiceContract(Namespace = "http://ramtech-corp.com/professional-services/")]
    public interface ISyncWcfService
    {
        [OperationContract]
        void ExecuteManualSync();
    }
}