namespace OutOfProcModel;

internal static class LogHelper
{
    public static void Log(string component, bool indent, string message)
    {
        string msg = indent ? "  " : string.Empty;
        msg += $"{component} -> {message}";

        Console.WriteLine(msg);
    }
}
