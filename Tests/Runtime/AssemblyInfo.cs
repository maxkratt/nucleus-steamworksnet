using Nucleus.Serializers;

// The test assembly declares its own kick notice, so it takes a bundle id of its own, one below the transport's, for the reason the
// transport's AssemblyInfo gives.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 481))]
