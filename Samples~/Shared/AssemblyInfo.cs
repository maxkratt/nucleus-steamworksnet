using Nucleus.Serializers;

// The shared sample assembly declares the name tag's component, so it needs a bundle of its own, clear of the transport's (480) and
// its test assembly's (481).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 482))]
