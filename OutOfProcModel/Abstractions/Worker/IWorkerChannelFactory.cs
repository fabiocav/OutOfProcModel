namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerChannelFactory
{
    IWorkerChannel CreateWorkerChannel(string applicationId);
}

public interface IWorkerChannelWriterProvider
{
    /// <summary>
    /// Provides an <see cref="IWorkerChannelWriter"/> for the specified application ID.
    /// Caller does not dispose of this writer, as it is managed by the provider and may be shared.
    /// </summary>
    /// <param name="applicationId"></param>
    /// <returns></returns>
    IWorkerChannelWriter GetWriter(string applicationId);
}
