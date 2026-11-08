namespace KeelMatrix.FeedFence.Phase0Probe;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Any(argument => argument != "--machine-in-user-layer"))
            {
                throw new ArgumentException("Unknown probe argument.");
            }
            return new ProbeRunner(args.Contains("--machine-in-user-layer", StringComparer.Ordinal)).Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"STOP: probe infrastructure failure: {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }
}
