using System.Buffers.Binary;
using System.Diagnostics;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Sound;
using QuakeSharp.Platform.Sdl;
using SDL;
using static QuakeSharp.Core.Sound.Mci;

namespace QuakeSharp.Platform.Tests;

/// <summary>
/// The executable's CD drive over track files (design.md §7.2): which files make the disc, the MCI answers, and a
/// track's end posted as cd_win.c expects it. The tracks play on SDL's dummy audio driver, which takes the samples as
/// a device would, in real time, without one.
/// </summary>
public sealed class CdDriveTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public CdDriveTests()
    {
        Directory.CreateDirectory(dir);
        SDL3.SDL_SetHint(SDL3.SDL_HINT_AUDIO_DRIVER, "dummy");
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void TheDiscIsTheFilesFoundTheFirstFolderWinning()
    {
        string first = Folder("first"), second = Folder("second");
        Wav(Path.Combine(first, "TRACK03.WAV"), 0.1);
        Wav(Path.Combine(second, "track03.wav"), 0.1);
        Wav(Path.Combine(second, "track05.wav"), 0.1);

        CdTracks tracks = CdTracks.In([first, second, Path.Combine(dir, "missing")]);
        Assert.Equal(5, tracks.Count);
        Assert.Equal(Path.Combine(first, "TRACK03.WAV"), tracks.File(3));
        Assert.Equal(Path.Combine(second, "track05.wav"), tracks.File(5));
        Assert.Null(tracks.File(1));
        Assert.Null(tracks.File(4));
    }

    [Fact]
    public void TheSteamInstallsRereleaseMusicIsFound()
    {
        string music = Folder(Path.Combine("rerelease", "id1", "music"));
        Wav(Path.Combine(music, "track02.wav"), 0.1);

        CdTracks tracks = CdTracks.Find(dir, new CommandLine(["quake"]));
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void WithoutFilesTheDriveHasNoDisc()
    {
        using var drive = new FileCdDrive(CdTracks.In([dir]));
        uint id = Open(drive);

        var status = new MciParms { dwItem = MCI_STATUS_READY };
        Assert.Equal(0u, drive.mciSendCommand(id, MCI_STATUS, MCI_STATUS_ITEM | MCI_WAIT, status));
        Assert.Equal(0u, status.dwReturn);
        Assert.Equal(MCIERR_HARDWARE, drive.mciSendCommand(id, MCI_PLAY, MCI_FROM, new MciParms { dwFrom = 2 }));
    }

    [Fact]
    public void TracksWithoutFilesAreNotAudio()
    {
        string music = Folder("music");
        Wav(Path.Combine(music, "track03.wav"), 0.1);
        using var drive = new FileCdDrive(CdTracks.In([music]));
        uint id = Open(drive);

        Assert.Equal(3u, Status(drive, id, MCI_STATUS_NUMBER_OF_TRACKS, 0));
        Assert.Equal(MCI_CDA_TRACK_OTHER, Status(drive, id, MCI_CDA_STATUS_TYPE_TRACK, 1));
        Assert.Equal(MCI_CDA_TRACK_OTHER, Status(drive, id, MCI_CDA_STATUS_TYPE_TRACK, 2));
        Assert.Equal(MCI_CDA_TRACK_AUDIO, Status(drive, id, MCI_CDA_STATUS_TYPE_TRACK, 3));

        // 2.5 s: 0 minutes, 2 seconds, 37 frames of 1/75 s
        string longer = Path.Combine(music, "track04.wav");
        Wav(longer, 2.5);
        using var drive2 = new FileCdDrive(CdTracks.In([music]));
        uint id2 = Open(drive2);
        Assert.Equal(MCI_MAKE_MSF(0, 2, 37), Status(drive2, id2, MCI_STATUS_LENGTH, 4));
    }

    [Fact]
    public void ATrackPlayedToItsEndIsPosted()
    {
        string music = Folder("music");
        Wav(Path.Combine(music, "track02.wav"), 0.2);
        using var drive = new FileCdDrive(CdTracks.In([music]));
        uint id = Open(drive);

        var play = new MciParms { dwFrom = MCI_MAKE_TMSF(2, 0, 0, 0), dwTo = MCI_MAKE_TMSF(3, 0, 0, 0) };
        Assert.Equal(0u, drive.mciSendCommand(id, MCI_PLAY, MCI_NOTIFY | MCI_FROM | MCI_TO, play));

        var clock = Stopwatch.StartNew();
        uint wParam = 0, lParam = 0;
        while (!drive.PeekNotify(out wParam, out lParam) && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
        }

        Assert.Equal(MCI_NOTIFY_SUCCESSFUL, wParam);
        Assert.Equal(id, lParam);
    }

    [Fact]
    public void AStoppedTrackIsNotPosted()
    {
        string music = Folder("music");
        Wav(Path.Combine(music, "track02.wav"), 0.2);
        using var drive = new FileCdDrive(CdTracks.In([music]));
        uint id = Open(drive);

        drive.mciSendCommand(id, MCI_PLAY, MCI_NOTIFY | MCI_FROM | MCI_TO, new MciParms { dwFrom = 2 });
        drive.mciSendCommand(id, MCI_STOP, 0, null);
        Thread.Sleep(600);
        Assert.False(drive.PeekNotify(out _, out _));
    }

    // The rerelease's Ogg Vorbis tracks decode (with game data only: QUAKESHARP_QUAKE_DIR holding rerelease/id1/music)
    [Fact]
    public void TheRereleasesTracksDecode()
    {
        string? quake = Environment.GetEnvironmentVariable("QUAKESHARP_QUAKE_DIR")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        CdTracks? tracks = quake == null ? null : CdTracks.Find(quake, new CommandLine(["quake"]));
        Assert.SkipUnless(tracks?.File(2)?.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) == true,
            "Set QUAKESHARP_QUAKE_DIR to a Quake folder with the rerelease's music (rerelease/id1/music/track02.ogg).");

        for (int track = 2; track <= tracks!.Count; track++)
        {
            Assert.True(CdTrackPlayer.Length(tracks.File(track)!) > TimeSpan.FromSeconds(30), $"track {track}");
        }
    }

    private static uint Open(ICdDrive drive)
    {
        var open = new MciParms { lpstrDeviceType = "cdaudio" };
        Assert.Equal(0u, drive.mciSendCommand(0, MCI_OPEN, MCI_OPEN_TYPE | MCI_OPEN_SHAREABLE, open));
        return open.wDeviceID;
    }

    private static uint Status(ICdDrive drive, uint id, uint item, uint track)
    {
        var status = new MciParms { dwItem = item, dwTrack = track };
        Assert.Equal(0u, drive.mciSendCommand(id, MCI_STATUS, MCI_STATUS_ITEM | MCI_WAIT | (track != 0 ? MCI_TRACK : 0), status));
        return status.dwReturn;
    }

    private string Folder(string name)
    {
        string path = Path.Combine(dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // a WAV of silence: 16-bit stereo at 22050 Hz
    private static void Wav(string path, double seconds)
    {
        const int Rate = 22050, Channels = 2, Bytes = 2;
        int data = (int)(seconds * Rate) * Channels * Bytes;
        byte[] wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), Rate * Channels * Bytes);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), Channels * Bytes);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), Bytes * 8);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        File.WriteAllBytes(path, wav);
    }
}
