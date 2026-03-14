using Android.Media;
using EchoLink.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Android;

public class AndroidAudioRuntimeBridge : IAudioRuntimeBridge
{
    private readonly LoggingService _log = LoggingService.Instance;

    private AudioRecord? _audioRecord;
    private AudioTrack? _audioTrack;
    private CancellationTokenSource? _micCts;

    public bool IsAvailable => true;
    public bool CanCaptureMicrophone => true;
    public bool CanCaptureSystemAudio => false;

    public bool StartMicrophoneCapture(Action<short[]> onPcmFrame, int sampleRate, int channels)
    {
        StopMicrophoneCapture();

        var channelConfig = channels == 1 ? ChannelIn.Mono : ChannelIn.Stereo;
        int minBuffer = AudioRecord.GetMinBufferSize(sampleRate, channelConfig, Encoding.Pcm16bit);
        if (minBuffer <= 0)
        {
            _log.Error("[Audio][Android] Invalid min buffer size for microphone.");
            return false;
        }

        _audioRecord = new AudioRecord(
            AudioSource.Mic,
            sampleRate,
            channelConfig,
            Encoding.Pcm16bit,
            Math.Max(minBuffer, sampleRate / 5));

        if (_audioRecord.State != State.Initialized)
        {
            _log.Error("[Audio][Android] AudioRecord initialization failed.");
            _audioRecord.Release();
            _audioRecord.Dispose();
            _audioRecord = null;
            return false;
        }

        _micCts = new CancellationTokenSource();
        var token = _micCts.Token;

        _audioRecord.StartRecording();

        Task.Run(() =>
        {
            short[] buffer = new short[960 * channels];
            while (!token.IsCancellationRequested)
            {
                int read = _audioRecord.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    short[] frame = new short[read];
                    Array.Copy(buffer, frame, read);
                    onPcmFrame(frame);
                }
            }
        }, token);

        _log.Info("[Audio][Android] Microphone capture started.");
        return true;
    }

    public void StopMicrophoneCapture()
    {
        if (_micCts != null)
        {
            _micCts.Cancel();
            _micCts.Dispose();
            _micCts = null;
        }

        if (_audioRecord != null)
        {
            try { _audioRecord.Stop(); } catch { }
            _audioRecord.Release();
            _audioRecord.Dispose();
            _audioRecord = null;
        }

        _log.Info("[Audio][Android] Microphone capture stopped.");
    }

    public bool StartPlayback(int sampleRate, int channels)
    {
        StopPlayback();

        var channelOut = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
        var channelConfig = channels == 1 ? ChannelConfiguration.Mono : ChannelConfiguration.Stereo;
        int minBuffer = AudioTrack.GetMinBufferSize(sampleRate, channelOut, Encoding.Pcm16bit);
        if (minBuffer <= 0)
        {
            _log.Error("[Audio][Android] Invalid min buffer size for playback.");
            return false;
        }

        _audioTrack = new AudioTrack(
            global::Android.Media.Stream.Music,
            sampleRate,
            channelConfig,
            Encoding.Pcm16bit,
            Math.Max(minBuffer, sampleRate / 2),
            AudioTrackMode.Stream);

        if (_audioTrack.State != AudioTrackState.Initialized)
        {
            _log.Error("[Audio][Android] AudioTrack initialization failed.");
            _audioTrack.Release();
            _audioTrack.Dispose();
            _audioTrack = null;
            return false;
        }

        _audioTrack.Play();
        _log.Info("[Audio][Android] Playback started.");
        return true;
    }

    public void PlayPcm(short[] samples, int sampleRate, int channels)
    {
        if (_audioTrack == null || samples.Length == 0) return;
        _audioTrack.Write(samples, 0, samples.Length);
    }

    public void StopPlayback()
    {
        if (_audioTrack != null)
        {
            try { _audioTrack.Stop(); } catch { }
            _audioTrack.Release();
            _audioTrack.Dispose();
            _audioTrack = null;
        }

        _log.Info("[Audio][Android] Playback stopped.");
    }
}
