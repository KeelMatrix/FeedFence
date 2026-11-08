namespace KeelMatrix.FeedFence.CliTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return CliTestRunner.Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
    }
}
