using System.Runtime.CompilerServices;
using Nucleus.Serializers;

// The inspector reads whether Steam and a game server are running through the transport's own checks.
[assembly: InternalsVisibleTo("Nucleus.Integrations.SteamworksNet.Editor")]
// The play mode test asks whether Steam is already up before initializing it.
[assembly: InternalsVisibleTo("Nucleus.Integrations.SteamworksNet.Tests")]

// A game's main assembly defaults to bundle 0, so this one takes an id near the top of the space, clear of the Unity integration's
// own ids just below the framework's, where the wire format keeps it as cheap as a small one. The offset is 480, Steam's test App ID.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 480))]
