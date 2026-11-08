namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void AssertContains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"expected output to contain '{expected}'.");
        }
    }

    private static void AssertNotContains(string value, string unexpected)
    {
        if (!string.IsNullOrEmpty(unexpected) && value.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"expected output not to contain '{unexpected}'.");
        }
    }
}
