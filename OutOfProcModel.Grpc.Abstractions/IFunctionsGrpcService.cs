using ProtoBuf.Grpc.Configuration;

namespace OutOfProcModel.Grpc.Abstractions;

[Service]
public interface IFunctionsHostGrpcService
{
    IAsyncEnumerable<GrpcToWorker> StartStreamAsync(IAsyncEnumerable<GrpcFromWorker> requests);
}
