using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;
using System.Net;
using System.Net.Sockets;

namespace EchoLink.Services;

public class AudioStreamingService
{
    private static readonly Lazy<AudioStreamingService> _instance = new(() => new AudioStreamingService());
    public static AudioStreamingService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;

    private const string AudioChannel = "audio";

    // ── Server state (receiving audio from a peer) ───────────────────────────
    private CancellationTokenSource? _serverCts;

    // ── Client state (sending audio to a peer) ───────────────────────────────
    private Stream? _sendStream;

    // ── Desktop capture ──────────────────────────────────────────────────────
    private WasapiLoopbackCapture? _desktopLoopbackCapture;
    private WaveInEvent? _desktopMicCapture;

    // ── Desktop playback ─────────────────────────────────────────────────────
    private IWavePlayer? _desktopPlaybackOutput;
    private BufferedWaveProvider? _desktopPlaybackBuffer;

    // ── Opus codec ───────────────────────────────────────────────────────────
    private OpusEncoder? _sendEncoder;
    private OpusDecoder? _receiveDecoder;
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
    // SERVER – handles incoming audio channel on shared direct TCP transport
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts a local TCP server that accepts audio streams from peers.
    /// Should be called once at app startup (like RemoteControlService.StartServer).
    /// </summary>
    public void StartServer(int sampleRate = 48000, int channels = 1)
    {
        if (_serverCts != null) return;

        _receiveSampleRate = sampleRate;
        _receiveChannels = channels;

        _serverCts = new CancellationTokenSource();

        DirectTcpTransportService.Instance.RegisterHandler(AudioChannel, HandleIncomingAudioStreamAsync);
        _log.Info($"[Audio] Server channel '{AudioChannel}' active on TCP {DirectTcpTransportService.SharedPort}");
    }

    public void StopServer()
    {
        if (_serverCts == null) return;
        _serverCts.Cancel();
        _serverCts.Dispose();
        _serverCts = null;
        DirectTcpTransportService.Instance.UnregisterHandler(AudioChannel);
        CleanupPlayback();
        _log.Info("[Audio] TCP server stopped.");
    }

