using System.Text;
using QuakeSharp.Core.GameData;
using Xunit;

namespace QuakeSharp.Core.Tests;

// The LHA reader (design.md §11.10) on archives made here: level 0 headers, stored members, a self-extractor's stub
// before them; the LH5 decoder itself is checked on the shareware's archive (SharewareInstallerTests)
public class LhaArchiveTests
{
    private static byte[] Member(string name, byte[] content, string method = "-lh0-", ushort? crc = null)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        int headerSize = 22 + nameBytes.Length + 2 - 2; // the header after its first two bytes
        var header = new List<byte> { (byte)headerSize, 0 };
        header.AddRange(Encoding.ASCII.GetBytes(method));
        header.AddRange(BitConverter.GetBytes(content.Length)); // packed
        header.AddRange(BitConverter.GetBytes(content.Length)); // size
        header.AddRange([0, 0, 0, 0, 0x20, 0]); // time, attributes, level 0
        header.Add((byte)nameBytes.Length);
        header.AddRange(nameBytes);
        header.AddRange(BitConverter.GetBytes(crc ?? LhaArchive.Crc16(content)));
        header.AddRange(content);
        return [.. header];
    }

    private static byte[] Archive(params byte[][] members) =>
        [.. Encoding.ASCII.GetBytes("MZ a self-extractor's stub, -not a header-"), .. members.SelectMany(m => m), 0];

    [Fact]
    public void TheCrcIsArcs() => Assert.Equal(0xBB3D, LhaArchive.Crc16("123456789"u8));

    [Fact]
    public void StoredMembersAreReadPastTheStub()
    {
        var lha = new LhaArchive(Archive(Member(@"ID1\A.TXT", "hello"u8.ToArray()), Member("B.BIN", [1, 2, 3])));
        Assert.Equal([@"ID1\A.TXT", "B.BIN"], lha.Entries.Select(e => e.Name));
        Assert.Equal("hello"u8.ToArray(), lha.Extract(lha.Find("id1/a.txt")!));
        Assert.Equal([1, 2, 3], lha.Extract(lha.Find("b.bin")!));
        Assert.Null(lha.Find("c.bin"));
    }

    [Fact]
    public void AWrongCrcOrAnUnknownMethodIsRefused()
    {
        var bad = new LhaArchive(Archive(Member("A", [1, 2, 3], crc: 1)));
        Assert.Throws<InvalidDataException>(() => bad.Extract(bad.Entries[0]));
        var lh7 = new LhaArchive(Archive(Member("A", [1, 2, 3], method: "-lh7-")));
        Assert.Throws<InvalidDataException>(() => lh7.Extract(lh7.Entries[0]));
    }

    [Fact]
    public void AnArchiveCutShortIsRefused()
    {
        byte[] whole = Archive(Member("A", new byte[100]));
        Assert.Throws<InvalidDataException>(() => new LhaArchive(whole[..^50]));
    }
}
