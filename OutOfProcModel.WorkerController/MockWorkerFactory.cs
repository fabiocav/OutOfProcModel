using OutOfProcModel.Abstractions.ControlPlane;

namespace OutOfProcModel.WorkerController;

public class MockWorkerFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public MockWorker CreateWorker(WorkerState workerState)
    {
        return new MockWorker(workerState, _configuration, _loggerFactory.CreateLogger<MockWorker>());
    }
}
