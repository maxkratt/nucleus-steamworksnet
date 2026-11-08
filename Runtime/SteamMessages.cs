// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// Reads Steam's native messages into engine packets, and holds the layout and limits both sockets of <see cref="SteamSockets"/> share.
    /// </summary>
    internal static class SteamMessages
    {
        /// <summary>
        /// Where <see cref="SteamNetworkingMessage_t.m_pData"/> sits in a native message.
        /// </summary>
        /// <remarks>
        /// Messages are read and written through these offsets rather than marshalled whole, because marshalling the struct boxes it and
        /// copies its identity array on every message.
        /// </remarks>
        internal static readonly int DataOffset = (int)Marshal.OffsetOf<SteamNetworkingMessage_t>(nameof(SteamNetworkingMessage_t.m_pData));
        /// <summary>
        /// Where <see cref="SteamNetworkingMessage_t.m_cbSize"/> sits in a native message.
        /// </summary>
        internal static readonly int SizeOffset = (int)Marshal.OffsetOf<SteamNetworkingMessage_t>(nameof(SteamNetworkingMessage_t.m_cbSize));
        /// <summary>
        /// Where <see cref="SteamNetworkingMessage_t.m_conn"/> sits in a native message.
        /// </summary>
        internal static readonly int ConnectionOffset = (int)Marshal.OffsetOf<SteamNetworkingMessage_t>(nameof(SteamNetworkingMessage_t.m_conn));
        /// <summary>
        /// Where <see cref="SteamNetworkingMessage_t.m_nFlags"/> sits in a native message.
        /// </summary>
        internal static readonly int FlagsOffset = (int)Marshal.OffsetOf<SteamNetworkingMessage_t>(nameof(SteamNetworkingMessage_t.m_nFlags));
        /// <summary>
        /// Where <see cref="SteamNetworkingMessage_t.m_idxLane"/> sits in a native message.
        /// </summary>
        internal static readonly int LaneOffset = (int)Marshal.OffsetOf<SteamNetworkingMessage_t>(nameof(SteamNetworkingMessage_t.m_idxLane));
        /// <summary>
        /// The most messages taken from Steam in one receive call; more wait for the next call in the same pass.
        /// </summary>
        internal const int MaximumMessagesPerReceive = 256;
        /// <summary>
        /// The most bytes Steam carries in one message; a reliable packet this long or longer is sent in pieces of this length.
        /// </summary>
        internal const int MaximumMessageBytes = Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend;
        /// <summary>
        /// The reason code a connection this transport closes is closed with: Steam's first application-defined reason.
        /// </summary>
        internal const int ApplicationCloseReason = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Min;
        /// <summary>
        /// The lane reliable packets ride.
        /// </summary>
        internal const ushort ReliableLane = 0;
        /// <summary>
        /// The lane unreliable packets ride.
        /// </summary>
        internal const ushort UnreliableLane = 1;

        /// <summary>
        /// Copies a received message into an engine packet and releases the message back to Steam, joining the pieces of a reliable packet
        /// <see cref="SteamSendBatch.Stage"/> split.
        /// </summary>
        /// <param name="messagePointer">The message Steam handed over.</param>
        /// <param name="sender">The Connection the packet came from.</param>
        /// <param name="steamConnection">The Steam connection the message arrived on.</param>
        /// <param name="splitPackets">The socket's split packets, which a piece is added to.</param>
        /// <param name="maximumPacketBytes">The largest packet accepted, or <see cref="SteamSockets.UnsetMaximumClientPacketBytes"/> for no limit.</param>
        /// <param name="incomingPacket">The packet, owning a payload array rented from <see cref="TrackedArrayPool{T0}"/>.</param>
        /// <param name="isOverLimit">
        /// <see langword="true"/> when the message would take its packet past <paramref name="maximumPacketBytes"/>; the message and the
        /// pieces joined so far have then been released, and the sender should be dropped.
        /// </param>
        /// <returns><see langword="true"/> when the message completed a packet; <see langword="false"/> when it was a piece with more to follow, or was over the limit.</returns>
        internal static bool TryCreatePacket(IntPtr messagePointer, Connection sender, HSteamNetConnection steamConnection, Dictionary<HSteamNetConnection, ArraySegment<byte>> splitPackets, uint maximumPacketBytes, out IncomingPacket incomingPacket, out bool isOverLimit)
        {
            int size = Marshal.ReadInt32(messagePointer, SizeOffset);
            IntPtr dataPointer = Marshal.ReadIntPtr(messagePointer, DataOffset);
            isOverLimit = false;

            // Steam reports the reliable bit on every received message, which is what makes a channel header unnecessary.
            bool isReliable = (Marshal.ReadInt32(messagePointer, FlagsOffset) & Constants.k_nSteamNetworkingSend_Reliable) != 0;

            // A whole packet: anything unreliable, or a reliable message shorter than a piece with no split packet in progress.
            if (!isReliable || (size < MaximumMessageBytes && (splitPackets.Count == 0 || !splitPackets.ContainsKey(steamConnection))))
            {
                if (maximumPacketBytes is not SteamSockets.UnsetMaximumClientPacketBytes && (uint)size > maximumPacketBytes)
                    return RefuseOverLimit(messagePointer, steamConnection, splitPackets, out incomingPacket, out isOverLimit);

                byte[] payloadBytes = TrackedArrayPool<byte>.Rent(size);
                Marshal.Copy(dataPointer, payloadBytes, 0, size);

                SteamNetworkingMessage_t.Release(messagePointer);

                incomingPacket = new(new(payloadBytes, 0, size), isReliable ? Channel.Reliable : Channel.Unreliable, sender);

                return true;
            }

            splitPackets.TryGetValue(steamConnection, out ArraySegment<byte> splitPacket);

            // Checked before anything is copied, so a packet past the limit never grows the buffer.
            if (maximumPacketBytes is not SteamSockets.UnsetMaximumClientPacketBytes && (ulong)splitPacket.Count + (ulong)size > maximumPacketBytes)
                return RefuseOverLimit(messagePointer, steamConnection, splitPackets, out incomingPacket, out isOverLimit);

            byte[] splitBytes = splitPacket.Array ?? TrackedArrayPool<byte>.Rent(MaximumMessageBytes * 2);
            int splitLength = splitPacket.Count;

            if (splitBytes.Length < splitLength + size)
                TrackedArrayPool<byte>.ResizeWithoutClearing(ref splitBytes, Math.Max(splitBytes.Length * 2, splitLength + size));

            // The empty last piece of a packet that filled its pieces exactly has no buffer to copy from.
            if (size > 0)
                Marshal.Copy(dataPointer, splitBytes, splitLength, size);

            splitLength += size;

            SteamNetworkingMessage_t.Release(messagePointer);

            // A full-length piece always has another after it.
            if (size == MaximumMessageBytes)
            {
                splitPackets[steamConnection] = new(splitBytes, 0, splitLength);
                incomingPacket = default;

                return false;
            }

            splitPackets.Remove(steamConnection);
            incomingPacket = new(new(splitBytes, 0, splitLength), Channel.Reliable, sender);

            return true;
        }

        /// <summary>
        /// Returns the pieces received so far of a closed connection's split reliable packet, which will never be completed.
        /// </summary>
        /// <param name="splitPackets">The socket's split packets.</param>
        /// <param name="steamConnection">The closed connection.</param>
        internal static void DiscardSplitPacket(Dictionary<HSteamNetConnection, ArraySegment<byte>> splitPackets, HSteamNetConnection steamConnection)
        {
            if (splitPackets.Remove(steamConnection, out ArraySegment<byte> splitPacket))
                TrackedArrayPool<byte>.Return(splitPacket.Array, clearArray: false);
        }

        /// <summary>
        /// Returns the pieces of every split reliable packet a socket was still joining.
        /// </summary>
        /// <param name="splitPackets">The socket's split packets.</param>
        internal static void DiscardSplitPackets(Dictionary<HSteamNetConnection, ArraySegment<byte>> splitPackets)
        {
            foreach (KeyValuePair<HSteamNetConnection, ArraySegment<byte>> splitPacket in splitPackets)
                TrackedArrayPool<byte>.Return(splitPacket.Value.Array, clearArray: false);

            splitPackets.Clear();
        }

        /// <summary>
        /// Releases a message that would take its packet past the receiver's limit, along with the pieces already joined.
        /// </summary>
        /// <param name="messagePointer">The message Steam handed over.</param>
        /// <param name="steamConnection">The Steam connection the message arrived on.</param>
        /// <param name="splitPackets">The socket's split packets.</param>
        /// <param name="incomingPacket">No packet, always.</param>
        /// <param name="isOverLimit">Always <see langword="true"/>.</param>
        /// <returns><see langword="false"/>, since no packet was completed.</returns>
        private static bool RefuseOverLimit(IntPtr messagePointer, HSteamNetConnection steamConnection, Dictionary<HSteamNetConnection, ArraySegment<byte>> splitPackets, out IncomingPacket incomingPacket, out bool isOverLimit)
        {
            SteamNetworkingMessage_t.Release(messagePointer);
            DiscardSplitPacket(splitPackets, steamConnection);

            incomingPacket = default;
            isOverLimit = true;

            return false;
        }
    }
}
#endif
