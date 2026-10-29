using System.Text;
using UsageCore.Parsing;

namespace UsageCore.Tests;

public class LineReadingTests
{
    private static List<string> ReadAll(string content, int maxLineBytes, out int oversized)
    {
        using var reader = new ByteLineReader(new MemoryStream(Encoding.UTF8.GetBytes(content)), maxLineBytes);
        var lines = new List<string>();
        while (reader.TryReadLine(out var line)) lines.Add(Encoding.UTF8.GetString(line));
        oversized = reader.OversizedLines;
        return lines;
    }

    [Fact]
    public void ALineOverTheCeilingIsSkipped_AndReadingResumesAfterIt()
    {
        var lines = ReadAll($"first\n{new string('x', 5_000)}\nsecond\n{new string('y', 300)}\nthird", 256, out var oversized);
        Assert.Equal(["first", "second", "third"], lines);
        Assert.Equal(2, oversized);
    }

    [Fact]
    public void AFileWithNoNewlineAtAllEndsWithoutBeingHeld()
    {
        Assert.Empty(ReadAll(new string('z', 10_000), 256, out var oversized));
        Assert.Equal(1, oversized);
    }

    [Fact]
    public void LinesUnderTheCeilingGrowTheBufferAsBefore()
    {
        // The newline has to fit too.
        var exact = new string('a', 255);
        Assert.Equal([exact, "b"], ReadAll($"{exact}\nb\n", 256, out var oversized));
        Assert.Equal(0, oversized);
    }
}
