using System.Runtime.Serialization;

namespace OutOfProcModel.Abstractions.ControlPlane;

[DataContract]
public class ApplicationContext : IEquatable<ApplicationContext>
{
    public ApplicationContext()
    {
        ApplicationId = string.Empty;
        ApplicationVersion = string.Empty;
    }

    public ApplicationContext(string applicationId, string applicationVersion)
    {
        ApplicationId = applicationId;
        ApplicationVersion = applicationVersion;
    }

    [DataMember(Order = 1)]
    public string ApplicationId { get; set; }

    [DataMember(Order = 2)]
    public string ApplicationVersion { get; set; }

    public override string ToString()
    {
        return $"{ApplicationId} v{ApplicationVersion}";
    }

    public bool Equals(ApplicationContext? other)
    {
        if (other is null)
        {
            return false;
        }

        return ApplicationId == other.ApplicationId && ApplicationVersion == other.ApplicationVersion;
    }

    public override bool Equals(object? obj)
    {
        return obj is ApplicationContext other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ApplicationId, ApplicationVersion);
    }
}