using System.Buffers.Binary;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Sf2Subset <input.sf2> <output.sf2>");
    return 2;
}

var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var source = File.ReadAllBytes(input);
var subset = SoundFontSubset.Create(source);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, subset.Build());
Console.WriteLine($"Selected presets: {string.Join(", ", subset.SelectedPresetNames)}");
Console.WriteLine($"SoundFont: {source.Length:N0} -> {new FileInfo(output).Length:N0} bytes");
return 0;

internal sealed class SoundFontSubset
{
    private const ushort InstrumentOperator = 41;
    private const ushort SampleOperator = 53;
    private const int SampleGuardPoints = 46;

    private readonly byte[] _source;
    private readonly List<Preset> _presets;
    private readonly List<Bag> _presetBags;
    private readonly List<Modulator> _presetModulators;
    private readonly List<Generator> _presetGenerators;
    private readonly List<Instrument> _instruments;
    private readonly List<Bag> _instrumentBags;
    private readonly List<Modulator> _instrumentModulators;
    private readonly List<Generator> _instrumentGenerators;
    private readonly List<SampleHeader> _samples;
    private readonly byte[] _sampleData;
    private readonly List<string> _selectedPresetNames = new();

    private SoundFontSubset(byte[] source)
    {
        _source = source;
        var chunks = Riff.ReadTopLevel(source);
        var pdta = chunks.Single(chunk => chunk.IsList("pdta"));
        var sdta = chunks.Single(chunk => chunk.IsList("sdta"));
        var pdtaChunks = Riff.ReadListChildren(source, pdta);
        var sdtaChunks = Riff.ReadListChildren(source, sdta);

        _presets = ParsePresets(GetPayload(source, pdtaChunks, "phdr"));
        _presetBags = ParseBags(GetPayload(source, pdtaChunks, "pbag"));
        _presetModulators = ParseModulators(GetPayload(source, pdtaChunks, "pmod"));
        _presetGenerators = ParseGenerators(GetPayload(source, pdtaChunks, "pgen"));
        _instruments = ParseInstruments(GetPayload(source, pdtaChunks, "inst"));
        _instrumentBags = ParseBags(GetPayload(source, pdtaChunks, "ibag"));
        _instrumentModulators = ParseModulators(GetPayload(source, pdtaChunks, "imod"));
        _instrumentGenerators = ParseGenerators(GetPayload(source, pdtaChunks, "igen"));
        _samples = ParseSamples(GetPayload(source, pdtaChunks, "shdr"));
        _sampleData = GetPayload(source, sdtaChunks, "smpl");

        _originalPresets = _presets.Select(item => item.Clone()).ToList();
        _originalPresetGenerators = _presetGenerators.Select(item => item.Clone()).ToList();
        _originalPresetModulators = _presetModulators.Select(item => item.Clone()).ToList();

        ValidateTables();
    }

    public IReadOnlyList<string> SelectedPresetNames => _selectedPresetNames;

    public static SoundFontSubset Create(byte[] source)
    {
        if (source.Length < 12 || Text(source, 0, 4) != "RIFF" || Text(source, 8, 4) != "sfbk")
        {
            throw new InvalidDataException("The input is not an RIFF SoundFont (sfbk) file.");
        }

        var result = new SoundFontSubset(source);
        result.SelectDependencies();
        return result;
    }

