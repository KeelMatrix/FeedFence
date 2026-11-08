using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed class ExplicitMachineWideSettings : IMachineWideSettings
{
    public ExplicitMachineWideSettings(ISettings settings)
    {
        Settings = settings;
    }

    public ISettings Settings { get; }
}
