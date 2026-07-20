using NAudio.Wave;

namespace Jampanion.Live.Audio;

/// <summary>
/// Low-latency ASIO output for the FluidSynth renderer.  The ASIO driver is
/// supplied by the user's audio interface; Jampanion does not redistribute a
/// proprietary driver.
/// </summary>
internal sealed class AsioAudioOutput : IAudioOutput
{
    private readonly AsioOut _asio;
    private bool _started;
    private bool _disposed;

    private AsioAudioOutput(AsioOut asio)
    {
        _asio = asio;
    }

    internal static AsioAudioOutput? TryCreate(Action<short[], int> render)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(render);

        string[] driverNames;
        try
        {
            var requested = Environment.GetEnvironmentVariable("JAMPANION_ASIO_DRIVER");
            driverNames = string.IsNullOrWhiteSpace(requested)
                ? AsioOut.GetDriverNames()
                : new[] { requested };
        }
        catch
        {
            return null;
        }

        foreach (var driverName in driverNames)
        {
            if (string.IsNullOrWhiteSpace(driverName))
            {
                continue;
            }

            AsioOut? asio = null;
            try
            {
                asio = new AsioOut(driverName);
                if (!asio.IsSampleRateSupported(48_000))
                {
                    throw new InvalidOperationException($"ASIO driver '{driverName}' does not support 48 kHz.");
                }

                asio.Init(new RenderWaveProvider(render));
                return new AsioAudioOutput(asio);
            }
            catch
            {
                try
                {
                    asio?.Dispose();
                }
                catch
                {
                    // Continue trying another driver or the WinMM fallback.
                }
            }
        }

        return null;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _asio.Play();
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_started)
            {
                try
                {
                    _asio.Stop();
                }
                catch
                {
                    // A driver may already have been reset or disconnected.
                }
            }
        }
        finally
        {
            try
            {
                _asio.Dispose();
            }
            catch
            {
                // Disposal must not prevent the MIDI output router from closing.
            }
        }

        GC.SuppressFinalize(this);
    }

    private sealed class RenderWaveProvider : IWaveProvider
    {
        private readonly Action<short[], int> _render;
        private short[] _samples = new short[2_048];

        public RenderWaveProvider(Action<short[], int> render)
        {
            _render = render;
            WaveFormat = new WaveFormat(48_000, 16, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            if ((count & 1) != 0)
            {
                count--;
            }

            var sampleCount = count / sizeof(short);
            if (sampleCount == 0)
            {
                return 0;
            }

            if (_samples.Length < sampleCount)
            {
                _samples = new short[Math.Max(sampleCount, _samples.Length * 2)];
            }

            _render(_samples, sampleCount);
            Buffer.BlockCopy(_samples, 0, buffer, offset, count);
            return count;
        }
    }
}
