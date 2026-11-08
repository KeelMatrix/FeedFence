namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
    private void Assert(string description, bool condition)
    {
        if (!condition)
        {
            _failures.Add(description);
        }
    }

    private void AssertSet(string description, IEnumerable<string> actual, IEnumerable<string> expected)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        Assert(description, actualSet.SetEquals(expectedSet));
        if (!actualSet.SetEquals(expectedSet))
        {
            _failures.Add($"{description}: expected [{string.Join(", ", expectedSet)}], actual [{string.Join(", ", actualSet)}]");
        }
    }
}
