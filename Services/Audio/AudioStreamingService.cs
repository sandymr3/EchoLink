using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;
using System.Net;
using System.Net.Sockets;
using EchoLink.Services.UnifiedProtocol;

namespace EchoLink.Services;

public class AudioStreamingService
{
    private static readonly Lazy<AudioStreamingService> _instance = new(() => new AudioStreamingService());
    public static AudioStreamingService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;

    // ── Desktop capture ──────────────────────────────────────────────────────
    private WasapiLoopbackCapture? _desktopLoopbackCapture;
    private WaveInEvent? _desktopMicCapture;

    // ── Desktop playback ─────────────────────────────────────────────────────
    private IWavePlayer? _desktopPlaybackOutput;
    private BufferedWaveProvider? _desktopPlaybackBuffer;

    // ── Opus codec ───────────────────────────────────────────────────────────
    private OpusEncoder? _sendEncoder;
    private readonly List<short> _sendAccumulator = new();
    private readonly object _sendLock = new();

    private int _sendSampleRate = 48000;
    private int _sendChannels = 1;
    private int _sendFrameSize = 960;

    private int _receiveSampleRate = 48000;
    private int _receiveChannels = 1;

    // ── Android bridge ───────────────────────────────────────────────────────
    public IAudioRuntimeBridge? RuntimeBridge { get; set; }

    public bool IsSending { get; private set; }
    public bool IsReceiving { get; private set; }

    private AudioStreamingService() { }

