// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nucleus.Managers.Transports;
using Nucleus.Packets;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.SteamworksNet
{
    /// <summary>
    /// One socket's Steam-owned messages built this frame, and the interface they are sent through.
    /// </summary>
    /// <remarks>
    /// Outbound packets are copied into Steam-owned messages as the engine sends them and handed to Steam in one call at
    /// <see cref="Nucleus.Managers.NetworkLoop.NetworkLoopSteps.LateVariableUpdate"/>, after the frame's sends; that call is the only one
    /// able to pick a lane, and lanes are what keep unreliable state from queueing behind a reliable burst. Steam refuses a message past
    /// 512 KiB, or one that would overfill a connection's send buffer, and a refused reliable message would leave the peers disagreeing;
    /// so a reliable packet past 512 KiB is split into pieces, and reliable messages wait here while Steam's queue is full.
    /// </remarks>
    internal sealed class SteamSendBatch
    {
        /// <summary>
        /// The interface the socket's connections were opened on, which allocates and sends its messages.
        /// </summary>
        /// <remarks>A handle opened on one interface is unknown to the other, which is why each socket has a batch of its own.</remarks>
        public SteamNetworkingInterface NetworkingInterface = SteamNetworkingInterface.Client;
        /// <summary>
        /// The send buffer of the socket's connections, in bytes, which queued reliable messages are measured against.
        /// </summary>
        public int SendBufferBytes = SteamSockets.DefaultSendBufferBytes;
        /// <summary>
        /// The reliable messages the last <see cref="Flush"/> saw Steam refuse, for the socket to close their links; the socket clears it.
        /// </summary>
        public readonly List<RefusedSend> RefusedReliableSends = new();
        /// <summary>
        /// The reliable messages each connection has waiting, in order, released at a flush as far as its send buffer has room.
        /// </summary>
        /// <remarks>An entry lives until its connection closes, so a link that sends reliably allocates its queue once.</remarks>
        private readonly Dictionary<HSteamNetConnection, Queue<StagedSend>> _queuedReliableSends = new();
        /// <summary>
        /// The unreliable messages built this frame, which go at the next flush whether or not Steam has room for them.
        /// </summary>
        private readonly List<StagedSend> _unreliableSends = new();
        /// <summary>
        /// The messages the next call to Steam hands over, reliable first; what each one was is kept because Steam owns and may free the
        /// message itself once it is sent.
        /// </summary>
        private readonly List<StagedSend> _sends = new();
        /// <summary>
        /// The messages handed to Steam in one call, grown to the largest frame seen.
        /// </summary>
        private IntPtr[] _messagePointers = new IntPtr[SteamMessages.MaximumMessagesPerReceive];
        /// <summary>
        /// Steam's result for each message in <see cref="_messagePointers"/>: a message number, or a negated <see cref="EResult"/>.
        /// </summary>
        private long[] _results = new long[SteamMessages.MaximumMessagesPerReceive];
        /// <summary>
        /// The bytes Steam's reliable framing adds to each message in its queue, at most, counted when measuring a message against the room left.
        /// </summary>
        private const int ReliableHeaderAllowanceBytes = 16;

        /// <summary>
        /// Copies a packet into Steam-owned messages addressed to a connection, to go out at the next flush, and hands the packet back to
        /// the engine, being copied.
        /// </summary>
        /// <param name="steamConnection">The connection to send on.</param>
        /// <param name="outgoingPacket">The packet to send.</param>
        /// <remarks>
        /// A reliable packet of <see cref="SteamMessages.MaximumMessageBytes"/> or more goes as pieces of exactly that length, then one
        /// shorter piece, empty when the packet fills its last piece exactly. The pieces ride the reliable lane, which keeps them in order,
        /// and <see cref="SteamMessages.TryCreatePacket"/> joins them by length alone, so no packet carries a header. The engine splits
        /// unreliable packets itself, so one never reaches that length.
        /// </remarks>
        public void Stage(HSteamNetConnection steamConnection, OutgoingPacket outgoingPacket)
        {
            ArraySegment<byte> payload = outgoingPacket.Payload;
            bool isReliable = outgoingPacket.Channel is Channel.Reliable;
            int offset = payload.Offset;
            int remainingBytes = payload.Count;

            while (isReliable && remainingBytes >= SteamMessages.MaximumMessageBytes)
            {
                StagePiece(steamConnection, payload.Array!, offset, SteamMessages.MaximumMessageBytes, isReliable: true);

                offset += SteamMessages.MaximumMessageBytes;
                remainingBytes -= SteamMessages.MaximumMessageBytes;
            }

            StagePiece(steamConnection, payload.Array!, offset, remainingBytes, isReliable);

            TransportManager.PacketOffloaded(outgoingPacket);
        }

        /// <summary>
        /// Moves every reliable message a connection has waiting into the next flush, whether or not Steam has room for them.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <remarks>For a lingering close, which drains Steam's queue anyway; any Steam refuses die with the link.</remarks>
        public void ReleaseAllQueuedReliableSends(HSteamNetConnection steamConnection)
        {
            if (_queuedReliableSends.TryGetValue(steamConnection, out Queue<StagedSend> queuedReliableSends))
                ReleaseQueuedReliableSends(queuedReliableSends, budgetBytes: int.MaxValue);
        }

        /// <summary>
        /// Hands the staged messages to Steam in one call, the reliable ones as far as each link's send buffer has room, and records in
        /// <see cref="RefusedReliableSends"/> every reliable message Steam refused.
        /// </summary>
        /// <param name="steamSockets">The transport, which counts refused unreliable messages.</param>
        /// <remarks>
        /// Steam owns each message from this call on, sent or not. Reliable messages that would overfill a link's buffer stay queued for a
        /// later flush, in order, because Steam refuses them and a lost reliable packet would leave the two peers disagreeing for the rest
        /// of the session. Measuring the room reads Steam's live status, which Steamworks.NET allocates about 80 bytes to marshal, once per
        /// link with reliable messages waiting. A refused unreliable message is counted and dropped. With Steam shut down the staged
        /// messages can be neither sent nor released, so they are dropped with it.
        /// </remarks>
        public void Flush(SteamSockets steamSockets)
        {
            SteamNetworkingInterface steamNetworkingInterface = NetworkingInterface;

            if (steamNetworkingInterface.IsShutDown())
            {
                Clear();

                return;
            }

            // Reliable first, so this frame's unreliable state cannot take the room the reliable messages were measured against.
            foreach (KeyValuePair<HSteamNetConnection, Queue<StagedSend>> queuedReliableSends in _queuedReliableSends)
            {
                if (queuedReliableSends.Value.Count > 0)
                    ReleaseQueuedReliableSends(queuedReliableSends.Value, GetSendBudgetBytes(queuedReliableSends.Key));
            }

            _sends.AddRange(_unreliableSends);
            _unreliableSends.Clear();

            int messageCount = _sends.Count;

            if (messageCount == 0)
                return;

            if (_messagePointers.Length < messageCount)
            {
                _messagePointers = new IntPtr[messageCount];
                _results = new long[messageCount];
            }

            for (int i = 0; i < messageCount; i++)
                _messagePointers[i] = _sends[i].MessagePointer;

            steamNetworkingInterface.SendMessages(messageCount, _messagePointers, _results);

            for (int i = 0; i < messageCount; i++)
            {
                if (_results[i] >= 0)
                    continue;

                StagedSend stagedSend = _sends[i];
                EResult result = (EResult)(-_results[i]);

                // The peer has already closed this link and Steam's report of it is on its way, so the message has no one to reach.
                if (result is EResult.k_EResultNoConnection)
                    continue;

                if (stagedSend.IsReliable)
                    RefusedReliableSends.Add(new(stagedSend.SteamConnection, stagedSend.PayloadBytes, result));
                else
                    steamSockets.RefusedUnreliableSendCount++;
            }

            _sends.Clear();
        }

        /// <summary>
        /// Releases the reliable messages a closed connection had waiting, which Steam will never take.
        /// </summary>
        /// <param name="steamConnection">The closed connection.</param>
        /// <remarks>Steam frees a message once it is handed over, so only one never handed over is released here; with Steam shut down none can be.</remarks>
        public void Discard(HSteamNetConnection steamConnection)
        {
            if (!_queuedReliableSends.Remove(steamConnection, out Queue<StagedSend> queuedReliableSends) || NetworkingInterface.IsShutDown())
                return;

            while (queuedReliableSends.TryDequeue(out StagedSend stagedSend))
                SteamNetworkingMessage_t.Release(stagedSend.MessagePointer);
        }

        /// <summary>
        /// Forgets every staged message without handing any to Steam.
        /// </summary>
        /// <remarks>For a Steam that has shut down, which took the messages with it, and for a socket going back to its pool.</remarks>
        public void Clear()
        {
            _sends.Clear();
            _unreliableSends.Clear();
            _queuedReliableSends.Clear();
            RefusedReliableSends.Clear();
        }

        /// <summary>
        /// Copies bytes into one Steam-owned message addressed to a connection, queued behind the connection's earlier reliable messages
        /// when it is reliable.
        /// </summary>
        /// <param name="steamConnection">The connection to send on.</param>
        /// <param name="bytes">The array holding the bytes.</param>
        /// <param name="offset">Where the bytes start in <paramref name="bytes"/>.</param>
        /// <param name="count">How many bytes to copy, which may be zero.</param>
        /// <param name="isReliable">True to send on the reliable lane.</param>
        private void StagePiece(HSteamNetConnection steamConnection, byte[] bytes, int offset, int count, bool isReliable)
        {
            IntPtr messagePointer = NetworkingInterface.AllocateMessage(count);

            // An empty message has no buffer to copy into.
            if (count > 0)
                Marshal.Copy(bytes, offset, Marshal.ReadIntPtr(messagePointer, SteamMessages.DataOffset), count);

            Marshal.WriteInt32(messagePointer, SteamMessages.ConnectionOffset, (int)steamConnection.m_HSteamNetConnection);
            Marshal.WriteInt32(messagePointer, SteamMessages.FlagsOffset, isReliable ? Constants.k_nSteamNetworkingSend_ReliableNoNagle : Constants.k_nSteamNetworkingSend_UnreliableNoNagle);
            Marshal.WriteInt16(messagePointer, SteamMessages.LaneOffset, (short)(isReliable ? SteamMessages.ReliableLane : SteamMessages.UnreliableLane));

            StagedSend stagedSend = new(messagePointer, steamConnection, isReliable, count);

            if (!isReliable)
            {
                _unreliableSends.Add(stagedSend);

                return;
            }

            if (!_queuedReliableSends.TryGetValue(steamConnection, out Queue<StagedSend> queuedReliableSends))
            {
                queuedReliableSends = new();
                _queuedReliableSends.Add(steamConnection, queuedReliableSends);
            }

            queuedReliableSends.Enqueue(stagedSend);
        }

        /// <summary>
        /// Moves a connection's queued reliable messages into the next send, oldest first, while they fit in the room left.
        /// </summary>
        /// <param name="queuedReliableSends">The connection's queued reliable messages.</param>
        /// <param name="budgetBytes">The room left in the connection's send buffer, in bytes; <see cref="int.MaxValue"/> moves them all.</param>
        /// <remarks>
        /// One message larger than the whole buffer would never fit, so it goes once Steam's queue is empty, and Steam's refusal reports
        /// it rather than the link waiting forever.
        /// </remarks>
        private void ReleaseQueuedReliableSends(Queue<StagedSend> queuedReliableSends, int budgetBytes)
        {
            bool isBufferEmpty = budgetBytes >= SendBufferBytes;

            while (queuedReliableSends.Count > 0)
            {
                StagedSend stagedSend = queuedReliableSends.Peek();
                int costBytes = stagedSend.PayloadBytes + ReliableHeaderAllowanceBytes;

                if (costBytes > budgetBytes && !isBufferEmpty)
                    break;

                queuedReliableSends.Dequeue();
                _sends.Add(stagedSend);

                budgetBytes -= costBytes;
                isBufferEmpty = false;
            }
        }

        /// <summary>
        /// Measures the room left in a connection's send buffer.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <returns>
        /// The bytes Steam will still queue before it refuses a send, or <see cref="int.MaxValue"/> for a connection Steam cannot report
        /// on, which is closed and refuses whatever is sent; the socket reports that where it reports refusals.
        /// </returns>
        /// <remarks>
        /// Steam refuses a send that would take its unsent bytes past the buffer. Reliable bytes sent but not yet acknowledged are counted
        /// as well, which can only make the transport wait longer than it has to, never let a send through that Steam refuses.
        /// </remarks>
        private int GetSendBudgetBytes(HSteamNetConnection steamConnection)
        {
            if (NetworkingInterface.GetConnectionRealTimeStatus(steamConnection, out SteamNetConnectionRealTimeStatus_t realTimeStatus) is not EResult.k_EResultOK)
                return int.MaxValue;

            return SendBufferBytes - realTimeStatus.m_cbPendingReliable - realTimeStatus.m_cbPendingUnreliable - realTimeStatus.m_cbSentUnackedReliable;
        }

        /// <summary>
        /// A reliable message Steam refused.
        /// </summary>
        public readonly struct RefusedSend
        {
            /// <summary>
            /// The connection the message was addressed to.
            /// </summary>
            public readonly HSteamNetConnection SteamConnection;
            /// <summary>
            /// The length of the message's payload, in bytes.
            /// </summary>
            public readonly int PayloadBytes;
            /// <summary>
            /// Why Steam refused it.
            /// </summary>
            public readonly EResult Result;

            /// <summary>
            /// Creates a new instance.
            /// </summary>
            /// <param name="steamConnection">The connection the message was addressed to.</param>
            /// <param name="payloadBytes">The length of the message's payload, in bytes.</param>
            /// <param name="result">Why Steam refused it.</param>
            public RefusedSend(HSteamNetConnection steamConnection, int payloadBytes, EResult result)
            {
                SteamConnection = steamConnection;
                PayloadBytes = payloadBytes;
                Result = result;
            }
        }

        /// <summary>
        /// What a staged message was, kept because Steam owns the message itself once it is sent.
        /// </summary>
        private readonly struct StagedSend
        {
            /// <summary>
            /// The Steam-owned message, which this batch releases if it never hands it over.
            /// </summary>
            public readonly IntPtr MessagePointer;
            /// <summary>
            /// The connection the message was addressed to.
            /// </summary>
            public readonly HSteamNetConnection SteamConnection;
            /// <summary>
            /// True when the message rode the reliable channel.
            /// </summary>
            public readonly bool IsReliable;
            /// <summary>
            /// The length of the message's payload, in bytes.
            /// </summary>
            public readonly int PayloadBytes;

            /// <summary>
            /// Creates a new instance.
            /// </summary>
            /// <param name="messagePointer">The Steam-owned message.</param>
            /// <param name="steamConnection">The connection the message was addressed to.</param>
            /// <param name="isReliable">True when the message rode the reliable channel.</param>
            /// <param name="payloadBytes">The length of the message's payload, in bytes.</param>
            public StagedSend(IntPtr messagePointer, HSteamNetConnection steamConnection, bool isReliable, int payloadBytes)
            {
                MessagePointer = messagePointer;
                SteamConnection = steamConnection;
                IsReliable = isReliable;
                PayloadBytes = payloadBytes;
            }
        }
    }
}
#endif
