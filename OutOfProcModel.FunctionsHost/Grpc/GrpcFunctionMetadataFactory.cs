using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Grpc.Abstractions;

namespace OutOfProcModel.FunctionsHost.Grpc;

public class GrpcFunctionMetadataFactory : IFunctionMetadataFactory, IMessageHandler
{
    private readonly string _applicationId;
    private readonly IWorkerChannelWriter _channelWriter;

    private readonly TaskCompletionSource<IEnumerable<string>> _metadataTaskCompletionSource = new();
    private readonly Lazy<Task<IEnumerable<string>>> _metadataLazy;

    public GrpcFunctionMetadataFactory(string applicationId, IWorkerChannelWriter channelWriter)
    {
        _applicationId = applicationId;
        _channelWriter = channelWriter;
        _metadataLazy = new(LoadMetadataAsync);
    }

    public Task<IEnumerable<string>> GetFunctionMetadataAsync()
    {
        return _metadataLazy.Value;
    }

    private Task<IEnumerable<string>> LoadMetadataAsync()
    {
        _channelWriter.TryWrite(new MessageToWorker(_applicationId, FunctionsGrpcMessage.MetadataRequest, new Dictionary<string, string>()));
        return _metadataTaskCompletionSource.Task;
    }

    public ValueTask<bool> HandleMessage(MessageFromWorker message)
    {
        if (message.MessageType == FunctionsGrpcMessage.MetadataResponse)
        {
            _metadataTaskCompletionSource.TrySetResult(message.Properties.Keys); // mocking this
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }
}
