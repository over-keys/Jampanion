using System.Runtime.InteropServices;

namespace Jampanion.Live.Audio;

/// <summary>
/// Minimal CoreAudio AudioQueue output for the built-in synth on macOS.
/// </summary>
internal sealed class MacAudioQueueOutput : IAudioOutput
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const int FramesPerBuffer = 512;
    private const int BufferCount = 4;
    private const uint AudioFormatLinearPcm = 0x6C70636D;
    private const uint AudioFormatFlagIsSignedInteger = 1 << 2;
    private const uint AudioFormatFlagIsPacked = 1 << 3;

    private readonly Action<short[], int> _render;
    private readonly AudioQueueOutputCallback _callback;
    private readonly Dictionary<IntPtr, short[]> _bufferSamples = new();
    private GCHandle _selfHandle;
    private IntPtr _queue;
    private bool _running;
    private bool _disposed;

    public MacAudioQueueOutput(Action<short[], int> render)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _callback = OnAudioQueueBufferReady;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        var format = AudioStreamBasicDescription.CreatePcm();
        _selfHandle = GCHandle.Alloc(this);

        try
        {
            Check(AudioQueueNewOutput(
                ref format,
                _callback,
                GCHandle.ToIntPtr(_selfHandle),
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                out _queue), "create macOS audio queue");

            _running = true;
            var bufferByteSize = (uint)(FramesPerBuffer * Channels * sizeof(short));
            for (var i = 0; i < BufferCount; i++)
            {
                Check(AudioQueueAllocateBuffer(_queue, bufferByteSize, out var buffer), "allocate macOS audio buffer");
                _bufferSamples[buffer] = new short[FramesPerBuffer * Channels];
                FillAndEnqueue(buffer);
            }

            Check(AudioQueueStart(_queue, IntPtr.Zero), "start macOS audio queue");
        }
        catch
        {
            StopCore(immediate: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCore(immediate: true);
        GC.SuppressFinalize(this);
    }

    private void OnBufferReady(IntPtr buffer)
    {
        if (!_running || _disposed)
        {
            return;
        }

        try
        {
            FillAndEnqueue(buffer);
        }
        catch
        {
            _running = false;
        }
    }

    private void FillAndEnqueue(IntPtr buffer)
    {
        if (!_bufferSamples.TryGetValue(buffer, out var samples))
        {
            return;
        }

        _render(samples, samples.Length);
        var queueBuffer = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
        Marshal.Copy(samples, 0, queueBuffer.AudioData, samples.Length);
        queueBuffer.AudioDataByteSize = (uint)(samples.Length * sizeof(short));
        Marshal.StructureToPtr(queueBuffer, buffer, fDeleteOld: false);
        Check(AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero), "enqueue macOS audio buffer");
    }

    private void StopCore(bool immediate)
    {
        _running = false;

        if (_queue != IntPtr.Zero)
        {
            AudioQueueStop(_queue, immediate);
            foreach (var buffer in _bufferSamples.Keys.ToArray())
            {
                AudioQueueFreeBuffer(_queue, buffer);
            }

            _bufferSamples.Clear();
            AudioQueueDispose(_queue, immediate);
            _queue = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private static void OnAudioQueueBufferReady(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is MacAudioQueueOutput output)
        {
            output.OnBufferReady(buffer);
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"Could not {operation}; CoreAudio status {status}.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        AudioQueueOutputCallback callback,
        IntPtr userData,
        IntPtr callbackRunLoop,
        IntPtr callbackRunLoopMode,
        uint flags,
        out IntPtr queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptionCount, IntPtr packetDescriptions);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueFreeBuffer(IntPtr queue, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;

        public static AudioStreamBasicDescription CreatePcm()
        {
            var bytesPerFrame = (uint)(Channels * BitsPerSample / 8);
            return new AudioStreamBasicDescription
            {
                SampleRate = MacAudioQueueOutput.SampleRate,
                FormatId = AudioFormatLinearPcm,
                FormatFlags = AudioFormatFlagIsSignedInteger | AudioFormatFlagIsPacked,
                BytesPerPacket = bytesPerFrame,
                FramesPerPacket = 1,
                BytesPerFrame = bytesPerFrame,
                ChannelsPerFrame = Channels,
                BitsPerChannel = BitsPerSample
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }
}
