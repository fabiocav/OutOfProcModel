namespace OutOfProcModel.WorkerController;

internal class MockWorkerFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public MockWorker CreateWorker(ControllerWorkerDefinition workerDef)
    {
        return new MockWorker(workerDef, _configuration, _loggerFactory.CreateLogger<MockWorker>());
    }
}
