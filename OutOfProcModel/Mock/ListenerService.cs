using Microsoft.Extensions.Hosting;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Mock
{
    internal class ListenerService(IFunctionMetadataFactory metadataFactory) : IHostedService
    {
        private readonly IFunctionMetadataFactory _metadataFactory = metadataFactory;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var metadata = await _metadataFactory.GetFunctionMetadataAsync();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}