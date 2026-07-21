using System.Runtime.InteropServices;
using NAudio.Wave.Asio;

namespace Jampanion.Live.Audio;

public enum AsioAudioBackend
{
    Automatic,
    Asio,
    WinMm
}

/// <summary>
/// User-selectable Windows ASIO settings. A buffer size of zero means the
/// driver's preferred size.
/// </summary>
public sealed record AsioAudioSettings(
    string? DriverName = null,
    int SampleRate = 48_000,
    int BufferSize = 0,
    int OutputChannelOffset = 0,
    AsioAudioBackend Backend = AsioAudioBackend.Automatic);

internal sealed record AsioOutputChannelInfo(int Offset, string DisplayName);

/// <summary>
/// Low-latency ASIO output for the FluidSynth renderer. The NAudio
/// AsioDriverExt API is used directly so the host can choose the sample rate
/// and the buffer size before ASIO buffers are created.
/// </summary>
internal sealed class AsioAudioOutput : IAudioOutput
{
    private static readonly int[] CommonSampleRates =
    {
        44_100, 48_000, 88_200, 96_000, 176_400, 192_000
    };

    private readonly AsioDriverExt _driver;
    private readonly Action<short[], int> _render;
    private readonly AsioFillBufferCallback _fillBufferCallback;
    private readonly AsioSampleType _leftType;
    private readonly AsioSampleType _rightType;
    private readonly int _framesPerBuffer;
    private readonly short[] _interleavedSamples;
    private readonly float[] _floatSamples;
    private readonly double[] _doubleSamples;
    private bool _started;
    private bool _disposed;

    private AsioAudioOutput(
        AsioDriverExt driver,
        Action<short[], int> render,
        int framesPerBuffer,
        AsioSampleType leftType,
        AsioSampleType rightType)
    {
        _driver = driver;
        _render = render;
        _framesPerBuffer = framesPerBuffer;
        _leftType = leftType;
        _rightType = rightType;
        _interleavedSamples = new short[framesPerBuffer * 2];
        _floatSamples = new float[framesPerBuffer];
        _doubleSamples = new double[framesPerBuffer];
        _fillBufferCallback = FillBuffer;
    }

    internal static bool IsSupported => OperatingSystem.IsWindows();

    internal static IReadOnlyList<string> GetDriverNames()
    {
        if (!IsSupported)
        {
            return Array.Empty<string>();
        }

        try
        {
            return AsioDriver.GetAsioDriverNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    internal static IReadOnlyList<int> GetSupportedSampleRates(string? driverName)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(driverName))
        {
            return CommonSampleRates;
        }

        return WithDriver(
            driverName,
            driver => CommonSampleRates.Where(rate => driver.IsSampleRateSupported(rate)).ToArray(),
            CommonSampleRates);
    }

