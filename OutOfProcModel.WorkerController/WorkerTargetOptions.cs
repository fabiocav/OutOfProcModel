namespace OutOfProcModel.Abstractions.ControlPlane
{
    internal class WorkerTargetOptions
    {
        public Dictionary<string, int> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}