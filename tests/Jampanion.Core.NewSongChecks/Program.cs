using Jampanion.Core.Music;

var counts = new[] { 4, 5, 7, 31, 32, 33, 127, NewSongTemplate.MaximumBarCount };
foreach (var count in counts)
{
    var source = NewSongTemplate.CreateChordPro(count, $"Test Song {count}", $"new-song-{count}");
    if (source.Contains("Cmaj7", StringComparison.Ordinal) || !source.Contains("| C ", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("A new song must use one plain C chord per bar.");
    }
    var tune = ChordProSongParser.Parse(source, $"new-song-{count}.cho");
    if (tune.Title != $"Test Song {count}")
    {
        throw new InvalidOperationException($"Unexpected title: {tune.Title}");
    }
    if (tune.Bars.Count != count)
    {
        throw new InvalidOperationException($"Expected {count} bars, parsed {tune.Bars.Count}.");
    }
    var expectedSegments = (count + SessionConstants.BarsPerSegment - 1) /
        SessionConstants.BarsPerSegment;
    if (tune.SegmentCount != expectedSegments)
    {
        throw new InvalidOperationException(
            $"Expected {expectedSegments} segments for {count} bars, parsed {tune.SegmentCount}.");
    }
}

try
{
    _ = NewSongTemplate.CreateChordPro(NewSongTemplate.MaximumBarCount + 1);
    throw new InvalidOperationException("The maximum-bar guard did not run.");
}
catch (ArgumentOutOfRangeException)
{
}

Console.WriteLine("New Song contract checks passed.");
