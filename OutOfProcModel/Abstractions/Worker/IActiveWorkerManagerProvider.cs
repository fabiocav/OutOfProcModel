namespace OutOfProcModel.Abstractions.Worker;

public interface IActiveWorkerManagerProvider
{
    Task<IWorkerManager> GetActiveManagerAsync(string applicationId);
}