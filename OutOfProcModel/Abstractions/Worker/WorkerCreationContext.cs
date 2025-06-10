namespace OutOfProcModel.Abstractions.Worker;

public record WorkerCreationContext(string WorkerId, string ApplicationId, string ApplicationVersion, IEnumerable<string> Capabilities);