    // ═════════════════════════════════════════════════════════════════════════
    // SEND – Desktop loopback (system audio) over SSH-tunneled TCP
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures desktop system audio (loopback) and sends it over an SSH-tunneled TCP stream.
    /// </summary>
    public async Task<bool> StartLoopbackSendAsync(Models.Device target, string pkeyPath, CancellationToken ct = default)
    {
        if (OperatingSystem.IsAndroid())
        {
            _log.Warning("[Audio] Loopback capture is not supported on Android.");
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            StopSend();

            _sendSampleRate = 48000;
            _sendChannels = 1;
            _sendFrameSize = 960;
            _sendEncoder = OpusEncoder.Create(_sendSampleRate, _sendChannels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            _sendEncoder.Bitrate = 48000;

            if (!await ConnectSendStreamAsync(target, pkeyPath, ct))
                return false;

            if (RuntimeBridge is null || !RuntimeBridge.IsAvailable || !RuntimeBridge.CanCaptureSystemAudio)
            {
                _log.Error("[Audio] System audio bridge unavailable on Linux.");
                DisconnectSendStream();
                return false;
            }

            // Note: LinuxAudioRuntimeBridge.StartSystemAudioCapture captures the system audio 
            // routed to the EchoLink_Sink virtual microphone.
            bool started = RuntimeBridge.StartSystemAudioCapture(samples => EncodeAndSendFrames(samples), _sendSampleRate, _sendChannels);
            IsSending = started;
            _log.Info(started
                ? $"[Audio] Linux System audio streaming -> {target.IpAddress} via Unified Protocol"
                : "[Audio] Failed to start system audio capture.");

            if (!started) DisconnectSendStream();
            return started;
        }

        if (!OperatingSystem.IsWindows())
        {
            _log.Warning("[Audio] Loopback send currently supports Windows desktop and Linux only.");
            return false;
        }

        StopSend();

        // 1. Establish SSH tunnel to peer's audio server port
        if (!await ConnectSendStreamAsync(target, pkeyPath, ct))
            return false;

        // 2. Set up loopback capture
        _desktopLoopbackCapture = new WasapiLoopbackCapture();
        int deviceRate = _desktopLoopbackCapture.WaveFormat.SampleRate;
        if (!IsOpusSampleRateSupported(deviceRate))
        {
            _log.Error($"[Audio] Unsupported loopback sample rate for Opus: {deviceRate}");
            _desktopLoopbackCapture.Dispose();
            _desktopLoopbackCapture = null;
            DisconnectSendStream();
            return false;
        }

        _sendSampleRate = deviceRate;
        _sendChannels = 1;
        _sendFrameSize = _sendSampleRate / 50;
        _sendEncoder = OpusEncoder.Create(_sendSampleRate, _sendChannels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _sendEncoder.Bitrate = 48000;

        _desktopLoopbackCapture.DataAvailable += (_, args) =>
        {
            try
            {
                var pcm = ConvertWasapiFloatStereoToMonoInt16(args.Buffer, args.BytesRecorded);
                EncodeAndSendFrames(pcm);
            }
            catch (Exception ex)
            {
                _log.Warning($"[Audio] Loopback frame failed: {ex.Message}");
            }
        };
        _desktopLoopbackCapture.RecordingStopped += (_, _) => _log.Info("[Audio] Desktop loopback stopped.");

        try
        {
            _desktopLoopbackCapture.StartRecording();
            IsSending = true;
            _log.Info($"[Audio] Desktop system audio streaming -> {target.IpAddress} via Unified Protocol");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Audio] Failed to start desktop loopback: {ex.Message}");
            StopSend();
            return false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SEND – Microphone over SSH-tunneled TCP
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures microphone audio and sends it over an SSH-tunneled TCP stream.
    /// </summary>
    public async Task<bool> StartMicrophoneSendAsync(Models.Device target, string pkeyPath, CancellationToken ct = default)
    {
        StopSend();

        _sendSampleRate = 48000;
        _sendChannels = 1;
        _sendFrameSize = 960;
        _sendEncoder = OpusEncoder.Create(_sendSampleRate, _sendChannels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _sendEncoder.Bitrate = 24000;

        // Establish the low-latency SSH tunnel (bypasses Windows PC UDP limits)
        if (!await ConnectSendStreamAsync(target, pkeyPath, ct))
            return false;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
        {
            if (RuntimeBridge is null || !RuntimeBridge.IsAvailable || !RuntimeBridge.CanCaptureMicrophone)
            {
                _log.Error("[Audio] Microphone bridge unavailable.");
                DisconnectSendStream();
                return false;
            }

            // Stream PCM direct into the TCP tunnel instead of using local UDP
            bool started = RuntimeBridge.StartMicrophoneCapture(samples => EncodeAndSendFrames(samples), _sendSampleRate, _sendChannels);
            IsSending = started;
            _log.Info(started
                ? $"[Audio] Microphone streaming -> {target.IpAddress} via Unified Protocol"
                : "[Audio] Failed to start microphone capture.");

            if (!started) DisconnectSendStream();
            return started;
        }

        // Desktop (Windows): send via Unified Protocol
        _desktopMicCapture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_sendSampleRate, 16, _sendChannels),
            BufferMilliseconds = 20
        };

        _desktopMicCapture.DataAvailable += (_, args) =>
        {
            var samples = BytesToInt16(args.Buffer, args.BytesRecorded);
            EncodeAndSendFrames(samples);
        };
        _desktopMicCapture.RecordingStopped += (_, _) => _log.Info("[Audio] Desktop microphone capture stopped.");

        try
        {
            _desktopMicCapture.StartRecording();
            IsSending = true;
            _log.Info($"[Audio] Desktop microphone streaming -> {target.IpAddress} via Unified Protocol");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Audio] Failed to start desktop microphone: {ex.Message}");
            StopSend();
            return false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SSH tunnel connection for send path
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<bool> ConnectSendStreamAsync(Models.Device target, string pkeyPath, CancellationToken ct)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            _log.Info($"[Audio] Using existing Unified connection to {target.IpAddress}");
            return true;
        }

        return await UnifiedProtocolClient.Instance.ConnectAsync(target.IpAddress, pkeyPath, ct);
    }

    private void DisconnectSendStream()
    {
        // Don't forcefully disconnect if shared
    }

    // ═════════════════════════════════════════════════════════════════════════
    // STOP
    // ═════════════════════════════════════════════════════════════════════════

    public void StopSend()
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
        {
            RuntimeBridge?.StopMicrophoneCapture();
            RuntimeBridge?.StopSystemAudioCapture();
        }

        if (_desktopLoopbackCapture != null)
        {
            try { _desktopLoopbackCapture.StopRecording(); } catch { }
            _desktopLoopbackCapture.Dispose();
            _desktopLoopbackCapture = null;
        }

        if (_desktopMicCapture != null)
        {
            try { _desktopMicCapture.StopRecording(); } catch { }
            _desktopMicCapture.Dispose();
            _desktopMicCapture = null;
        }

        _sendEncoder = null;

        lock (_sendLock)
        {
            _sendAccumulator.Clear();
        }

        DisconnectSendStream();

        _udpSendClient?.Dispose();
        _udpSendClient = null;

        IsSending = false;
    }

