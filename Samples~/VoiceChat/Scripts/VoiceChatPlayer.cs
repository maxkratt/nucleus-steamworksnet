// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using System;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Systems;
using Nucleus.Managers.Rpc;
using Nucleus.Systems;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Nucleus.Integrations.SteamworksNet.Samples.VoiceChat
{
    /// <summary>
    /// One player's voice. The peer that controls it records through Steam while the push to talk key is held and sends each
    /// compressed chunk as a <see cref="VoiceChatRpc"/> on this object; every other client decompresses what arrives and plays it on
    /// this object's <see cref="AudioSource"/>. The capsule lights up, and its name tag shows a speaking icon, while its player is talking.
    /// </summary>
    /// <remarks>
    /// The scene's <see cref="NetworkPlayerSpawner"/> spawns it on the server and hands control to its player, so the default
    /// <see cref="RpcSendAccess.Controller"/> already lets that player send. A client's call goes to the server, which relays it to
    /// the other observers; a host's own call is sent as the server. Sent unreliable: a lost chunk is a short gap, while a resent one
    /// would arrive too late to play. It adopts the system the <see cref="Components.NetworkTransform"/> declares.
    /// </remarks>
    [HelpURL("https://github.com/maxkratt/nucleus-steamworksnet/blob/main/Samples~/VoiceChat/README.md")]
    public class VoiceChatPlayer : NucleusBehaviour
    {
        /// <summary>
        /// The key the controlling player holds to talk.
        /// </summary>
        [SerializeField]
        [Tooltip("The key the controlling player holds to talk.")]
        private Key _pushToTalkKey = Key.V;
        /// <summary>
        /// The source received voice plays on. Set its spatial blend for positional voice.
        /// </summary>
        [SerializeField]
        [Tooltip("The source received voice plays on.")]
        private AudioSource _audioSource;
        /// <summary>
        /// The renderer lit while this player is talking.
        /// </summary>
        [SerializeField]
        [Tooltip("The renderer lit while this player is talking.")]
        private Renderer _bodyRenderer;
        /// <summary>
        /// The name tag that shows a speaking icon while this player is talking.
        /// </summary>
        [SerializeField]
        [Tooltip("The name tag that shows a speaking icon while this player is talking.")]
        private PlayerNameTag _playerNameTag;

        /// <summary>
        /// This capsule's own material instance, so lighting it leaves the shared asset alone.
        /// </summary>
        private Material _bodyMaterial;
        /// <summary>
        /// The capsule's colour while its player is silent, read from its material.
        /// </summary>
        private Color _bodyColor = Color.white;
        /// <summary>
        /// Where <see cref="SteamUser.GetVoice"/> writes compressed voice for a call to send, and where a received chunk that does not start its array is copied for <see cref="SteamUser.DecompressVoice"/>.
        /// </summary>
        private readonly byte[] _compressedVoiceBuffer = new byte[CompressedVoiceBufferLength];
        /// <summary>
        /// Where <see cref="SteamUser.DecompressVoice"/> writes 16 bit mono PCM, sized in <see cref="StartPlayback"/>.
        /// </summary>
        private byte[] _decompressedVoiceBuffer;
        /// <summary>
        /// The ring of received samples the audio thread plays from, one second long, or null until the first voice arrives.
        /// </summary>
        private float[] _playbackSamples;
        /// <summary>
        /// The streamed clip that pulls <see cref="_playbackSamples"/> through <see cref="OnAudioRead"/>.
        /// </summary>
        private AudioClip _voiceClip;
        /// <summary>
        /// Guards the ring, which the main thread writes and the audio thread reads.
        /// </summary>
        private readonly object _playbackLock = new();
        /// <summary>
        /// The next ring index a received sample is written to.
        /// </summary>
        private int _playbackWriteIndex;
        /// <summary>
        /// The next ring index the audio thread plays.
        /// </summary>
        private int _playbackReadIndex;
        /// <summary>
        /// How many received samples wait in the ring.
        /// </summary>
        private int _bufferedSampleCount;
        /// <summary>
        /// True while playback waits for <see cref="PrebufferSeconds"/> of voice, so chunks arriving unevenly do not stutter.
        /// </summary>
        private bool _isPrebuffering = true;
        /// <summary>
        /// The rate voice is decompressed and played at, Steam's optimal rate for this machine.
        /// </summary>
        private uint _playbackSampleRate;
        /// <summary>
        /// True while Steam is recording for this player.
        /// </summary>
        private bool _isRecording;
        /// <summary>
        /// The <see cref="Time.unscaledTime"/> this player's voice was last sent or received.
        /// </summary>
        private float _lastVoiceTime = float.NegativeInfinity;
        /// <summary>
        /// Whether the capsule is drawn lit, so its material changes only on an edge.
        /// </summary>
        private bool _isLit;

        /// <summary>
        /// The most compressed voice read from Steam at once; Steam recommends a buffer of up to 8 KiB.
        /// </summary>
        private const int CompressedVoiceBufferLength = 8 * 1024;
        /// <summary>
        /// How long the capsule stays lit after the last voice, in seconds.
        /// </summary>
        private const float TalkingHoldSeconds = 0.25f;
        /// <summary>
        /// How much voice playback gathers before it starts, and again after running dry, in seconds.
        /// </summary>
        private const float PrebufferSeconds = 0.1f;

        /// <summary>
        /// Takes this capsule's own material instance, then lets the base adopt the system.
        /// </summary>
        /// <remarks>
        /// The material is taken first because on a receiving client the system can already be linked when this runs, and the base
        /// then raises <see cref="OnSystemLinked"/> from inside its own <c>Awake</c>.
        /// </remarks>
        protected override void Awake()
        {
            if (_bodyRenderer != null)
            {
                _bodyMaterial = _bodyRenderer.material;
                _bodyColor = _bodyMaterial.color;
            }

            base.Awake();
        }

        /// <summary>
        /// Stops recording, then destroys the clip and the material instance.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();

            SetRecording(isRecording: false);

            if (_voiceClip != null)
                Destroy(_voiceClip);

            if (_bodyMaterial != null)
                Destroy(_bodyMaterial);
        }

        /// <summary>
        /// Listens for this player's voice on the newly linked system.
        /// </summary>
        protected override void OnSystemLinked() => NetworkSystem.RegisterRpcHandler<VoiceChatRpc>(OnVoiceReceived);

        /// <summary>
        /// Stops recording and drops any voice still waiting, so a pooled capsule starts its next life silent.
        /// </summary>
        protected override void OnSystemUnlinked()
        {
            SetRecording(isRecording: false);

            lock (_playbackLock)
            {
                _bufferedSampleCount = 0;
                _playbackReadIndex = _playbackWriteIndex;
                _isPrebuffering = true;
            }
        }

        /// <summary>
        /// Follows the push to talk key and sends what Steam recorded, then lights the capsule while its player talks.
        /// </summary>
        /// <remarks>
        /// A held key is a property of the frame, and Steam offers no callback for recorded voice, so both are read here. Steam keeps
        /// returning voice for a moment after recording stops, which is sent too.
        /// </remarks>
        private void Update()
        {
            if (IsController(ControllerType.Client) && NetworkSystem.IsStarted)
            {
                Keyboard keyboard = Keyboard.current;
                SetRecording(keyboard is not null && keyboard[_pushToTalkKey].isPressed);
                SendRecordedVoice();
            }

            bool isTalking = Time.unscaledTime - _lastVoiceTime < TalkingHoldSeconds;

            if (isTalking == _isLit)
                return;

            _isLit = isTalking;

            if (_bodyMaterial != null)
                _bodyMaterial.color = isTalking ? Color.green : _bodyColor;

            if (_playerNameTag != null)
                _playerNameTag.IsSpeaking = isTalking;
        }

        /// <summary>
        /// Starts or stops Steam's recording, telling Steam's friends list whether this player is speaking in game.
        /// </summary>
        /// <param name="isRecording">True to record.</param>
        private void SetRecording(bool isRecording)
        {
            if (isRecording == _isRecording)
                return;

            _isRecording = isRecording;

            if (isRecording)
                SteamUser.StartVoiceRecording();
            else
                SteamUser.StopVoiceRecording();

            SteamFriends.SetInGameVoiceSpeaking(SteamUser.GetSteamID(), isRecording);
        }

        /// <summary>
        /// Sends whatever compressed voice Steam has ready to every other observer of this player.
        /// </summary>
        private void SendRecordedVoice()
        {
            if (SteamUser.GetAvailableVoice(out uint availableByteCount) is not EVoiceResult.k_EVoiceResultOK || availableByteCount == 0)
                return;

            if (SteamUser.GetVoice(bWantCompressed: true, _compressedVoiceBuffer, CompressedVoiceBufferLength, out uint writtenByteCount) is not EVoiceResult.k_EVoiceResultOK || writtenByteCount == 0)
                return;

            // The call is written at the send site, so the buffer is free again once it returns.
            ArraySegment<byte> compressedVoice = new(_compressedVoiceBuffer, 0, (int)writtenByteCount);

            if (NetworkSystem.TrySendRpc(RpcTarget.Observers, Channel.Unreliable, new VoiceChatRpc { CompressedVoice = compressedVoice }))
                _lastVoiceTime = Time.unscaledTime;
        }

        /// <summary>
        /// Plays a received chunk of this player's voice on clients other than the speaker's own, then returns its pooled array.
        /// </summary>
        /// <param name="rpcContext">Who sent the call and how it arrived.</param>
        /// <param name="voiceChatRpc">The compressed voice.</param>
        /// <returns><see cref="RpcRelayAction.Relay"/> always, so the server passes every chunk on.</returns>
        /// <remarks>
        /// A dedicated server only relays; a host plays a remote player's voice for its own player and relays it to the rest. The relay
        /// sends the received bytes, not this segment, so returning the array here is safe on the server too.
        /// </remarks>
        private RpcRelayAction OnVoiceReceived(in RpcContext rpcContext, VoiceChatRpc voiceChatRpc)
        {
            ArraySegment<byte> compressedVoice = voiceChatRpc.CompressedVoice;

            if (compressedVoice.Array is null)
                return RpcRelayAction.Relay;

            if (IsStarted(Invoker.Client) && !IsController(ControllerType.Client))
                PlayVoice(compressedVoice);

            // Only a call read back from bytes holds a pooled array; a call that was never serialized holds the sender's own.
            if (rpcContext.IsDeserialized)
                TrackedArrayPool<byte>.Return(compressedVoice.Array);

            return RpcRelayAction.Relay;
        }

        /// <summary>
        /// Decompresses one chunk of voice into the playback ring.
        /// </summary>
        /// <param name="compressedVoice">The compressed voice, valid only for this call.</param>
        private void PlayVoice(ArraySegment<byte> compressedVoice)
        {
            if (_voiceClip == null)
                StartPlayback();

            byte[] compressedVoiceArray = compressedVoice.Array;

            // Steam reads from the start of the array, so a chunk that starts further in is copied to the front of a buffer first.
            if (compressedVoice.Offset != 0)
            {
                compressedVoiceArray = _compressedVoiceBuffer;
                Buffer.BlockCopy(compressedVoice.Array, compressedVoice.Offset, compressedVoiceArray, 0, Math.Min(compressedVoice.Count, CompressedVoiceBufferLength));
            }

            EVoiceResult voiceResult = SteamUser.DecompressVoice(compressedVoiceArray, (uint)compressedVoice.Count, _decompressedVoiceBuffer, (uint)_decompressedVoiceBuffer.Length, out uint decompressedByteCount, _playbackSampleRate);

            if (voiceResult is not EVoiceResult.k_EVoiceResultOK)
            {
                Debug.LogWarning($"[{nameof(VoiceChatPlayer)}] could not decompress a voice chunk: [{voiceResult}].");

                return;
            }

            QueueSamples((int)decompressedByteCount);
            _lastVoiceTime = Time.unscaledTime;
        }

        /// <summary>
        /// Creates the ring and the streamed clip at Steam's optimal rate and starts the source, on the first voice received.
        /// </summary>
        private void StartPlayback()
        {
            _playbackSampleRate = SteamUser.GetVoiceOptimalSampleRate();
            int sampleRate = (int)_playbackSampleRate;
            _decompressedVoiceBuffer = new byte[sampleRate * sizeof(short)];
            _playbackSamples = new float[sampleRate];

            _voiceClip = AudioClip.Create($"{name} Voice", sampleRate, channels: 1, sampleRate, stream: true, OnAudioRead);
            _audioSource.clip = _voiceClip;
            _audioSource.loop = true;
            _audioSource.Play();
        }

        /// <summary>
        /// Converts decompressed 16 bit little endian PCM to samples and adds them to the ring, dropping what does not fit.
        /// </summary>
        /// <param name="decompressedByteCount">How many bytes of <see cref="_decompressedVoiceBuffer"/> hold PCM.</param>
        private void QueueSamples(int decompressedByteCount)
        {
            int ringLength = _playbackSamples.Length;
            int prebufferSampleCount = (int)(_playbackSampleRate * PrebufferSeconds);

            lock (_playbackLock)
            {
                for (int i = 0; i + 1 < decompressedByteCount && _bufferedSampleCount < ringLength; i += sizeof(short))
                {
                    short sample = (short)(_decompressedVoiceBuffer[i] | (_decompressedVoiceBuffer[i + 1] << 8));
                    _playbackSamples[_playbackWriteIndex] = sample / 32768f;
                    _playbackWriteIndex = (_playbackWriteIndex + 1) % ringLength;
                    _bufferedSampleCount++;
                }

                if (_bufferedSampleCount >= prebufferSampleCount)
                    _isPrebuffering = false;
            }
        }

        /// <summary>
        /// Fills the clip's next block from the ring, with silence while prebuffering or once the ring runs dry.
        /// </summary>
        /// <param name="data">The block Unity's audio thread asks for.</param>
        private void OnAudioRead(float[] data)
        {
            lock (_playbackLock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (_bufferedSampleCount == 0)
                        _isPrebuffering = true;

                    if (_isPrebuffering)
                    {
                        data[i] = 0f;

                        continue;
                    }

                    data[i] = _playbackSamples[_playbackReadIndex];
                    _playbackReadIndex = (_playbackReadIndex + 1) % _playbackSamples.Length;
                    _bufferedSampleCount--;
                }
            }
        }
    }
}
#endif
