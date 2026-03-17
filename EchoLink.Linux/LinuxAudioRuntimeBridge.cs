using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Services;

namespace EchoLink.Linux;

public class LinuxAudioRuntimeBridge : IAudioRuntimeBridge
{
    private readonly LoggingService _log = LoggingService.Instance;

    private IntPtr _playbackStream = IntPtr.Zero;
    private IntPtr _captureStream = IntPtr.Zero;
    private IntPtr _systemCaptureStream = IntPtr.Zero;

    private CancellationTokenSource? _playbackCts;
    private CancellationTokenSource? _captureCts;
    private CancellationTokenSource? _systemCaptureCts;

    private Task? _playbackTask;
    private Task? _captureTask;
    private Task? _systemCaptureTask;

    private readonly ConcurrentQueue<short[]> _playbackBuffer = new();
    private readonly object _playbackBufferLock = new();
    private readonly object _streamLifecycleLock = new();
    private const int MaxJitterBufferFrames = 5; 

    private static bool _paInitialized = false;
    private static readonly object _paInitLock = new();

    private int _actualPlaybackChannels = 1;

    public bool IsAvailable
    {
        get
        {
            if (!_paInitialized)
            {
                lock (_paInitLock)
                {
                    if (!_paInitialized)
                    {
                        try
                        {
                            int err = Pa_Initialize();
                            if (err == 0)
                            {
                                _paInitialized = true;
                                _log.Info("[LinuxAudio] PortAudio Initialized Successfully.");
                                LogHostApis();
                                LogAllDevices();
                            }
                            else
                            {
                                _log.Error($"[LinuxAudio] Pa_Initialize failed with error code: {err}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"[LinuxAudio] Pa_Initialize exception: {ex.Message}");
                        }
                    }
                }
            }
            return _paInitialized;
        }
    }

    private void LogHostApis()
    {
        int count = Pa_GetHostApiCount();
        _log.Info($"[LinuxAudio] PortAudio found {count} Host APIs:");
        for (int i = 0; i < count; i++)
        {
            IntPtr infoPtr = Pa_GetHostApiInfo(i);
            if (infoPtr == IntPtr.Zero) continue;
            PaHostApiInfo info = Marshal.PtrToStructure<PaHostApiInfo>(infoPtr);
            _log.Info($"  Host API {i}: {info.name} (Type: {info.type}, Devices: {info.deviceCount}, Default Device: {info.defaultOutputDevice})");
        }
    }

    private void LogAllDevices()
    {
        int count = Pa_GetDeviceCount();
        _log.Info($"[LinuxAudio] PortAudio sees {count} total devices:");
        for (int i = 0; i < count; i++)
        {
            IntPtr infoPtr = Pa_GetDeviceInfo(i);
            if (infoPtr == IntPtr.Zero) continue;
            PaDeviceInfo info = Marshal.PtrToStructure<PaDeviceInfo>(infoPtr);
            string name = Marshal.PtrToStringAnsi(info.name) ?? "unknown";
            _log.Info($"  Device {i}: {name} (Host API: {info.hostApi}, In: {info.maxInputChannels}, Out: {info.maxOutputChannels}, Default Rate: {info.defaultSampleRate})");
        }
    }

    public bool CanCaptureMicrophone => true;
    public bool CanCaptureSystemAudio => true; 

    public LinuxAudioRuntimeBridge()
    {
    }

    ~LinuxAudioRuntimeBridge()
    {
        // NEVER terminate PortAudio in a finalizer. It's too dangerous for shared PulseAudio contexts.
    }

    public bool StartMicrophoneCapture(Action<short[]> onPcmFrame, int sampleRate, int channels)
    {
        if (!IsAvailable) return false;
        
        lock (_streamLifecycleLock)
        {
            StopMicrophoneCaptureInternal();

            // Try to find the dedicated mic device
            int deviceIndex = FindDevice(true, "EchoLink_Virtual_Mic");
            
            // If not found, try PulseAudio bridge
            if (deviceIndex < 0) deviceIndex = FindDevice(true, "pulse");
            
            // If still not found, use default
            if (deviceIndex < 0) deviceIndex = Pa_GetDefaultInputDevice();

            if (deviceIndex < 0)
            {
                _log.Error("[LinuxAudio] No microphone capture device found even with fallbacks.");
                return false;
            }

            int captureChannels = 2; 
            PaStreamParameters inputParams = new PaStreamParameters
            {
                device = deviceIndex,
                channelCount = captureChannels,
                sampleFormat = (IntPtr)0x00000008, // paInt16
                suggestedLatency = 0.05,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            _log.Info($"[LinuxAudio] Opening Mic Capture on device {deviceIndex} in Stereo.");

            int err = Pa_OpenStream(out _captureStream, ref inputParams, IntPtr.Zero, sampleRate, 0, 0, IntPtr.Zero, IntPtr.Zero);
            if (err != 0 || _captureStream == IntPtr.Zero)
            {
                _log.Error($"[LinuxAudio] Pa_OpenStream (Mic) failed: {err}");
                return false;
            }

            err = Pa_StartStream(_captureStream);
            if (err != 0)
            {
                _log.Error($"[LinuxAudio] Pa_StartStream (Mic) failed: {err}");
                Pa_CloseStream(_captureStream);
                _captureStream = IntPtr.Zero;
                return false;
            }

            _captureCts = new CancellationTokenSource();
            var token = _captureCts.Token;
            _captureTask = Task.Run(() => CaptureLoop(onPcmFrame, sampleRate, channels, token, () => _captureStream, captureChannels, "MicCapture"));
            return true;
        }
    }

    public void StopMicrophoneCapture()
    {
        lock (_streamLifecycleLock)
        {
            StopMicrophoneCaptureInternal();
        }
    }

    private void StopMicrophoneCaptureInternal()
    {
        _captureCts?.Cancel();
        if (_captureTask != null)
        {
            _captureTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;

        if (_captureStream != IntPtr.Zero)
        {
            Pa_StopStream(_captureStream);
            Pa_CloseStream(_captureStream);
            _captureStream = IntPtr.Zero;
        }
    }

    public bool StartSystemAudioCapture(Action<short[]> onPcmFrame, int sampleRate, int channels)
    {
        if (!IsAvailable) return false;
        
        lock (_streamLifecycleLock)
        {
            StopSystemAudioCaptureInternal();

            // For Loopback, we want the monitor of our virtual sink
            int deviceIndex = FindDevice(true, "EchoLink_Virtual_Sink.monitor");
            if (deviceIndex < 0) deviceIndex = FindDevice(true, "EchoLink_Virtual_Output.monitor");
            if (deviceIndex < 0) deviceIndex = FindDevice(true, "EchoLink_Virtual");
            
            // Fallback to PulseAudio
            if (deviceIndex < 0) deviceIndex = FindDevice(true, "pulse");
            
            // Default
            if (deviceIndex < 0) deviceIndex = Pa_GetDefaultInputDevice();

            if (deviceIndex < 0)
            {
                _log.Error("[LinuxAudio] No system capture device found.");
                return false;
            }

            int captureChannels = 2; 
            PaStreamParameters inputParams = new PaStreamParameters
            {
                device = deviceIndex,
                channelCount = captureChannels,
                sampleFormat = (IntPtr)0x00000008, // paInt16
                suggestedLatency = 0.06,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            _log.Info($"[LinuxAudio] Opening System Capture on device {deviceIndex} in Stereo.");

            int err = Pa_OpenStream(out _systemCaptureStream, ref inputParams, IntPtr.Zero, sampleRate, 0, 0, IntPtr.Zero, IntPtr.Zero);
            if (err != 0 || _systemCaptureStream == IntPtr.Zero)
            {
                _log.Error($"[LinuxAudio] Pa_OpenStream (System) failed: {err}");
                return false;
            }

            err = Pa_StartStream(_systemCaptureStream);
            if (err != 0)
            {
                _log.Error($"[LinuxAudio] Pa_StartStream (System) failed: {err}");
                Pa_CloseStream(_systemCaptureStream);
                _systemCaptureStream = IntPtr.Zero;
                return false;
            }

            _systemCaptureCts = new CancellationTokenSource();
            var token = _systemCaptureCts.Token;
            _systemCaptureTask = Task.Run(() => CaptureLoop(onPcmFrame, sampleRate, channels, token, () => _systemCaptureStream, captureChannels, "SystemCapture"));
            return true;
        }
    }

    public void StopSystemAudioCapture()
    {
        lock (_streamLifecycleLock)
        {
            StopSystemAudioCaptureInternal();
        }
    }

    private void StopSystemAudioCaptureInternal()
    {
        _systemCaptureCts?.Cancel();
        if (_systemCaptureTask != null)
        {
            _systemCaptureTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        _systemCaptureCts?.Dispose();
        _systemCaptureCts = null;
        _systemCaptureTask = null;

        if (_systemCaptureStream != IntPtr.Zero)
        {
            Pa_StopStream(_systemCaptureStream);
            Pa_CloseStream(_systemCaptureStream);
            _systemCaptureStream = IntPtr.Zero;
        }
    }

    private DateTime _lastOverflowLog = DateTime.MinValue;

    private void CaptureLoop(Action<short[]> onPcmFrame, int sampleRate, int requestedChannels, CancellationToken ct, Func<IntPtr> getStreamPtr, int actualChannels, string label)
    {
        ulong frames = 960;
        short[] captureBuffer = new short[frames * (ulong)actualChannels];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                IntPtr streamPtr = getStreamPtr();
                if (streamPtr == IntPtr.Zero) break;

                int err = Pa_ReadStream(streamPtr, captureBuffer, frames);
                
                if (err == 0 || err == -9981) 
                {
                    if (err == -9981 && (DateTime.Now - _lastOverflowLog).TotalSeconds > 5)
                    {
                        _log.Warning($"[LinuxAudio] [{label}] Buffer overflowed.");
                        _lastOverflowLog = DateTime.Now;
                    }

                    if (actualChannels == 2 && requestedChannels == 1)
                    {
                        short[] mono = new short[frames];
                        for (int i = 0; i < (int)frames; i++)
                        {
                            mono[i] = (short)((captureBuffer[i * 2] + captureBuffer[i * 2 + 1]) / 2);
                        }
                        onPcmFrame(mono);
                    }
                    else
                    {
                        short[] copy = new short[captureBuffer.Length];
                        Array.Copy(captureBuffer, copy, captureBuffer.Length);
                        onPcmFrame(copy);
                    }
                }
                else
                {
                    if (!ct.IsCancellationRequested)
                    {
                        _log.Error($"[LinuxAudio] [{label}] Read fatal error: {err}.");
                    }
                    break; 
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) _log.Error($"[LinuxAudio] [{label}] Exception: {ex.Message}");
                break;
            }
        }
    }

    public bool StartPlayback(int sampleRate, int requestedChannels)
    {
        if (!IsAvailable) return false;
        
        lock (_streamLifecycleLock)
        {
            StopPlaybackInternal();

            // We want to play to the virtual sink so it becomes a mic
            int deviceIndex = FindDevice(false, "EchoLink_Virtual_Sink");
            if (deviceIndex < 0) deviceIndex = FindDevice(false, "EchoLink_Virtual_Output");
            if (deviceIndex < 0) deviceIndex = FindDevice(false, "EchoLink_Virtual");
            
            // Fallback to Pulse
            if (deviceIndex < 0) deviceIndex = FindDevice(false, "pulse");
            
            // Default
            if (deviceIndex < 0) deviceIndex = Pa_GetDefaultOutputDevice();

            if (deviceIndex < 0)
            {
                _log.Error("[LinuxAudio] No playback device found.");
                return false;
            }

            _actualPlaybackChannels = requestedChannels;
            IntPtr infoPtr = Pa_GetDeviceInfo(deviceIndex);
            if (infoPtr != IntPtr.Zero)
            {
                PaDeviceInfo info = Marshal.PtrToStructure<PaDeviceInfo>(infoPtr);
                string name = Marshal.PtrToStringAnsi(info.name) ?? "";
                if (name.Contains("EchoLink_Virtual", StringComparison.OrdinalIgnoreCase))
                {
                    _actualPlaybackChannels = 2; // Sink is Stereo
                }
            }

            _log.Info($"[LinuxAudio] Opening Playback on device {deviceIndex} in {_actualPlaybackChannels} channels.");

            PaStreamParameters outputParams = new PaStreamParameters
            {
                device = deviceIndex,
                channelCount = _actualPlaybackChannels,
                sampleFormat = (IntPtr)0x00000008, // paInt16
                suggestedLatency = 0.08,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            int err = Pa_OpenStream(out _playbackStream, IntPtr.Zero, ref outputParams, sampleRate, 0, 0, IntPtr.Zero, IntPtr.Zero);
            if (err != 0 || _playbackStream == IntPtr.Zero)
            {
                _log.Error($"[LinuxAudio] Pa_OpenStream (Playback) failed: {err}");
                return false;
            }

            err = Pa_StartStream(_playbackStream);
            if (err != 0)
            {
                _log.Error($"[LinuxAudio] Pa_StartStream (Playback) failed: {err}");
                Pa_CloseStream(_playbackStream);
                _playbackStream = IntPtr.Zero;
                return false;
            }

            _playbackBuffer.Clear();
            _playbackCts = new CancellationTokenSource();
            var token = _playbackCts.Token;
            _playbackTask = Task.Run(() => PlaybackLoop(token, _actualPlaybackChannels, () => _playbackStream));

            return true;
        }
    }

    public void PlayPcm(short[] samples, int sampleRate, int incomingChannels)
    {
        if (_playbackStream == IntPtr.Zero || samples.Length == 0) return;

        lock (_playbackBufferLock)
        {
            while (_playbackBuffer.Count >= MaxJitterBufferFrames)
            {
                _playbackBuffer.TryDequeue(out _);
            }

            if (incomingChannels == 1 && _actualPlaybackChannels == 2)
            {
                short[] stereo = new short[samples.Length * 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    stereo[i * 2] = samples[i];     
                    stereo[i * 2 + 1] = samples[i]; 
                }
                _playbackBuffer.Enqueue(stereo);
            }
            else
            {
                _playbackBuffer.Enqueue(samples);
            }
        }
    }

    private void PlaybackLoop(CancellationToken ct, int channels, Func<IntPtr> getStreamPtr)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IntPtr streamPtr = getStreamPtr();
                if (streamPtr == IntPtr.Zero) break;

                if (_playbackBuffer.TryDequeue(out short[]? samples))
                {
                    ulong frames = (ulong)(samples.Length / channels);
                    int err = Pa_WriteStream(streamPtr, samples, frames);
                    if (err != 0 && !ct.IsCancellationRequested)
                    {
                        _log.Warning($"[LinuxAudio] Playback Write Error: {err}");
                    }
                }
                else
                {
                    Thread.Sleep(5); 
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) _log.Error($"[LinuxAudio] Playback exception: {ex.Message}");
                break;
            }
        }
    }

    public void StopPlayback()
    {
        lock (_streamLifecycleLock)
        {
            StopPlaybackInternal();
        }
    }

    private void StopPlaybackInternal()
    {
        _playbackCts?.Cancel();
        if (_playbackTask != null)
        {
            _playbackTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        _playbackCts?.Dispose();
        _playbackCts = null;
        _playbackTask = null;

        if (_playbackStream != IntPtr.Zero)
        {
            Pa_StopStream(_playbackStream);
            Pa_CloseStream(_playbackStream);
            _playbackStream = IntPtr.Zero;
        }

        _playbackBuffer.Clear();
    }

    private int FindDevice(bool input, string targetSubstring)
    {
        int count = Pa_GetDeviceCount();
        if (count < 0) return -1;

        for (int i = 0; i < count; i++)
        {
            IntPtr infoPtr = Pa_GetDeviceInfo(i);
            if (infoPtr == IntPtr.Zero) continue;

            PaDeviceInfo info = Marshal.PtrToStructure<PaDeviceInfo>(infoPtr);
            if (input && info.maxInputChannels == 0) continue;
            if (!input && info.maxOutputChannels == 0) continue;

            string name = Marshal.PtrToStringAnsi(info.name) ?? "";
            if (name.Contains(targetSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaStreamParameters
    {
        public int device;
        public int channelCount;
        public IntPtr sampleFormat; // paInt16 = 0x00000008
        public double suggestedLatency;
        public IntPtr hostApiSpecificStreamInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaDeviceInfo
    {
        public int structVersion;
        public IntPtr name;
        public int hostApi;
        public int maxInputChannels;
        public int maxOutputChannels;
        public double defaultLowInputLatency;
        public double defaultLowOutputLatency;
        public double defaultHighInputLatency;
        public double defaultHighOutputLatency;
        public double defaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaHostApiInfo
    {
        public int structVersion;
        public int type;
        public IntPtr name;
        public int deviceCount;
        public int defaultInputDevice;
        public int defaultOutputDevice;
    }

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_Initialize();

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_GetHostApiCount();

    [DllImport("libportaudio.so.2")]
    public static extern IntPtr Pa_GetHostApiInfo(int hostApi);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_GetDeviceCount();

    [DllImport("libportaudio.so.2")]
    public static extern IntPtr Pa_GetDeviceInfo(int device);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_GetDefaultInputDevice();

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_GetDefaultOutputDevice();

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_OpenStream(out IntPtr stream, ref PaStreamParameters inputParameters, IntPtr outputParameters, double sampleRate, ulong framesPerBuffer, uint streamFlags, IntPtr streamCallback, IntPtr userData);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_OpenStream(out IntPtr stream, IntPtr inputParameters, ref PaStreamParameters outputParameters, double sampleRate, ulong framesPerBuffer, uint streamFlags, IntPtr streamCallback, IntPtr userData);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_StartStream(IntPtr stream);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_StopStream(IntPtr stream);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_CloseStream(IntPtr stream);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_WriteStream(IntPtr stream, short[] buffer, ulong frames);

    [DllImport("libportaudio.so.2")]
    public static extern int Pa_ReadStream(IntPtr stream, short[] buffer, ulong frames);
}