    public byte[] Build()
    {
        var wanted = _presets
            .Select((preset, index) => (preset, index))
            .Where(item => item.index < _presets.Count - 1 && IsWanted(item.preset))
            .ToArray();
        if (wanted.Length == 0)
        {
            throw new InvalidDataException("The SoundFont contains none of the required GM presets.");
        }

        var selectedSamples = CollectSamples();
        var sampleMap = selectedSamples
            .Select((oldIndex, newIndex) => (oldIndex, newIndex))
            .ToDictionary(item => item.oldIndex, item => item.newIndex);
        var newSampleData = BuildSampleData(selectedSamples, sampleMap, out var sampleSegments);
        var newSamples = BuildSampleHeaders(selectedSamples, sampleMap, sampleSegments, newSampleData.Length / 2);

        RemapGenerators(sampleMap);

        var newPdta = new MemoryStream();
        WriteChunk(newPdta, "phdr", Encode(_presets));
        WriteChunk(newPdta, "pbag", Encode(_presetBags));
        WriteChunk(newPdta, "pmod", Encode(_presetModulators));
        WriteChunk(newPdta, "pgen", Encode(_presetGenerators));
        WriteChunk(newPdta, "inst", Encode(_instruments));
        WriteChunk(newPdta, "ibag", Encode(_instrumentBags));
        WriteChunk(newPdta, "imod", Encode(_instrumentModulators));
        WriteChunk(newPdta, "igen", Encode(_instrumentGenerators));
        WriteChunk(newPdta, "shdr", Encode(newSamples));

        var newSdta = new MemoryStream();
        newSdta.Write(Encoding.ASCII.GetBytes("sdta"));
        WriteChunk(newSdta, "smpl", newSampleData);

        var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        output.Write(new byte[4]);
        output.Write(Encoding.ASCII.GetBytes("sfbk"));

        foreach (var chunk in Riff.ReadTopLevel(_source))
        {
            if (chunk.IsList("pdta"))
            {
                WriteChunk(output, "LIST", newPdta.ToArray(), "pdta");
            }
            else if (chunk.IsList("sdta"))
            {
                WriteChunk(output, "LIST", newSdta.ToArray(), null);
            }
            else
            {
                output.Write(_source, chunk.RawOffset, chunk.RawLength);
            }
        }

        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        return bytes;
    }

    private void SelectDependencies()
    {
        var selectedPresets = _presets
            .Select((preset, index) => (preset, index))
            .Where(item => item.index < _presets.Count - 1 && IsWanted(item.preset))
            .ToArray();
        if (selectedPresets.Length == 0)
        {
            return;
        }

        var oldPresetBags = _presetBags.ToArray();
        var oldPresetGenerators = _presetGenerators.ToArray();
        var oldPresetModulators = _presetModulators.ToArray();
        var oldInstruments = _instruments.ToArray();
        var oldInstrumentBags = _instrumentBags.ToArray();
        var oldInstrumentGenerators = _instrumentGenerators.ToArray();
        var oldInstrumentModulators = _instrumentModulators.ToArray();

        _presets.Clear();
        _presetBags.Clear();
        _presetModulators.Clear();
        _presetGenerators.Clear();
        _instruments.Clear();
        _instrumentBags.Clear();
        _instrumentModulators.Clear();
        _instrumentGenerators.Clear();
        _selectedPresetNames.Clear();

        var instrumentMap = new Dictionary<int, int>();

        foreach (var (oldPreset, oldPresetIndex) in selectedPresets)
        {
            var newPreset = oldPreset.Clone();
            newPreset.BagIndex = CheckedUShort(_presetBags.Count, "preset bag");
            _presets.Add(newPreset);
            _selectedPresetNames.Add(oldPreset.Name);

            var firstBag = oldPreset.BagIndex;
            var lastBag = _presetsOriginalNextBag(oldPresetIndex);
            for (var oldBagIndex = firstBag; oldBagIndex < lastBag; oldBagIndex++)
            {
                var oldBag = oldPresetBags[oldBagIndex];
                var newBag = new Bag
                {
                    GeneratorIndex = CheckedUShort(_presetGenerators.Count, "preset generator"),
                    ModulatorIndex = CheckedUShort(_presetModulators.Count, "preset modulator")
                };

                var nextBag = oldPresetBags[oldBagIndex + 1];
                for (var generatorIndex = oldBag.GeneratorIndex; generatorIndex < nextBag.GeneratorIndex; generatorIndex++)
                {
                    var generator = oldPresetGenerators[generatorIndex].Clone();
                    if (generator.Operator == InstrumentOperator)
                    {
                        var oldInstrumentIndex = generator.Amount;
                        if (oldInstrumentIndex >= oldInstruments.Length - 1)
                        {
                            throw new InvalidDataException($"Preset '{oldPreset.Name}' references an invalid instrument index.");
                        }

                        generator.Amount = (ushort)GetOrAddInstrument(
                            oldInstrumentIndex,
                            instrumentMap,
                            oldInstruments,
                            oldInstrumentBags,
                            oldInstrumentGenerators,
                            oldInstrumentModulators);
                    }

                    _presetGenerators.Add(generator);
                }

                for (var modulatorIndex = oldBag.ModulatorIndex; modulatorIndex < nextBag.ModulatorIndex; modulatorIndex++)
                {
                    _presetModulators.Add(oldPresetModulators[modulatorIndex].Clone());
                }

                _presetBags.Add(newBag);
            }
        }

        var terminalPreset = _presetsOriginalTerminal().Clone();
        terminalPreset.BagIndex = CheckedUShort(_presetBags.Count, "preset terminal bag");
        _presets.Add(terminalPreset);
        _presetBags.Add(new Bag
        {
            GeneratorIndex = CheckedUShort(_presetGenerators.Count, "preset terminal generator"),
            ModulatorIndex = CheckedUShort(_presetModulators.Count, "preset terminal modulator")
        });
        _presetGenerators.Add(_presetGeneratorsOriginalTerminal().Clone());
        _presetModulators.Add(_presetModulatorsOriginalTerminal().Clone());

        // The terminal records are part of the SoundFont table format, not
        // optional metadata.  FluidSynth uses them to delimit the final
        // instrument/bag/generator ranges.
        var terminalInstrument = oldInstruments[^1].Clone();
        terminalInstrument.BagIndex = CheckedUShort(_instrumentBags.Count, "instrument terminal bag");
        _instruments.Add(terminalInstrument);
        _instrumentBags.Add(new Bag
        {
            GeneratorIndex = CheckedUShort(_instrumentGenerators.Count, "instrument terminal generator"),
            ModulatorIndex = CheckedUShort(_instrumentModulators.Count, "instrument terminal modulator")
        });
        _instrumentGenerators.Add(oldInstrumentGenerators[^1].Clone());
        _instrumentModulators.Add(oldInstrumentModulators[^1].Clone());
    }

