using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Sound;

/// <summary>sound.h: sfx_t (cache is cache.data: the loaded sound, which stays loaded)</summary>
public sealed class Sfx
{
    public string name = "";
    public SfxCache? cache;
}

/// <summary>sound.h: sfxcache_t</summary>
public sealed class SfxCache
{
    public int length;
    public int loopstart;
    public int speed;
    public int width;
    public int stereo;

    /// <summary>variable sized</summary>
    public byte[] data = [];
}

/// <summary>sound.h: portable_samplepair_t</summary>
public struct PortableSamplePair
{
    public int left;
    public int right;
}

/// <summary>sound.h: channel_t. <see cref="Clear"/> is C's memset to 0.</summary>
public sealed class Channel
{
    /// <summary>sfx number</summary>
    public Sfx? sfx;

    /// <summary>0-255 volume</summary>
    public int leftvol;

    /// <summary>0-255 volume</summary>
    public int rightvol;

    /// <summary>end time in global paintsamples</summary>
    public int end;

    /// <summary>sample position in sfx</summary>
    public int pos;

    /// <summary>where to loop, -1 = no looping</summary>
    public int looping;

    /// <summary>to allow overriding a specific sound</summary>
    public int entnum;

    public int entchannel;

    /// <summary>origin of sound effect</summary>
    public Vec3 origin;

    /// <summary>distance multiplier (attenuation/clipK)</summary>
    public float dist_mult;

    /// <summary>0-255 master volume</summary>
    public int master_vol;

    public void Clear()
    {
        sfx = null;
        leftvol = 0;
        rightvol = 0;
        end = 0;
        pos = 0;
        looping = 0;
        entnum = 0;
        entchannel = 0;
        origin = default;
        dist_mult = 0;
        master_vol = 0;
    }
}

/// <summary>sound.h: wavinfo_t</summary>
public struct WavInfo
{
    public int rate;
    public int width;
    public int channels;
    public int loopstart;
    public int samples;

    /// <summary>chunk starts this many bytes from file start</summary>
    public int dataofs;
}
