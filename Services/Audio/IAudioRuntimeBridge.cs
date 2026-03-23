using System;

namespace EchoLink.Services;

public interface IAudioRuntimeBridge
{
    bool IsAvailable { get; }
    bool CanCaptureMicrophone { get; }
    bool CanCaptureSystemAudio { get; }

    bool StartMicrophoneCapture(Action<short[]> onPcmFrame, int sampleRate, int channels);
    void StopMicrophoneCapture();

    bool StartSystemAudioCapture(Action<short[]> onPcmFrame, int sampleRate, int channels);
    void StopSystemAudioCapture();

    bool StartPlayback(int sampleRate, int channels);
    void PlayPcm(short[] samples, int sampleRate, int channels);
    void StopPlayback();
}