    private int GetOrAddInstrument(
        int oldIndex,
        Dictionary<int, int> map,
        IReadOnlyList<Instrument> oldInstruments,
        IReadOnlyList<Bag> oldBags,
        IReadOnlyList<Generator> oldGenerators,
        IReadOnlyList<Modulator> oldModulators)
    {
        if (map.TryGetValue(oldIndex, out var existing))
        {
            return existing;
        }

        var newIndex = _instruments.Count;
        map.Add(oldIndex, newIndex);
        var oldInstrument = oldInstruments[oldIndex];
        var newInstrument = oldInstrument.Clone();
        newInstrument.BagIndex = CheckedUShort(_instrumentBags.Count, "instrument bag");
        _instruments.Add(newInstrument);

        var firstBag = oldInstrument.BagIndex;
        var lastBag = oldInstruments[oldIndex + 1].BagIndex;
        for (var oldBagIndex = firstBag; oldBagIndex < lastBag; oldBagIndex++)
        {
            var oldBag = oldBags[oldBagIndex];
            var nextBag = oldBags[oldBagIndex + 1];
            var newBag = new Bag
            {
                GeneratorIndex = CheckedUShort(_instrumentGenerators.Count, "instrument generator"),
                ModulatorIndex = CheckedUShort(_instrumentModulators.Count, "instrument modulator")
            };

            for (var generatorIndex = oldBag.GeneratorIndex; generatorIndex < nextBag.GeneratorIndex; generatorIndex++)
            {
                var generator = oldGenerators[generatorIndex].Clone();
                if (generator.Operator == SampleOperator)
                {
                    var oldSampleIndex = generator.Amount;
                    if (oldSampleIndex >= _samples.Count - 1)
                    {
                        throw new InvalidDataException($"Instrument '{oldInstrument.Name}' references an invalid sample index.");
                    }

                    AddSampleWithLinkedPair(oldSampleIndex);
                }

                _instrumentGenerators.Add(generator);
            }

            for (var modulatorIndex = oldBag.ModulatorIndex; modulatorIndex < nextBag.ModulatorIndex; modulatorIndex++)
            {
                _instrumentModulators.Add(oldModulators[modulatorIndex].Clone());
            }

            _instrumentBags.Add(newBag);
        }

        return newIndex;
    }

    private readonly List<int> _sampleIndices = new();
    private readonly HashSet<int> _sampleIndexSet = new();

    private void AddSampleWithLinkedPair(int index)
    {
        if (_sampleIndexSet.Add(index))
        {
            _sampleIndices.Add(index);
        }

        var linked = _samples[index].SampleLink;
        if (linked != 0 && linked < _samples.Count - 1 && _sampleIndexSet.Add(linked))
        {
            _sampleIndices.Add(linked);
        }
    }

    private List<int> CollectSamples() => _sampleIndices;

