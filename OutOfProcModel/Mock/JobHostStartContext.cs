namespace OutOfProcModel.Mock
{
    public class JobHostStartContext(string applicationId, string applicationVersion)
    {
        public string ApplicationId { get; set; } = applicationId;

        public string ApplicationVersion { get; set; } = applicationVersion;
    }
}