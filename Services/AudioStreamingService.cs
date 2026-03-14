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

    private const int AndroidMeshInboundPort = 4000;
    private const int DesktopMicInboundPort = 4002;
    private const int AndroidLocalMicUplinkPort = 4002;

    private UdpClient? _sendClient;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    private WasapiLoopbackCapture? _desktopLoopbackCapture;
    private WaveInEvent? _desktopMicCapture;

    private WaveOutEvent? _desktopPlaybackOutput;
    private BufferedWaveProvider? _desktopPlaybackBuffer;

    private OpusEncoder? _sendEncoder;
    private OpusDecoder? _receiveDecoder;
    private readonly List<short> _sendAccumulator = new();
    private readonly object _sendLock = new();

    private int _sendSampleRate = 48000;
    private int _sendChannels = 1;
    private int _sendFrameSize = 960;

    private int _receiveSampleRate = 48000;
    private int _receiveChannels = 1;

    public IAudioRuntimeBridge? RuntimeBridge { get; set; }

    public bool IsSending { get; private set; }
    public bool IsReceiving { get; private set; }

    private AudioStreamingService() { }

    public async Task<bool> StartReceiveAsync(int localPort, int sampleRate = 48000, int channels = 1, CancellationToken ct = default)
    {
        await StopReceiveAsync();

        _receiveSampleRate = sampleRate;
        _receiveChannels = channels;
        _receiveDecoder = OpusDecoder.Create(_receiveSampleRate, _receiveChannels);

        if (OperatingSystem.IsAndroid())
        {
            if (RuntimeBridge is null || !RuntimeBridge.IsAvailable || !RuntimeBridge.StartPlayback(sampleRate, channels))
            {
                _log.Error("[Audio] Android playback bridge is unavailable.");
                return false;
            }
        }
        else
        {
            InitializeDesktopPlayback(sampleRate, channels);
        }

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _receiveCts.Token;

        _receiveTask = Task.Run(async () =>
        {
            using var receiver = new UdpClient(localPort);
            _log.Info($"[Audio] Receiving UDP audio on 127.0.0.1:{localPort}");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var packet = await receiver.ReceiveAsync(token);
                    DecodeAndPlay(packet.Buffer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warning($"[Audio] Receive loop error: {ex.Message}");
                }
            }
        }, token);

        IsReceiving = true;
        return true;
    }

    public async Task StopReceiveAsync()
    {
        if (_receiveCts != null)
        {
            _receiveCts.Cancel();
            if (_receiveTask != null)
            {
                try { await _receiveTask; } catch { }
            }

            _receiveCts.Dispose();
            _receiveCts = null;
            _receiveTask = null;
        }

        if (OperatingSystem.IsAndroid())
        {
            RuntimeBridge?.StopPlayback();
        }
        else
        {
            _desktopPlaybackOutput?.Stop();
            _desktopPlaybackOutput?.Dispose();
            _desktopPlaybackOutput = null;
            _desktopPlaybackBuffer = null;
        }

        IsReceiving = false;
    }

    public Task<bool> StartLoopbackSendAsync(string remoteIp, CancellationToken ct = default)
    {
        if (OperatingSystem.IsAndroid())
        {
            _log.Warning("[Audio] Loopback capture is not supported on Android.");
            return Task.FromResult(false);
        }

        if (!OperatingSystem.IsWindows())
        {
            _log.Warning("[Audio] Loopback send currently supports Windows desktop only.");
            return Task.FromResult(false);
        }

        StopSend();

        _desktopLoopbackCapture = new WasapiLoopbackCapture();
        int deviceRate = _desktopLoopbackCapture.WaveFormat.SampleRate;
        if (!IsOpusSampleRateSupported(deviceRate))
        {
            _log.Error($"[Audio] Unsupported loopback sample rate for Opus: {deviceRate}");
            _desktopLoopbackCapture.Dispose();
            _desktopLoopbackCapture = null;
            return Task.FromResult(false);
        }

        _sendSampleRate = deviceRate;
        _sendChannels = 1;
        _sendFrameSize = _sendSampleRate / 50;
        _sendEncoder = OpusEncoder.Create(_sendSampleRate, _sendChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _sendEncoder.Bitrate = 32000;

        _sendClient = new UdpClient();
        var target = new IPEndPoint(IPAddress.Parse(remoteIp), AndroidMeshInboundPort);

        _desktopLoopbackCapture.DataAvailable += (_, args) =>
        {
            try
            {
                var pcm = ConvertWasapiFloatStereoToMonoInt16(args.Buffer, args.BytesRecorded);
                EncodeAndSendFrames(pcm, target);
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
            _log.Info($"[Audio] Desktop speaker streaming -> {remoteIp}:{AndroidMeshInboundPort}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.Error($"[Audio] Failed to start desktop loopback: {ex.Message}");
            StopSend();
            return Task.FromResult(false);
        }
    }

    public Task<bool> StartMicrophoneSendAsync(string remoteIp, CancellationToken ct = default)
    {
        StopSend();

        _sendSampleRate = 48000;
        _sendChannels = 1;
        _sendFrameSize = 960;
        _sendEncoder = OpusEncoder.Create(_sendSampleRate, _sendChannels, OpusApplication.OPUS_APPLICATION_VOIP);
        _sendEncoder.Bitrate = 24000;

        if (OperatingSystem.IsAndroid())
        {
            TailscaleService.Instance.NativeBridge?.SetAudioTargetHost(remoteIp);

            if (RuntimeBridge is null || !RuntimeBridge.IsAvailable || !RuntimeBridge.CanCaptureMicrophone)
            {
                _log.Error("[Audio] Android microphone bridge unavailable.");
                return Task.FromResult(false);
            }

            _sendClient = new UdpClient();
            var endpoint = new IPEndPoint(IPAddress.Loopback, AndroidLocalMicUplinkPort);

            bool started = RuntimeBridge.StartMicrophoneCapture(samples => EncodeAndSendFrames(samples, endpoint), _sendSampleRate, _sendChannels);
            IsSending = started;
            _log.Info(started
                ? $"[Audio] Android microphone streaming -> local UDP :{AndroidLocalMicUplinkPort}"
                : "[Audio] Failed to start Android microphone capture.");

            return Task.FromResult(started);
        }

        _sendClient = new UdpClient();
        var target = new IPEndPoint(IPAddress.Parse(remoteIp), DesktopMicInboundPort);

        _desktopMicCapture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_sendSampleRate, 16, _sendChannels),
            BufferMilliseconds = 20
        };

        _desktopMicCapture.DataAvailable += (_, args) =>
        {
            var samples = BytesToInt16(args.Buffer, args.BytesRecorded);
            EncodeAndSendFrames(samples, target);
        };
        _desktopMicCapture.RecordingStopped += (_, _) => _log.Info("[Audio] Desktop microphone capture stopped.");

        try
        {
            _desktopMicCapture.StartRecording();
            IsSending = true;
            _log.Info($"[Audio] Desktop microphone streaming -> {remoteIp}:{DesktopMicInboundPort}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.Error($"[Audio] Failed to start desktop microphone: {ex.Message}");
            StopSend();
            return Task.FromResult(false);
        }
    }

    public void StopSend()
    {
        if (OperatingSystem.IsAndroid())
        {
            RuntimeBridge?.StopMicrophoneCapture();
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

        _sendClient?.Dispose();
        _sendClient = null;
        _sendEncoder = null;

        lock (_sendLock)
        {
            _sendAccumulator.Clear();
        }

        IsSending = false;
    }

    public async Task StopAllAsync()
    {
        StopSend();
        await StopReceiveAsync();
    }

    private void DecodeAndPlay(byte[] opusPacket)
    {
        if (_receiveDecoder is null) return;

        short[] pcmBuffer = new short[_receiveFrameMaxSamples()];
        int decodedSamples = _receiveDecoder.Decode(opusPacket, 0, opusPacket.Length, pcmBuffer, 0, pcmBuffer.Length / _receiveChannels, false);
        if (decodedSamples <= 0) return;

        int totalSamples = decodedSamples * _receiveChannels;
        short[] frame = new short[totalSamples];
        Array.Copy(pcmBuffer, frame, totalSamples);

        if (OperatingSystem.IsAndroid())
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
            BufferDuration = TimeSpan.FromMilliseconds(500)
        };

        _desktopPlaybackOutput = new WaveOutEvent();
        _desktopPlaybackOutput.Init(_desktopPlaybackBuffer);
        _desktopPlaybackOutput.Play();
    }

    private void EncodeAndSendFrames(short[] samples, IPEndPoint endpoint)
    {
        if (_sendEncoder == null || _sendClient == null || samples.Length == 0) return;

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

                _sendClient.Send(encoded, encodedLength, endpoint);
            }
        }
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
