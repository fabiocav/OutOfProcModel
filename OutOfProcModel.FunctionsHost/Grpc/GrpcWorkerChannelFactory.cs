using OutOfProcModel.Abstractions.Worker;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerChannelFactory(MessageHandlerPipeline handlerPipeline, ChannelWriter<GrpcToWorker> writeToWorker, ILoggerFactory loggerFactory)
    : IWorkerChannelFactory, IDisposable
{
    private readonly MessageHandlerPipeline _handlerPipeline = handlerPipeline;
    private readonly ChannelWriter<GrpcToWorker> _writeToWorker = writeToWorker;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    private readonly IList<IDisposable> _disposables = [];

    public IWorkerChannel CreateWorkerChannel()
    {
        var channel = new GrpcWorkerChannel(_handlerPipeline, _writeToWorker, _loggerFactory.CreateLogger<GrpcWorkerChannel>());
        if (channel is IDisposable disposable)
        {
            _disposables.Add(disposable);
        }
        return channel;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}
