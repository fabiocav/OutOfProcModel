using OutOfProcModel.Abstractions.Mock;

namespace OutOfProcModel.Abstractions.Worker;

public record WorkerCreationContext(
    WorkerDefinition Definition,
    IDictionary<string, object> Properties);
