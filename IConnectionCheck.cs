using System.Threading;
using System.Threading.Tasks;

namespace Ram.Mobile.Framework.Shared
{
    public interface IConnectionCheck
    {
        CancellationTokenSource CancellationTokenSource { get; }

        Task StartAsync();

        Task StopAsync();

        Task<bool> CheckIsConnectedAsync();

        bool IsConnected { get; }

        bool IsDomainConnected { get; }
    }
}