    private byte[] BuildSampleData(
        IReadOnlyList<int> selectedSamples,
        IReadOnlyDictionary<int, int> sampleMap,
        out List<SampleSegment> segments)
    {
        if ((_sampleData.Length & 1) != 0)
        {
            throw new InvalidDataException("The SoundFont sample data is not 16-bit aligned.");
        }

        var result = new MemoryStream();
        var byRange = new Dictionary<(int Start, int End), SampleSegment>();
        segments = new List<SampleSegment>();
        foreach (var oldSampleIndex in selectedSamples)
        {
            var sample = _samples[oldSampleIndex];
            var start = checked((int)sample.Start);
            var end = checked((int)sample.End);
            if (start < 0 || end < start || end > _sampleData.Length / 2)
            {
                throw new InvalidDataException($"Sample '{sample.Name}' has an invalid data range.");
            }

            if (!byRange.TryGetValue((start, end), out var segment))
            {
                segment = new SampleSegment(start, end, result.Length / 2);
                byRange.Add((start, end), segment);
                result.Write(_sampleData, start * 2, (end - start) * 2);
                segments.Add(segment);
            }
        }

        result.Write(new byte[SampleGuardPoints * 2]);
        return result.ToArray();
    }

    private List<SampleHeader> BuildSampleHeaders(
        IReadOnlyList<int> selectedSamples,
        IReadOnlyDictionary<int, int> sampleMap,
        IReadOnlyList<SampleSegment> segments,
        int outputSampleCount)
    {
        var result = new List<SampleHeader>(selectedSamples.Count + 1);
        foreach (var oldSampleIndex in selectedSamples)
        {
            var oldSample = _samples[oldSampleIndex];
            var segment = segments.Single(item => item.OldStart == oldSample.Start && item.OldEnd == oldSample.End);
            var sample = oldSample.Clone();
            sample.Start = MapOffset(segment, oldSample.Start);
            sample.End = MapOffset(segment, oldSample.End);
            sample.StartLoop = MapOffset(segment, oldSample.StartLoop);
            sample.EndLoop = MapOffset(segment, oldSample.EndLoop);
            sample.SampleLink = oldSample.SampleLink != 0 && sampleMap.TryGetValue(oldSample.SampleLink, out var linked)
                ? CheckedUShort(linked, "sample link")
                : (ushort)0;
            result.Add(sample);
        }

        var terminal = _samples[^1].Clone();
        terminal.Start = terminal.End = terminal.StartLoop = terminal.EndLoop = checked((uint)outputSampleCount);
        terminal.SampleLink = 0;
        result.Add(terminal);
        return result;
    }

    private static uint MapOffset(SampleSegment segment, uint oldOffset)
    {
        if (oldOffset < segment.OldStart || oldOffset > segment.OldEnd)
        {
            throw new InvalidDataException("A sample loop point falls outside its sample data range.");
        }

        return checked((uint)(segment.NewStart + oldOffset - segment.OldStart));
    }

    private void RemapGenerators(IReadOnlyDictionary<int, int> sampleMap)
    {
        foreach (var generator in _instrumentGenerators)
        {
            if (generator.Operator == SampleOperator)
            {
                if (!sampleMap.TryGetValue(generator.Amount, out var newIndex))
                {
                    throw new InvalidDataException("A selected instrument references a sample that was not copied.");
                }

                generator.Amount = CheckedUShort(newIndex, "sample index");
            }
        }
    }

    private void ValidateTables()
    {
        if (_presets.Count < 2 || _presetBags.Count < 2 || _presetGenerators.Count < 1 ||
            _instruments.Count < 2 || _instrumentBags.Count < 2 || _samples.Count < 2)
        {
            throw new InvalidDataException("The SoundFont hydra tables are incomplete.");
        }

        if ((_sampleData.Length & 1) != 0)
        {
            throw new InvalidDataException("The SoundFont sample data is not 16-bit aligned.");
        }
    }

    private static bool IsWanted(Preset preset) =>
        (preset.Bank == 0 && preset.ProgramNumber is 0 or 11 or 32) ||
        (preset.Bank == 128 && preset.ProgramNumber == 0);

    private int _presetsOriginalNextBag(int index) => _originalPresets[index + 1].BagIndex;
    private Preset _presetsOriginalTerminal() => _originalPresets[^1];
    private Generator _presetGeneratorsOriginalTerminal() => _originalPresetGenerators[^1];
    private Modulator _presetModulatorsOriginalTerminal() => _originalPresetModulators[^1];

    private List<Preset> _originalPresets = null!;
    private List<Generator> _originalPresetGenerators = null!;
    private List<Modulator> _originalPresetModulators = null!;