    internal static IReadOnlyList<int> GetSupportedBufferSizes(string? driverName)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(driverName))
        {
            return new[] { 0 };
        }

        return WithDriver(driverName, driver => GetBufferSizeOptions(driver.Capabilities), new[] { 0 });
    }

    internal static IReadOnlyList<AsioOutputChannelInfo> GetOutputChannelOptions(string? driverName)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(driverName))
        {
            return new[] { new AsioOutputChannelInfo(0, "Outputs 1/2") };
        }

        return WithDriver(driverName, driver => GetOutputChannelOptions(driver.Capabilities),
            new[] { new AsioOutputChannelInfo(0, "Outputs 1/2") });
    }

    internal static AsioAudioOutput? TryCreate(
        Action<short[], int> render,
        AsioAudioSettings? requestedSettings = null)
    {
        if (!IsSupported)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(render);
        var settings = requestedSettings ?? new AsioAudioSettings();
        var requestedDriver = string.IsNullOrWhiteSpace(settings.DriverName)
            ? Environment.GetEnvironmentVariable("JAMPANION_ASIO_DRIVER")
            : settings.DriverName;
        var driverNames = string.IsNullOrWhiteSpace(requestedDriver)
            ? GetDriverNames()
            : new[] { requestedDriver };
        var sampleRate = NormalizeSampleRate(settings.SampleRate);

        foreach (var driverName in driverNames)
        {
            if (string.IsNullOrWhiteSpace(driverName))
            {
                continue;
            }

            AsioDriver? rawDriver = null;
            AsioDriverExt? driver = null;
            AsioAudioOutput? output = null;
            try
            {
                rawDriver = AsioDriver.GetAsioDriverByName(driverName);
                driver = new AsioDriverExt(rawDriver);
                rawDriver = null;

                if (!driver.IsSampleRateSupported(sampleRate))
                {
                    throw new InvalidOperationException(
                        $"ASIO driver '{driverName}' does not support {sampleRate} Hz.");
                }

                driver.SetSampleRate(sampleRate);
                var capabilities = driver.Capabilities;
                if (capabilities.NbOutputChannels < 2)
                {
                    throw new InvalidOperationException($"ASIO driver '{driverName}' has fewer than two output channels.");
                }

                var outputChannelOffset = NormalizeOutputChannelOffset(
                    capabilities,
                    settings.OutputChannelOffset);
                driver.SetChannelOffset(outputChannelOffset, inputChannelOffset: 0);
                var framesPerBuffer = NormalizeBufferSize(capabilities, settings.BufferSize);
                var leftType = capabilities.OutputChannelInfos[outputChannelOffset].type;
                var rightType = capabilities.OutputChannelInfos[outputChannelOffset + 1].type;
                if (!IsSupportedSampleType(leftType) || !IsSupportedSampleType(rightType))
                {
                    throw new InvalidOperationException(
                        $"ASIO driver '{driverName}' uses an unsupported sample format.");
                }

                // AsioDriverExt exposes mutable capabilities. Setting the
                // preferred size before CreateBuffers lets us request an exact
                // supported size while retaining NAudio's callback plumbing.
                capabilities.BufferPreferredSize = framesPerBuffer;
                output = new AsioAudioOutput(
                    driver,
                    render,
                    framesPerBuffer,
                    leftType,
                    rightType);
                driver.FillBufferCallback = output._fillBufferCallback;
                var actualFrames = driver.CreateBuffers(2, 0, useMaxBufferSize: false);
                if (actualFrames != framesPerBuffer)
                {
                    throw new InvalidOperationException(
                        $"ASIO driver '{driverName}' selected {actualFrames} frames instead of {framesPerBuffer}.");
                }

                return output;
            }
            catch
            {
                output?.Dispose();
                if (driver is not null && output is null)
                {
                    SafeRelease(driver);
                }
                else if (rawDriver is not null)
                {
                    SafeRelease(rawDriver);
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

        _driver.Start();
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
                    _driver.Stop();
                }
                catch
                {
                    // A driver may already have been reset or disconnected.
                }
            }
        }
        finally
        {
            SafeRelease(_driver);
        }

        GC.SuppressFinalize(this);
    }

    private void FillBuffer(IntPtr[] _, IntPtr[] outputChannels)
    {
        if (outputChannels.Length < 2 || outputChannels[0] == IntPtr.Zero || outputChannels[1] == IntPtr.Zero)
        {
            return;
        }

        _render(_interleavedSamples, _interleavedSamples.Length);
        WriteChannel(outputChannels[0], _leftType, channel: 0);
        WriteChannel(outputChannels[1], _rightType, channel: 1);
    }

    private void WriteChannel(IntPtr destination, AsioSampleType sampleType, int channel)
    {
        for (var frame = 0; frame < _framesPerBuffer; frame++)
        {
            var sample = _interleavedSamples[frame * 2 + channel];
            var offset = frame * BytesPerSample(sampleType);
            WriteSample(destination, offset, sample, sampleType);
        }
    }

    private void WriteSample(IntPtr destination, int offset, short sample, AsioSampleType sampleType)
    {
        switch (sampleType)
        {
            case AsioSampleType.Int16LSB:
                Marshal.WriteInt16(destination, offset, sample);
                break;
            case AsioSampleType.Int16MSB:
                WriteBigEndian16(destination, offset, sample);
                break;
            case AsioSampleType.Int24LSB:
                WriteLittleEndian24(destination, offset, sample << 8);
                break;
            case AsioSampleType.Int24MSB:
                WriteBigEndian24(destination, offset, sample << 8);
                break;
            case AsioSampleType.Int32LSB:
            case AsioSampleType.Int32LSB16:
                Marshal.WriteInt32(destination, offset, sample << 16);
                break;
            case AsioSampleType.Int32LSB24:
                Marshal.WriteInt32(destination, offset, sample << 8);
                break;
            case AsioSampleType.Int32MSB:
            case AsioSampleType.Int32MSB16:
                WriteBigEndian32(destination, offset, sample << 16);
                break;
            case AsioSampleType.Int32MSB24:
                WriteBigEndian32(destination, offset, sample << 8);
                break;
            case AsioSampleType.Float32LSB:
                _floatSamples[0] = sample / 32768f;
                Marshal.Copy(_floatSamples, 0, IntPtr.Add(destination, offset), 1);
                break;
            case AsioSampleType.Float32MSB:
                WriteBigEndian32(destination, offset, BitConverter.SingleToInt32Bits(sample / 32768f));
                break;
            case AsioSampleType.Float64LSB:
                _doubleSamples[0] = sample / 32768d;
                Marshal.Copy(_doubleSamples, 0, IntPtr.Add(destination, offset), 1);
                break;
            case AsioSampleType.Float64MSB:
                WriteBigEndian64(destination, offset, BitConverter.DoubleToInt64Bits(sample / 32768d));
                break;
            default:
                throw new InvalidOperationException($"Unsupported ASIO sample format: {sampleType}.");
        }
    }

    private static int BytesPerSample(AsioSampleType sampleType) => sampleType switch
    {
        AsioSampleType.Int16LSB or AsioSampleType.Int16MSB => 2,
        AsioSampleType.Int24LSB or AsioSampleType.Int24MSB => 3,
        AsioSampleType.Int32LSB or AsioSampleType.Int32MSB or
            AsioSampleType.Int32LSB16 or AsioSampleType.Int32LSB24 or
            AsioSampleType.Int32MSB16 or AsioSampleType.Int32MSB24 or
            AsioSampleType.Float32LSB or AsioSampleType.Float32MSB => 4,
        AsioSampleType.Float64LSB or AsioSampleType.Float64MSB => 8,
        _ => throw new InvalidOperationException($"Unsupported ASIO sample format: {sampleType}.")
    };

    private static bool IsSupportedSampleType(AsioSampleType sampleType)
    {
        try
        {
            _ = BytesPerSample(sampleType);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int NormalizeSampleRate(int sampleRate) =>
        sampleRate is >= 8_000 and <= 384_000 ? sampleRate : 48_000;

    private static int NormalizeOutputChannelOffset(AsioDriverCapability capabilities, int requested)
    {
        if (requested >= 0 && requested + 1 < capabilities.NbOutputChannels)
        {
            return requested;
        }

        return 0;
    }

    private static IReadOnlyList<AsioOutputChannelInfo> GetOutputChannelOptions(AsioDriverCapability capabilities)
    {
        var options = new List<AsioOutputChannelInfo>();
        var channelInfos = capabilities.OutputChannelInfos;
        for (var offset = 0; offset + 1 < capabilities.NbOutputChannels; offset += 2)
        {
            var leftName = channelInfos[offset].name;
            var rightName = channelInfos[offset + 1].name;
            var leftLabel = string.IsNullOrWhiteSpace(leftName) ? $"Output {offset + 1}" : leftName;
            var rightLabel = string.IsNullOrWhiteSpace(rightName) ? $"Output {offset + 2}" : rightName;
            options.Add(new AsioOutputChannelInfo(
                offset,
                $"Outputs {offset + 1}/{offset + 2}: {leftLabel} / {rightLabel}"));
        }

        return options.Count > 0
            ? options
            : new[] { new AsioOutputChannelInfo(0, "Outputs 1/2") };
    }

    private static int NormalizeBufferSize(AsioDriverCapability capabilities, int requested)
    {
        var preferred = capabilities.BufferPreferredSize > 0
            ? capabilities.BufferPreferredSize
            : capabilities.BufferMinSize;
        if (requested <= 0 || !IsBufferSizeSupported(capabilities, requested))
        {
            return preferred > 0 ? preferred : 512;
        }

        return requested;
    }

    private static bool IsBufferSizeSupported(AsioDriverCapability capabilities, int size)
    {
        if (size < capabilities.BufferMinSize || size > capabilities.BufferMaxSize)
        {
            return false;
        }

        return capabilities.BufferGranularity switch
        {
            -1 => size > 0 && (size & (size - 1)) == 0,
            > 0 => (size - capabilities.BufferMinSize) % capabilities.BufferGranularity == 0,
            _ => true
        };
    }

    private static IReadOnlyList<int> GetBufferSizeOptions(AsioDriverCapability capabilities)
    {
        var options = new List<int> { 0 };
        if (capabilities.BufferMinSize <= 0 || capabilities.BufferMaxSize < capabilities.BufferMinSize)
        {
            return options;
        }

        if (capabilities.BufferGranularity == -1)
        {
            for (var size = 1; size <= capabilities.BufferMaxSize; size <<= 1)
            {
                if (size >= capabilities.BufferMinSize)
                {
                    options.Add(size);
                }

                if (size > int.MaxValue / 2)
                {
                    break;
                }
            }
        }
        else if (capabilities.BufferGranularity > 0 &&
                 (capabilities.BufferMaxSize - capabilities.BufferMinSize) / capabilities.BufferGranularity <= 64)
        {
            for (var size = capabilities.BufferMinSize;
                 size <= capabilities.BufferMaxSize;
                 size += capabilities.BufferGranularity)
            {
                options.Add(size);
            }
        }
        else
        {
            options.Add(capabilities.BufferMinSize);
            options.Add(capabilities.BufferPreferredSize);
            options.Add(capabilities.BufferMaxSize);
        }

        return options.Where(size => size == 0 || IsBufferSizeSupported(capabilities, size))
            .Distinct()
            .OrderBy(size => size == 0 ? int.MinValue : size)
            .ToArray();
    }

    private static T WithDriver<T>(string driverName, Func<AsioDriverExt, T> action, T fallback)
    {
        AsioDriver? rawDriver = null;
        AsioDriverExt? driver = null;
        try
        {
            rawDriver = AsioDriver.GetAsioDriverByName(driverName);
            driver = new AsioDriverExt(rawDriver);
            rawDriver = null;
            return action(driver);
        }
        catch
        {
            return fallback;
        }
        finally
        {
            if (driver is not null)
            {
                SafeRelease(driver);
            }
            else if (rawDriver is not null)
            {
                SafeRelease(rawDriver);
            }
        }
    }

    private static void SafeRelease(AsioDriverExt driver)
    {
        try
        {
            driver.ReleaseDriver();
        }
        catch
        {
            // Device enumeration and disposal must never terminate playback.
        }
    }

    private static void SafeRelease(AsioDriver driver)
    {
        try
        {
            driver.ReleaseComAsioDriver();
        }
        catch
        {
        }
    }

    private static void WriteBigEndian16(IntPtr destination, int offset, short value)
    {
        Marshal.WriteByte(destination, offset, (byte)(value >> 8));
        Marshal.WriteByte(destination, offset + 1, (byte)value);
    }

    private static void WriteLittleEndian24(IntPtr destination, int offset, int value)
    {
        Marshal.WriteByte(destination, offset, (byte)value);
        Marshal.WriteByte(destination, offset + 1, (byte)(value >> 8));
        Marshal.WriteByte(destination, offset + 2, (byte)(value >> 16));
    }

    private static void WriteBigEndian24(IntPtr destination, int offset, int value)
    {
        Marshal.WriteByte(destination, offset, (byte)(value >> 16));
        Marshal.WriteByte(destination, offset + 1, (byte)(value >> 8));
        Marshal.WriteByte(destination, offset + 2, (byte)value);
    }

    private static void WriteBigEndian32(IntPtr destination, int offset, int value)
    {
        Marshal.WriteByte(destination, offset, (byte)(value >> 24));
        Marshal.WriteByte(destination, offset + 1, (byte)(value >> 16));
        Marshal.WriteByte(destination, offset + 2, (byte)(value >> 8));
        Marshal.WriteByte(destination, offset + 3, (byte)value);
    }

    private static void WriteBigEndian64(IntPtr destination, int offset, long value)
    {
        for (var i = 0; i < 8; i++)
        {
            Marshal.WriteByte(destination, offset + i, (byte)(value >> (56 - i * 8)));
        }
    }
}
