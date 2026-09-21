namespace KeelMatrix.FeedFence;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] is "--help" or "-h"))
        {
            Console.WriteLine("feedfence check [path]");
            Console.WriteLine("Phase 0 foundation: the analysis command is not implemented yet.");
            return 0;
        }

        Console.Error.WriteLine("FeedFence analysis is not implemented in this foundation milestone.");
        return 2;
    }
}