    private async Task HandleIncomingAudioStreamAsync(Stream stream, CancellationToken ct)
    {
        _receiveDecoder = OpusDecoder.Create(_receiveSampleRate, _receiveChannels);

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
        {
            if (RuntimeBridge is null || !RuntimeBridge.IsAvailable || !RuntimeBridge.StartPlayback(_receiveSampleRate, _receiveChannels))
            {
                _log.Error("[Audio] Playback bridge is unavailable.");
                return;
            }
        }
        else
        {
            InitializeDesktopPlayback(_receiveSampleRate, _receiveChannels);
        }

        IsReceiving = true;
        _log.Info("[Audio] Receiving audio over direct TCP channel.");

        try
        {
            var lenBuf = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                // Read 4-byte length prefix (big-endian)
                int read = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
                if (read < 4) break;

                int packetLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (packetLen <= 0 || packetLen > 8000) continue; // sanity check

                var packetBuf = new byte[packetLen];
                read = await ReadExactAsync(stream, packetBuf, 0, packetLen, ct);
                if (read < packetLen) break;

                DecodeAndPlay(packetBuf);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warning($"[Audio] Receive loop error: {ex.Message}");
        }
        finally
        {
            IsReceiving = false;
            CleanupPlayback();
            _log.Info("[Audio] Incoming audio stream ended.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SEND – Desktop loopback (system audio) over direct TCP
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
                ? $"[Audio] Linux System audio streaming -> {target.IpAddress} via direct TCP"
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

        // 1. Establish direct TCP stream to peer audio channel
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
            _log.Info($"[Audio] Desktop system audio streaming -> {target.IpAddress} via direct TCP");
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
    // SEND – Microphone over direct TCP
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

        // Establish low-latency direct TCP stream to peer audio channel
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
                ? $"[Audio] Microphone streaming -> {target.IpAddress} via direct TCP"
                : "[Audio] Failed to start microphone capture.");

            if (!started) DisconnectSendStream();
            return started;
        }

        // Desktop (Windows): send via direct TCP
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
            _log.Info($"[Audio] Desktop microphone streaming -> {target.IpAddress} via direct TCP");
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
    // Direct TCP connection for send path
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<bool> ConnectSendStreamAsync(Models.Device target, string pkeyPath, CancellationToken ct)
    {
        try
        {
            _sendStream = await DirectTcpTransportService.Instance.ConnectAsync(target.IpAddress, AudioChannel, ct);
            _log.Info($"[Audio] Direct TCP stream established to {target.IpAddress} for audio.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Audio] Failed to create direct TCP stream for audio: {ex.Message}");
            return false;
        }
    }

    private void DisconnectSendStream()
    {
        if (_sendStream != null)
        {
            try { _sendStream.Dispose(); } catch { }
            _sendStream = null;
        }
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

    private void DecodeAndPlay(byte[] opusPacket)
    {
        if (_receiveDecoder is null) return;

        short[] pcmBuffer = new short[_receiveFrameMaxSamples()];
        int decodedSamples = _receiveDecoder.Decode(opusPacket, 0, opusPacket.Length, pcmBuffer, 0, pcmBuffer.Length / _receiveChannels, false);
        if (decodedSamples <= 0) return;

        int totalSamples = decodedSamples * _receiveChannels;
        short[] frame = new short[totalSamples];
        Array.Copy(pcmBuffer, frame, totalSamples);

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
        {
            RuntimeBridge?.PlayPcm(frame, _receiveSampleRate, _receiveChannels);
        }
        else
        {
            if (_desktopPlaybackBuffer == null) return;
            byte[] bytes = Int16ToBytes(frame);
            _desktopPlaybackBuffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    private int _receiveFrameMaxSamples() => _receiveSampleRate * 2 * _receiveChannels;

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
        if (_sendEncoder == null || _sendStream == null || samples.Length == 0) return;

        lock (_sendLock)
        {
            _sendAccumulator.AddRange(samples);

            int needed = _sendFrameSize * _sendChannels;

            // LATENCY SHIELD: If we have more than 200ms of audio buffered (10 frames), 
            // the network/tunnel is too slow. Drop the old data to avoid massive lag and static.
            if (_sendAccumulator.Count > needed * 10)
            {
                _log.Warning($"[Audio] Send buffer backlog detected ({_sendAccumulator.Count} samples). Dropping frames to maintain real-time.");
                _sendAccumulator.RemoveRange(0, _sendAccumulator.Count - needed);
            }

            while (_sendAccumulator.Count >= needed)
            {
                short[] frame = new short[needed];
                _sendAccumulator.CopyTo(0, frame, 0, needed);
                _sendAccumulator.RemoveRange(0, needed);

                byte[] encoded = new byte[4000];
                int encodedLength = _sendEncoder.Encode(frame, 0, _sendFrameSize, encoded, 0, encoded.Length);
                if (encodedLength <= 0) continue;

                try
                {
                    // Write 4-byte big-endian length prefix
                    byte[] lenPrefix = new byte[4];
                    lenPrefix[0] = (byte)((encodedLength >> 24) & 0xFF);
                    lenPrefix[1] = (byte)((encodedLength >> 16) & 0xFF);
                    lenPrefix[2] = (byte)((encodedLength >> 8) & 0xFF);
                    lenPrefix[3] = (byte)(encodedLength & 0xFF);

                    _sendStream.Write(lenPrefix, 0, 4);
                    _sendStream.Write(encoded, 0, encodedLength);
                    _sendStream.Flush();
                }
                catch (Exception ex)
                {
                    _log.Warning($"[Audio] TCP send failed: {ex.Message}");
                    break;
                }
            }
        }
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
}
