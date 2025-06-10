namespace OutOfProcModel.Abstractions.Worker;

public record MessageFromWorker(string ApplicationId, string MessageType, IDictionary<string, string> Properties);