    private static byte[] GetPayload(byte[] source, IReadOnlyList<Riff.Chunk> chunks, string id) =>
        source.AsSpan(chunks.Single(chunk => chunk.Id == id).PayloadOffset, chunks.Single(chunk => chunk.Id == id).PayloadLength).ToArray();

    private static List<Preset> ParsePresets(byte[] bytes)
    {
        EnsureRecordSize(bytes, 38, "phdr");
        var result = new List<Preset>(bytes.Length / 38);
        for (var offset = 0; offset < bytes.Length; offset += 38)
        {
            result.Add(new Preset(bytes.AsSpan(offset, 38).ToArray()));
        }

        return result;
    }

    private static List<Instrument> ParseInstruments(byte[] bytes)
    {
        EnsureRecordSize(bytes, 22, "inst");
        var result = new List<Instrument>(bytes.Length / 22);
        for (var offset = 0; offset < bytes.Length; offset += 22)
        {
            result.Add(new Instrument(bytes.AsSpan(offset, 22).ToArray()));
        }

        return result;
    }

    private static List<Bag> ParseBags(byte[] bytes)
    {
        EnsureRecordSize(bytes, 4, "bag");
        var result = new List<Bag>(bytes.Length / 4);
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            result.Add(new Bag
            {
                GeneratorIndex = ReadU16(bytes, offset),
                ModulatorIndex = ReadU16(bytes, offset + 2)
            });
        }

