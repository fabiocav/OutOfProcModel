using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.Grpc.Abstractions;
using OutOfProcModel.Mock;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class FunctionsHostGrpcService(IJobHostManager jobHostManager) : IFunctionsHostGrpcService
{
    private readonly IJobHostManager _jobHostManager = jobHostManager ?? throw new ArgumentNullException(nameof(jobHostManager));

    // TODO -- should this be here?
    private readonly IList<GrpcWorkerStream> _activeStreams = [];

    // TODO: rename this... shouldn't be called startstream
    public async IAsyncEnumerable<GrpcToWorker> StartStreamAsync(IAsyncEnumerable<GrpcFromWorker> requests)
    {
        var stream = new GrpcWorkerStream(_jobHostManager);
        _activeStreams.Add(stream);

        await foreach (var msg in stream.StartAsync(requests))
        {
            yield return msg;
        }

        await stream.StopAsync();
    }

    public async Task SpecializeWorkerAsync(string applicationId, string applicationVersion, RuntimeEnvironment environment)
    {
        // If not specialized, we need to remove them ourselves.
        List<GrpcWorkerStream> _streamsToRemove = [];

        if (await _jobHostManager.TryGetJobHostAsync(applicationId, out var jobHost))
        {
            return; // already specialized, nothing to do
        }

        foreach (var stream in _activeStreams)
        {
            if (!await stream.TrySpecializeAsync(applicationId, applicationVersion, environment))
            {
                _streamsToRemove.Add(stream);
            }
        }

        // The relevant worker(s) are specializing. Now shut down the JobHost. Should be only one, but let's find all.
        foreach (var stream in _streamsToRemove)
        {
            await stream.StopAsync();
        }
    }
}
