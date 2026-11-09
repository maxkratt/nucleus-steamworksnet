using Nucleus.Serializers;

// This sample declares its ban notice, so it needs a bundle of its own, clear of the transport's (480), its tests' (481), the shared sample's (482) and the test harness's (483).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 484))]