        return result;
    }

    private static List<Generator> ParseGenerators(byte[] bytes)
    {
        EnsureRecordSize(bytes, 4, "gen");
        var result = new List<Generator>(bytes.Length / 4);
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            result.Add(new Generator
            {
                Operator = ReadU16(bytes, offset),
                Amount = ReadU16(bytes, offset + 2)
            });
        }

        return result;
    }

    private static List<Modulator> ParseModulators(byte[] bytes)
    {
        EnsureRecordSize(bytes, 10, "mod");
        var result = new List<Modulator>(bytes.Length / 10);
        for (var offset = 0; offset < bytes.Length; offset += 10)
        {
            result.Add(new Modulator(bytes.AsSpan(offset, 10).ToArray()));
        }

        return result;
    }

    private static List<SampleHeader> ParseSamples(byte[] bytes)
    {
        EnsureRecordSize(bytes, 46, "shdr");
        var result = new List<SampleHeader>(bytes.Length / 46);
        for (var offset = 0; offset < bytes.Length; offset += 46)
        {
            result.Add(new SampleHeader(bytes.AsSpan(offset, 46).ToArray()));
        }

        return result;
    }

    private static void EnsureRecordSize(byte[] bytes, int size, string name)
    {
        if (bytes.Length == 0 || bytes.Length % size != 0)
        {
            throw new InvalidDataException($"The {name} table has an invalid size.");
        }
    }

    private static byte[] Encode<T>(IEnumerable<T> records) where T : IWritableRecord
    {
        using var stream = new MemoryStream();
        foreach (var record in records)
        {
            record.WriteTo(stream);
        }

        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string id, byte[] payload, string? listType = null)
    {
        var body = listType is null
            ? payload
            : Encoding.ASCII.GetBytes(listType).Concat(payload).ToArray();
        stream.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)body.Length));
        stream.Write(length);
        stream.Write(body);
        if ((body.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static string Text(byte[] bytes, int offset, int length) =>
        Encoding.ASCII.GetString(bytes, offset, length);

    private static ushort CheckedUShort(int value, string field) =>
        value is < 0 or > ushort.MaxValue
            ? throw new InvalidDataException($"The generated {field} index exceeds the SoundFont limit.")
            : (ushort)value;

    private interface IWritableRecord
    {
        void WriteTo(Stream stream);
    }

    private sealed class Preset : IWritableRecord
    {
        private readonly byte[] _raw;
        public Preset(byte[] raw) => _raw = raw;
        public ushort ProgramNumber => ReadU16(_raw, 20);
        public ushort Bank => ReadU16(_raw, 22);
        public ushort BagIndex { get => ReadU16(_raw, 24); set => WriteU16(_raw, 24, value); }
        public string Name => Text(_raw, 0, 20).TrimEnd('\0', ' ');
        public Preset Clone() => new(_raw.ToArray());
        public void WriteTo(Stream stream) => stream.Write(_raw);
    }

    private sealed class Instrument : IWritableRecord
    {
        private readonly byte[] _raw;
        public Instrument(byte[] raw) => _raw = raw;
        public ushort BagIndex { get => ReadU16(_raw, 20); set => WriteU16(_raw, 20, value); }
        public string Name => Text(_raw, 0, 20).TrimEnd('\0', ' ');
        public Instrument Clone() => new(_raw.ToArray());
        public void WriteTo(Stream stream) => stream.Write(_raw);
    }

    private sealed class Bag : IWritableRecord
    {
        public ushort GeneratorIndex { get; set; }
        public ushort ModulatorIndex { get; set; }
        public void WriteTo(Stream stream)
        {
            Span<byte> bytes = stackalloc byte[4];
            WriteU16(bytes, 0, GeneratorIndex);
            WriteU16(bytes, 2, ModulatorIndex);
            stream.Write(bytes);
        }
    }

    private sealed class Generator : IWritableRecord
    {
        public ushort Operator { get; set; }
        public ushort Amount { get; set; }
        public Generator Clone() => new() { Operator = Operator, Amount = Amount };
        public void WriteTo(Stream stream)
        {
            Span<byte> bytes = stackalloc byte[4];
            WriteU16(bytes, 0, Operator);
            WriteU16(bytes, 2, Amount);
            stream.Write(bytes);
        }
    }

    private sealed class Modulator : IWritableRecord
    {
        private readonly byte[] _raw;
        public Modulator(byte[] raw) => _raw = raw;
        public Modulator Clone() => new(_raw.ToArray());
        public void WriteTo(Stream stream) => stream.Write(_raw);
    }

    private sealed class SampleHeader : IWritableRecord
    {
        private readonly byte[] _raw;
        public SampleHeader(byte[] raw) => _raw = raw;
        public uint Start { get => ReadU32(_raw, 20); set => WriteU32(_raw, 20, value); }
        public uint End { get => ReadU32(_raw, 24); set => WriteU32(_raw, 24, value); }
        public uint StartLoop { get => ReadU32(_raw, 28); set => WriteU32(_raw, 28, value); }
        public uint EndLoop { get => ReadU32(_raw, 32); set => WriteU32(_raw, 32, value); }
        // shdr layout: sampleLink is at 42, followed by sampleType at 44.
        public ushort SampleLink { get => ReadU16(_raw, 42); set => WriteU16(_raw, 42, value); }
        public string Name => Text(_raw, 0, 20).TrimEnd('\0', ' ');
        public SampleHeader Clone() => new(_raw.ToArray());
        public void WriteTo(Stream stream) => stream.Write(_raw);
    }

    private readonly record struct SampleSegment(int OldStart, int OldEnd, long NewStart);

    private static uint ReadU32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void WriteU16(Span<byte> bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), value);

    private static class Riff
    {
        public readonly record struct Chunk(
            string Id,
            int RawOffset,
            int RawLength,
            int PayloadOffset,
            int PayloadLength,
            string? ListType)
        {
            public bool IsList(string type) => Id == "LIST" && ListType == type;
        }

        public static IReadOnlyList<Chunk> ReadTopLevel(byte[] bytes) =>
            ReadChunks(bytes, 12, bytes.Length);

        public static IReadOnlyList<Chunk> ReadListChildren(byte[] bytes, Chunk list)
        {
            if (list.Id != "LIST" || list.PayloadLength < 4)
            {
                throw new InvalidDataException("Malformed LIST chunk.");
            }

            return ReadChunks(bytes, list.PayloadOffset + 4, list.PayloadOffset + list.PayloadLength);
        }

        private static IReadOnlyList<Chunk> ReadChunks(byte[] bytes, int start, int end)
        {
            var chunks = new List<Chunk>();
            var offset = start;
            while (offset + 8 <= end)
            {
                var id = Text(bytes, offset, 4);
                var payloadLength = checked((int)ReadU32(bytes, offset + 4));
                var payloadOffset = checked(offset + 8);
                var rawLength = checked(8 + payloadLength + (payloadLength & 1));
                if (payloadOffset + payloadLength > end || offset + rawLength > end)
                {
                    throw new InvalidDataException($"Chunk '{id}' extends beyond its parent.");
                }

                var listType = id == "LIST" && payloadLength >= 4
                    ? Text(bytes, payloadOffset, 4)
                    : null;
                chunks.Add(new Chunk(id, offset, rawLength, payloadOffset, payloadLength, listType));
                offset += rawLength;
            }

            if (offset != end)
            {
                throw new InvalidDataException("RIFF chunk alignment is invalid.");
            }

            return chunks;
        }
    }
}
