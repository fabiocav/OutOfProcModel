namespace OutOfProcModel.WorkerController;

internal record ApplicationContext(string ApplicationId, string ApplicationVersion)
{
    public override string ToString()
    {
        return $"{ApplicationId} ({ApplicationVersion})";
    }
}