    public async Task StopAllAsync()
    {
        StopSend();
        await Task.CompletedTask;
        // Note: the TCP server keeps running (like RemoteControlService.StartServer)
        // Individual incoming connections are cleaned up when they end
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Decode + Play
    // ═════════════════════════════════════════════════════════════════════════

    private void InitializeDesktopPlayback(int sampleRate, int channels)
    {
        _desktopPlaybackBuffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(50)
        };

        if (OperatingSystem.IsWindows())
        {
            var virtualMicService = new VirtualMicService();
            var virtualDevice = virtualMicService.GetVirtualSpeakerDevice();
            
            if (virtualDevice != null)
            {
                _log.Info($"[Audio] Routing playback to virtual driver: {virtualDevice.FriendlyName}");
                _desktopPlaybackOutput = new WasapiOut(virtualDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
            }
            else
            {
                _log.Warning("[Audio] Virtual audio driver not found. Falling back to default speakers.");
                _desktopPlaybackOutput = new WaveOutEvent() { DesiredLatency = 50 };
            }
        }
        else
        {
            _desktopPlaybackOutput = new WaveOutEvent() { DesiredLatency = 50 };
        }

        _desktopPlaybackOutput.Init(_desktopPlaybackBuffer);
        _desktopPlaybackOutput.Play();
    }

    private void CleanupPlayback()
    {
        _desktopPlaybackOutput?.Stop();
        _desktopPlaybackOutput?.Dispose();
        _desktopPlaybackOutput = null;
        _desktopPlaybackBuffer = null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Encode + Send (TCP — length-prefixed frames)
    // ═════════════════════════════════════════════════════════════════════════

    private void EncodeAndSendFrames(short[] samples)
    {
        if (!UnifiedProtocolClient.Instance.IsConnected || samples.Length == 0) return;

        // Note: For simplicity and to match the unified receiver, we send raw PCM directly.
        _ = UnifiedProtocolClient.Instance.SendAudioFrameAsync(samples, CancellationToken.None);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Encode + Send (UDP — Android Go mesh bridge path)
    // ═════════════════════════════════════════════════════════════════════════

    private UdpClient? _udpSendClient;

    private void EncodeAndSendUdpFrames(short[] samples, IPEndPoint endpoint)
    {
        if (_sendEncoder == null || _udpSendClient == null || samples.Length == 0) return;

        lock (_sendLock)
        {
            _sendAccumulator.AddRange(samples);

            int needed = _sendFrameSize * _sendChannels;
            while (_sendAccumulator.Count >= needed)
            {
                short[] frame = new short[needed];
                _sendAccumulator.CopyTo(0, frame, 0, needed);
                _sendAccumulator.RemoveRange(0, needed);

                byte[] encoded = new byte[4000];
                int encodedLength = _sendEncoder.Encode(frame, 0, _sendFrameSize, encoded, 0, encoded.Length);
                if (encodedLength <= 0) continue;

                _udpSendClient.Send(encoded, encodedLength, endpoint);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
            if (read == 0) return totalRead; // stream closed
            totalRead += read;
        }
        return totalRead;
    }

    private static short[] ConvertWasapiFloatStereoToMonoInt16(byte[] buffer, int count)
    {
        int floatCount = count / sizeof(float);
        if (floatCount < 2) return Array.Empty<short>();

        int samplePairs = floatCount / 2;
        short[] mono = new short[samplePairs];

        for (int i = 0; i < samplePairs; i++)
        {
            float left = BitConverter.ToSingle(buffer, (i * 2) * sizeof(float));
            float right = BitConverter.ToSingle(buffer, (i * 2 + 1) * sizeof(float));
            float mixed = (left + right) * 0.5f;
            mixed = Math.Clamp(mixed, -1f, 1f);
            mono[i] = (short)(mixed * short.MaxValue);
        }

        return mono;
    }

    private static short[] BytesToInt16(byte[] bytes, int count)
    {
        short[] result = new short[count / 2];
        Buffer.BlockCopy(bytes, 0, result, 0, count);
        return result;
    }

    private static byte[] Int16ToBytes(short[] samples)
    {
        byte[] result = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, result, 0, result.Length);
        return result;
    }

    private static bool IsOpusSampleRateSupported(int sampleRate)
    {
        return sampleRate == 8000
            || sampleRate == 12000
            || sampleRate == 16000
            || sampleRate == 24000
            || sampleRate == 48000;
    }

    // === Unified Protocol Integration ===

    /// <summary>
    /// Initialize unified protocol handlers for audio streaming.
    /// Call this once at application startup.
    /// </summary>
    public void InitializeUnifiedProtocol()
    {
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.AudioFrame,
            async (payload, reply, ct) => await HandleAudioFrameUnifiedAsync(payload, ct));
        
        _log.Info("[AudioStreaming] Unified protocol handler registered");
    }

    /// <summary>
    /// Handle audio frame received via unified protocol.
    /// Payload is raw PCM samples (16-bit).
    /// </summary>
    private async Task HandleAudioFrameUnifiedAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length == 0) return;

        // Convert byte array to short array (PCM samples)
        int sampleCount = payload.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(payload, 0, samples, 0, payload.Length);

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
        {
            RuntimeBridge?.PlayPcm(samples, _receiveSampleRate, _receiveChannels);
        }
        else
        {
            if (_desktopPlaybackBuffer == null)
            {
                InitializeDesktopPlayback(_receiveSampleRate, _receiveChannels);
            }
            if (_desktopPlaybackBuffer != null)
            {
                _desktopPlaybackBuffer.AddSamples(payload, 0, payload.Length);
            }
        }
        
        await Task.CompletedTask;
    }
}
