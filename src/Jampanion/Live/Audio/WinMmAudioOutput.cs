using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jampanion.Live.Audio;

/// <summary>
/// Minimal stereo PCM output using the Windows waveOut API. Keeping this small
/// avoids an additional audio framework and gives the built-in preview synth a
/// fixed, predictable output path through the Windows default audio device.
/// </summary>
internal sealed class WinMmAudioOutput : IAudioOutput
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrDone = 0x00000001;
    private const int MmSysErrNoError = 0;
    private const int WaveErrStillPlaying = 33;
    // Four 512-frame buffers provide about 43 ms of queued coverage while
    // preserving a 10.7 ms render quantum. Combined with MMCSS scheduling and
    // the allocation-free render path this is a better latency/stability trade-
    // off than merely making each individual buffer very large.
    private const int BufferCount = 4;
    private const int FramesPerBuffer = 512;
    private const int BufferWaitTimeoutMilliseconds = 2;

    private readonly Action<short[], int> _render;
    private readonly EventWaitHandle _bufferDoneEvent = new(false, EventResetMode.AutoReset);
    private readonly List<AudioBuffer> _buffers = new(BufferCount);
    private Thread? _renderThread;
    private IntPtr _waveOut;
    private volatile bool _running;
    private bool _disposed;

    public WinMmAudioOutput(Action<short[], int> render)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        var format = WaveFormatEx.CreatePcm(sampleRate: 48_000, channels: 2, bitsPerSample: 16);
        var eventHandle = GetEventHandle(_bufferDoneEvent);
        Check(waveOutOpen(out _waveOut, WaveMapper, ref format, eventHandle, IntPtr.Zero, CallbackEvent), "open audio output");

        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                var buffer = new AudioBuffer(FramesPerBuffer * format.Channels);
                _buffers.Add(buffer);
                Check(waveOutPrepareHeader(_waveOut, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()), "prepare audio buffer");
                FillAndWrite(buffer);
            }

            _running = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "Jampanion Built-in Audio",
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();
        }
        catch
        {
            StopCore();
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
        StopCore();
        _bufferDoneEvent.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RenderLoop()
    {
        var mmcssHandle = AvSetMmThreadCharacteristics("Pro Audio", out _);
        try
        {
            while (_running)
            {
                // The event normally wakes us as soon as a buffer completes.
                // The short timeout also handles coalesced completion signals by
                // scanning every header and immediately replenishing all buffers.
                _bufferDoneEvent.WaitOne(BufferWaitTimeoutMilliseconds);
                if (!_running)
                {
                    break;
                }

                foreach (var buffer in _buffers)
                {
                    var header = Marshal.PtrToStructure<WaveHeader>(buffer.HeaderPointer);
                    if ((header.Flags & WhdrDone) != 0)
                    {
                        try
                        {
                            FillAndWrite(buffer);
                        }
                        catch
                        {
                            _running = false;
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            if (mmcssHandle != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(mmcssHandle);
            }
        }
    }

    private void FillAndWrite(AudioBuffer buffer)
    {
        _render(buffer.Samples, buffer.Samples.Length);
        Check(waveOutWrite(_waveOut, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()), "queue audio buffer");
    }

    private void StopCore()
    {
        _running = false;
        _bufferDoneEvent.Set();

        if (_waveOut != IntPtr.Zero)
        {
            waveOutReset(_waveOut);
        }

        if (_renderThread is not null && _renderThread != Thread.CurrentThread)
        {
            _renderThread.Join(1_000);
        }
        _renderThread = null;

        if (_waveOut != IntPtr.Zero)
        {
            foreach (var buffer in _buffers)
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var result = waveOutUnprepareHeader(_waveOut, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>());
                    if (result == MmSysErrNoError)
                    {
                        break;
                    }

                    if (result != WaveErrStillPlaying)
                    {
                        break;
                    }

                    Thread.Sleep(10);
                }

                buffer.Dispose();
            }

            _buffers.Clear();
            waveOutClose(_waveOut);
            _waveOut = IntPtr.Zero;
        }
    }

    private static IntPtr GetEventHandle(EventWaitHandle waitHandle)
    {
        SafeWaitHandle safeHandle = waitHandle.SafeWaitHandle;
        return safeHandle.DangerousGetHandle();
    }

    private static void Check(int result, string operation)
    {
        if (result != MmSysErrNoError)
        {
            throw new Win32Exception(result, $"Could not {operation}; waveOut error {result}.");
        }
    }

    private sealed class AudioBuffer : IDisposable
    {
        private readonly GCHandle _sampleHandle;

        public AudioBuffer(int sampleCount)
        {
            Samples = new short[sampleCount];
            _sampleHandle = GCHandle.Alloc(Samples, GCHandleType.Pinned);

            var header = new WaveHeader
            {
                Data = _sampleHandle.AddrOfPinnedObject(),
                BufferLength = (uint)(sampleCount * sizeof(short))
            };

            HeaderPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(header, HeaderPointer, fDeleteOld: false);
        }

        public short[] Samples { get; }
        public IntPtr HeaderPointer { get; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(HeaderPointer);
            if (_sampleHandle.IsAllocated)
            {
                _sampleHandle.Free();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public static WaveFormatEx CreatePcm(uint sampleRate, ushort channels, ushort bitsPerSample)
        {
            var blockAlign = (ushort)(channels * bitsPerSample / 8);
            return new WaveFormatEx
            {
                FormatTag = 1,
                Channels = channels,
                SamplesPerSecond = sampleRate,
                AverageBytesPerSecond = sampleRate * blockAlign,
                BlockAlign = blockAlign,
                BitsPerSample = bitsPerSample,
                ExtraSize = 0
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(
        out IntPtr waveOut,
        uint deviceId,
        ref WaveFormatEx format,
        IntPtr callback,
        IntPtr instance,
        uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr waveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr waveOut);
}
