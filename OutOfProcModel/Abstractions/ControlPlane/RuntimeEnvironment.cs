using System.Runtime.Serialization;

namespace OutOfProcModel.Abstractions.ControlPlane;

[DataContract]
public class RuntimeEnvironment : IEquatable<RuntimeEnvironment>
{
    public RuntimeEnvironment()
    {
        Runtime = string.Empty;
        Version = string.Empty;
        Architecture = string.Empty;
        IsPlaceholder = false;
    }

    public RuntimeEnvironment(string runtime, string version, string architecture, bool isPlaceholder = false)
    {
        Runtime = runtime;
        Version = version;
        Architecture = architecture;
        IsPlaceholder = isPlaceholder;
    }

    [DataMember(Order = 1)]
    public string Runtime { get; set; }

    [DataMember(Order = 2)]
    public string Version { get; set; }

    [DataMember(Order = 3)]
    public string Architecture { get; set; }

    [DataMember(Order = 4)]
    public bool IsPlaceholder { get; set; }

    public override string ToString()
    {
        return $"r={Runtime};rv={Version};a={Architecture};ph={IsPlaceholder}";
    }

    public bool Equals(RuntimeEnvironment? other)
    {
        if (other is null)
            return false;

        return Runtime == other.Runtime &&
               Version == other.Version &&
               Architecture == other.Architecture &&
               IsPlaceholder == other.IsPlaceholder;
    }

    public override bool Equals(object? obj) => Equals(obj as RuntimeEnvironment);

    public override int GetHashCode() =>
        HashCode.Combine(Runtime, Version, Architecture, IsPlaceholder);
}
