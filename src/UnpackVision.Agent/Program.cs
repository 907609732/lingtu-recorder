namespace UnpackVision.Agent;

internal static class Program
{
    public static Task<int> Main(string[] args) => AgentCli.RunAsync(args);
}
