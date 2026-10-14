using System.Security.Cryptography;
using QuakeSharp.Core.GameData;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The shareware made playable from its own untouched archive (design.md §11.10, decision 78): quake106.zip's LHA
/// archive read by QuakeSharp's own decoder, every member's CRC right, and the pak0.pak installed the catalogue's.
/// </summary>
public sealed class SharewareInstallerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EveryMemberOfResource1UnpacksWithItsCrc()
    {
        string zip = GameDataLocator.RequireSharewareZip();
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        System.IO.Compression.ZipArchiveEntry resource = archive.GetEntry("resource.1")!;
        byte[] data = new byte[resource.Length];
        using (Stream s = resource.Open())
        {
            s.ReadExactly(data);
        }

        var lha = new LhaArchive(data);
        Assert.Equal(16, lha.Entries.Count);
        foreach (LhaArchive.Entry entry in lha.Entries)
        {
            Assert.Equal(entry.Size, lha.Extract(entry).Length); // Extract checks the CRC
        }
    }

    [Fact]
    public void ThePakIsInstalledOnceAndIsTheCataloguesWithTheLicenceBesideIt()
    {
        string zip = GameDataLocator.RequireSharewareZip();
        Assert.True(SharewareInstaller.Install(zip, dir));
        string pak = Path.Combine(dir, "id1", "pak0.pak");
        Assert.Equal("5906e5998fc3d896ddaf5e6a62e03abb", Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(pak))));
        Assert.Contains("LIMITED USE SOFTWARE LICENSE AGREEMENT", File.ReadAllText(Path.Combine(dir, "slicnse.txt")));
        Assert.False(File.Exists(pak + ".part"));

        // a second time: nothing to do
        DateTime written = File.GetLastWriteTimeUtc(pak);
        Assert.True(SharewareInstaller.Install(zip, dir));
        Assert.Equal(written, File.GetLastWriteTimeUtc(pak));
    }
}
