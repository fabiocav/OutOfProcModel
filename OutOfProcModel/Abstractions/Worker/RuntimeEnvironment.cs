namespace OutOfProcModel.Abstractions.ControlPlane;

public record RuntimeEnvironment(string Runtime, string Version, string Architecture, bool IsPlaceholder);