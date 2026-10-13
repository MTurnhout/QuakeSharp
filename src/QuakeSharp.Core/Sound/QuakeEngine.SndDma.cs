using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Sound;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core;

// snd_dma.c: main control for any streaming sound output device. The device is an ISoundDma (snd_win.c's role).
public sealed partial class QuakeEngine
{
    // sound.h
    public const int DEFAULT_SOUND_PACKET_VOLUME = 255;
    public const double DEFAULT_SOUND_PACKET_ATTENUATION = 1.0;
    public const int MAX_CHANNELS = 128;
    public const int MAX_DYNAMIC_CHANNELS = 8;

    // snd_dma.c: MAX_SFX
    private const int MAX_SFX = 512;

    // MAX_DYNAMIC_CHANNELS to MAX_DYNAMIC_CHANNELS + NUM_AMBIENTS -1 = water, etc
    // MAX_DYNAMIC_CHANNELS + NUM_AMBIENTS to total_channels = static sounds
    public readonly Channel[] channels = [.. Enumerable.Range(0, MAX_CHANNELS).Select(_ => new Channel())];

    public int total_channels;

    public int snd_blocked;
    private bool snd_ambient = true;
    public bool snd_initialized;

    /// <summary>pointer should go away</summary>
    public Dma? shm;

    public Vec3 listener_origin;
    public Vec3 listener_forward;
    public Vec3 listener_right;
    public Vec3 listener_up;
    public float sound_nominal_clip_dist = 1000.0f;

    /// <summary>sample PAIRS</summary>
    public int soundtime;

    /// <summary>sample PAIRS</summary>
    public int paintedtime;

    /// <summary>hunk allocated [MAX_SFX]</summary>
    public Sfx[]? known_sfx;

    public int num_sfx;

    public readonly Sfx?[] ambient_sfx = new Sfx?[NUM_AMBIENTS];

    public int desired_speed = 11025;
    public int desired_bits = 16;

    public int sound_started;

    public readonly Cvar bgmvolume = new("bgmvolume", "1", true);
    public readonly Cvar volume = new("volume", "0.7", true);

    public readonly Cvar nosound = new("nosound", "0");
    public readonly Cvar precache = new("precache", "1");
    public readonly Cvar loadas8bit = new("loadas8bit", "0");
    public readonly Cvar bgmbuffer = new("bgmbuffer", "4096");
    public readonly Cvar ambient_level = new("ambient_level", "0.3");
    public readonly Cvar ambient_fade = new("ambient_fade", "100");
    public readonly Cvar snd_noextraupdate = new("snd_noextraupdate", "0");
    public readonly Cvar snd_show = new("snd_show", "0");
    public readonly Cvar _snd_mixahead = new("_snd_mixahead", "0.1", true);

    // Fake dma is a synchronous faking of the DMA progress used for isolating performance in the renderer. The
    // fakedma_updates is number of times S_Update() is called per second.
    public bool fakedma;
    public int fakedma_updates = 15;

    // snd_dma.c: GetSoundtime's static buffers, oldsamplepos
    private int snd_buffers;
    private int snd_oldsamplepos;

    // snd_dma.c: S_Play's and S_PlayVol's static hash
    private int s_play_hash = 345;
    private int s_playvol_hash = 543;

    // snd_dma.c: S_AmbientOff
    public void S_AmbientOff() => snd_ambient = false;

    // snd_dma.c: S_AmbientOn
    public void S_AmbientOn() => snd_ambient = true;

    // snd_dma.c: S_SoundInfo_f
    public void S_SoundInfo_f()
    {
        if (sound_started == 0 || shm == null)
        {
            Con_Printf("sound system not started\n");
            return;
        }

        Con_Printf("%5d stereo\n", shm.channels - 1);
        Con_Printf("%5d samples\n", shm.samples);
        Con_Printf("%5d samplepos\n", shm.samplepos);
        Con_Printf("%5d samplebits\n", shm.samplebits);
        Con_Printf("%5d submission_chunk\n", shm.submission_chunk);
        Con_Printf("%5d speed\n", shm.speed);
        Con_Printf("0x%x dma buffer\n", 0); // the buffer's address in C
        Con_Printf("%5d total_channels\n", total_channels);
    }

    // snd_dma.c: S_Startup
    public void S_Startup()
    {
        if (!snd_initialized)
        {
            return;
        }

        if (!fakedma)
        {
            shm = SNDDMA_Init();

            if (shm == null)
            {
                sound_started = 0;
                return;
            }
        }

        sound_started = 1;
    }

    // snd_dma.c: S_Init
    public void S_Init()
    {
        Con_Printf("\nSound Initialization\n");

        if (COM_CheckParm("-nosound") != 0)
        {
            return;
        }

        if (COM_CheckParm("-simsound") != 0)
        {
            fakedma = true;
        }

        Cmd_AddCommand("play", S_Play);
        Cmd_AddCommand("playvol", S_PlayVol);
        Cmd_AddCommand("stopsound", S_StopAllSoundsC);
        Cmd_AddCommand("soundlist", S_SoundList);
        Cmd_AddCommand("soundinfo", S_SoundInfo_f);

        Cvar_RegisterVariable(nosound);
        Cvar_RegisterVariable(volume);
        Cvar_RegisterVariable(precache);
        Cvar_RegisterVariable(loadas8bit);
        Cvar_RegisterVariable(bgmvolume);
        Cvar_RegisterVariable(bgmbuffer);
        Cvar_RegisterVariable(ambient_level);
        Cvar_RegisterVariable(ambient_fade);
        Cvar_RegisterVariable(snd_noextraupdate);
        Cvar_RegisterVariable(snd_show);
        Cvar_RegisterVariable(_snd_mixahead);

        if (host_parms.memsize < 0x800000)
        {
            Cvar_Set("loadas8bit", "1");
            Con_Printf("loading all sounds as 8bit\n");
        }

        snd_initialized = true;

        S_Startup();

        SND_InitScaletable();

        known_sfx = [.. Enumerable.Range(0, MAX_SFX).Select(_ => new Sfx())];
        num_sfx = 0;

        // create a piece of DMA memory
        if (fakedma)
        {
            shm = new Dma
            {
                splitbuffer = false,
                samplebits = 16,
                speed = 22050,
                channels = 2,
                samples = 32768,
                samplepos = 0,
                soundalive = true,
                gamealive = true,
                submission_chunk = 1,
                buffer = new byte[1 << 16],
            };
        }

        // (shm is NULL in C when the device failed: Con_Printf reads through it, as WinQuake would crash there)
        Con_Printf("Sound sampling rate: %i\n", shm?.speed ?? 0);

        // provides a tick sound until washed clean
        ambient_sfx[AMBIENT_WATER] = S_PrecacheSound("ambience/water1.wav");
        ambient_sfx[AMBIENT_SKY] = S_PrecacheSound("ambience/wind2.wav");

        S_StopAllSounds(true);
    }

    // =======================================================================
    // Shutdown sound engine
    // =======================================================================

    // snd_dma.c: S_Shutdown
    public void S_Shutdown()
    {
        if (sound_started == 0)
        {
            return;
        }

        if (shm != null)
        {
            shm.gamealive = false;
        }

        shm = null;
        sound_started = 0;

        if (!fakedma)
        {
            sounddma?.Shutdown();
        }
    }

    // =======================================================================
    // Load a sound
    // =======================================================================

    // snd_dma.c: S_FindName
    public Sfx S_FindName(string name)
    {
        int i;
        Sfx sfx;

        if (Q_strlen(name) >= QuakeDef.MAX_QPATH)
        {
            throw Sys_Error("Sound name too long: %s", name);
        }

        // see if already loaded
        for (i = 0; i < num_sfx; i++)
        {
            if (Q_strcmp(known_sfx![i].name, name) == 0)
            {
                return known_sfx[i];
            }
        }

        if (num_sfx == MAX_SFX)
        {
            throw Sys_Error("S_FindName: out of sfx_t");
        }

        sfx = known_sfx![i];
        sfx.name = name;

        num_sfx++;

        return sfx;
    }

    // snd_dma.c: S_TouchSound
    public void S_TouchSound(string name)
    {
        if (sound_started == 0)
        {
            return;
        }

        // (Cache_Check: nothing to touch in managed memory)
        _ = S_FindName(name);
    }

    // snd_dma.c: S_PrecacheSound
    public Sfx? S_PrecacheSound(string name)
    {
        Sfx sfx;

        if (sound_started == 0 || nosound.value != 0)
        {
            return null;
        }

        sfx = S_FindName(name);

        // cache it in
        if (precache.value != 0)
        {
            S_LoadSound(sfx);
        }

        return sfx;
    }

    // =============================================================================

    // snd_dma.c: SND_PickChannel
    public Channel? SND_PickChannel(int entnum, int entchannel)
    {
        int ch_idx;
        int first_to_die;
        int life_left;

        // Check for replacement sound, or find the best one to replace
        first_to_die = -1;
        life_left = 0x7fffffff;
        for (ch_idx = NUM_AMBIENTS; ch_idx < NUM_AMBIENTS + MAX_DYNAMIC_CHANNELS; ch_idx++)
        {
            if (entchannel != 0 // channel 0 never overrides
                && channels[ch_idx].entnum == entnum
                && (channels[ch_idx].entchannel == entchannel || entchannel == -1))
            {
                // allways override sound from same entity
                first_to_die = ch_idx;
                break;
            }

            // don't let monster sounds override player sounds
            if (channels[ch_idx].entnum == cl.viewentity && entnum != cl.viewentity && channels[ch_idx].sfx != null)
            {
                continue;
            }

            if (unchecked(channels[ch_idx].end - paintedtime) < life_left)
            {
                life_left = unchecked(channels[ch_idx].end - paintedtime);
                first_to_die = ch_idx;
            }
        }

        if (first_to_die == -1)
        {
            return null;
        }

        if (channels[first_to_die].sfx != null)
        {
            channels[first_to_die].sfx = null;
        }

        return channels[first_to_die];
    }

    // snd_dma.c: SND_Spatialize
    public void SND_Spatialize(Channel ch)
    {
        float dot;
        float dist;
        float lscale, rscale, scale;
        Vec3 source_vec = default;

        // anything coming from the view entity will allways be full volume
        if (ch.entnum == cl.viewentity)
        {
            ch.leftvol = ch.master_vol;
            ch.rightvol = ch.master_vol;
            return;
        }

        // calculate stereo seperation and distance attenuation
        VectorSubtract(ch.origin, listener_origin, ref source_vec);

        dist = VectorNormalize(ref source_vec) * ch.dist_mult;

        dot = DotProduct(listener_right, source_vec);

        if (shm!.channels == 1)
        {
            rscale = 1.0f;
            lscale = 1.0f;
        }
        else
        {
            rscale = (float)(1.0 + dot);
            lscale = (float)(1.0 - dot);
        }

        // add in distance effect
        scale = (float)((1.0 - dist) * rscale);
        ch.rightvol = (int)(ch.master_vol * scale);
        if (ch.rightvol < 0)
        {
            ch.rightvol = 0;
        }

        scale = (float)((1.0 - dist) * lscale);
        ch.leftvol = (int)(ch.master_vol * scale);
        if (ch.leftvol < 0)
        {
            ch.leftvol = 0;
        }
    }

    // =======================================================================
    // Start a sound effect
    // =======================================================================

    // snd_dma.c: S_StartSound
    public void S_StartSound(int entnum, int entchannel, Sfx? sfx, in Vec3 origin, float fvol, float attenuation)
    {
        Channel? target_chan;
        Channel check;
        SfxCache? sc;
        int vol;
        int ch_idx;
        int skip;

        if (sound_started == 0)
        {
            return;
        }

        if (sfx == null)
        {
            return;
        }

        if (nosound.value != 0)
        {
            return;
        }

        vol = (int)(fvol * 255);

        // pick a channel to play on
        target_chan = SND_PickChannel(entnum, entchannel);
        if (target_chan == null)
        {
            return;
        }

        // spatialize
        target_chan.Clear();
        VectorCopy(origin, ref target_chan.origin);
        target_chan.dist_mult = attenuation / sound_nominal_clip_dist;
        target_chan.master_vol = vol;
        target_chan.entnum = entnum;
        target_chan.entchannel = entchannel;
        SND_Spatialize(target_chan);

        if (target_chan.leftvol == 0 && target_chan.rightvol == 0)
        {
            return; // not audible at all
        }

        // new channel
        sc = S_LoadSound(sfx);
        if (sc == null)
        {
            target_chan.sfx = null;
            return; // couldn't load the sound's data
        }

        target_chan.sfx = sfx;
        target_chan.pos = 0;
        target_chan.end = paintedtime + sc.length;

        // if an identical sound has also been started this frame, offset the pos a bit to keep it from just making
        // the first one louder
        for (ch_idx = NUM_AMBIENTS; ch_idx < NUM_AMBIENTS + MAX_DYNAMIC_CHANNELS; ch_idx++)
        {
            check = channels[ch_idx];
            if (check == target_chan)
            {
                continue;
            }

            if (check.sfx == sfx && check.pos == 0)
            {
                skip = rand() % (int)(0.1 * shm!.speed);
                if (skip >= target_chan.end)
                {
                    skip = target_chan.end - 1;
                }

                target_chan.pos += skip;
                target_chan.end -= skip;
                break;
            }
        }
    }

    // snd_dma.c: S_StopSound
    public void S_StopSound(int entnum, int entchannel)
    {
        int i;

        for (i = 0; i < MAX_DYNAMIC_CHANNELS; i++)
        {
            if (channels[i].entnum == entnum
                && channels[i].entchannel == entchannel)
            {
                channels[i].end = 0;
                channels[i].sfx = null;
                return;
            }
        }
    }

    // snd_dma.c: S_StopAllSounds
    public void S_StopAllSounds(bool clear)
    {
        int i;

        if (sound_started == 0)
        {
            return;
        }

        total_channels = MAX_DYNAMIC_CHANNELS + NUM_AMBIENTS; // no statics

        for (i = 0; i < MAX_CHANNELS; i++)
        {
            channels[i].Clear();
        }

        if (clear)
        {
            S_ClearBuffer();
        }
    }

    // snd_dma.c: S_StopAllSoundsC
    public void S_StopAllSoundsC() => S_StopAllSounds(true);

    // snd_dma.c: S_ClearBuffer
    public void S_ClearBuffer()
    {
        int clear;

        enhanced?.Sound?.Clear();

        if (sound_started == 0 || shm == null || shm.buffer.Length == 0)
        {
            return;
        }

        if (shm.samplebits == 8)
        {
            clear = 0x80;
        }
        else
        {
            clear = 0;
        }

        shm.buffer.AsSpan(0, shm.samples * shm.samplebits / 8).Fill((byte)clear);
    }

    // snd_dma.c: S_StaticSound
    public void S_StaticSound(Sfx? sfx, in Vec3 origin, float vol, float attenuation)
    {
        Channel ss;
        SfxCache? sc;

        if (sfx == null)
        {
            return;
        }

        if (total_channels == MAX_CHANNELS)
        {
            Con_Printf("total_channels == MAX_CHANNELS\n");
            return;
        }

        ss = channels[total_channels];
        total_channels++;

        sc = S_LoadSound(sfx);
        if (sc == null)
        {
            return;
        }

        if (sc.loopstart == -1)
        {
            Con_Printf("Sound %s not looped\n", sfx.name);
            return;
        }

        ss.sfx = sfx;
        VectorCopy(origin, ref ss.origin);
        ss.master_vol = (int)vol;
        ss.dist_mult = attenuation / 64 / sound_nominal_clip_dist;
        ss.end = paintedtime + sc.length;

        SND_Spatialize(ss);
    }

    // =============================================================================

    // snd_dma.c: S_UpdateAmbientSounds
    public void S_UpdateAmbientSounds()
    {
        MLeaf? l;
        float vol;
        int ambient_channel;
        Channel chan;

        if (!snd_ambient)
        {
            return;
        }

        // calc ambient sound levels
        if (cl.worldmodel == null)
        {
            return;
        }

        l = Mod_PointInLeaf(listener_origin, cl.worldmodel);
        if (l == null || ambient_level.value == 0)
        {
            for (ambient_channel = 0; ambient_channel < NUM_AMBIENTS; ambient_channel++)
            {
                channels[ambient_channel].sfx = null;
            }

            return;
        }

        for (ambient_channel = 0; ambient_channel < NUM_AMBIENTS; ambient_channel++)
        {
            chan = channels[ambient_channel];
            chan.sfx = ambient_sfx[ambient_channel];

            vol = ambient_level.value * l.ambient_sound_level[ambient_channel];
            if (vol < 8)
            {
                vol = 0;
            }

            // don't adjust volume too fast
            if (chan.master_vol < vol)
            {
                chan.master_vol = (int)(chan.master_vol + (host_frametime * ambient_fade.value));
                if (chan.master_vol > vol)
                {
                    chan.master_vol = (int)vol;
                }
            }
            else if (chan.master_vol > vol)
            {
                chan.master_vol = (int)(chan.master_vol - (host_frametime * ambient_fade.value));
                if (chan.master_vol < vol)
                {
                    chan.master_vol = (int)vol;
                }
            }

            chan.leftvol = chan.rightvol = chan.master_vol;
        }
    }

    // snd_dma.c: S_Update
    // Called once each time through the main loop
    public void S_Update(in Vec3 origin, in Vec3 forward, in Vec3 right, in Vec3 up)
    {
        int i, j;
        int total;
        Channel ch;
        Channel? combine;

        if (sound_started == 0 || snd_blocked > 0)
        {
            return;
        }

        VectorCopy(origin, ref listener_origin);
        VectorCopy(forward, ref listener_forward);
        VectorCopy(right, ref listener_right);
        VectorCopy(up, ref listener_up);

        // update general area ambient sound sources
        S_UpdateAmbientSounds();

        combine = null;

        // update spatialization for static and dynamic sounds
        for (i = NUM_AMBIENTS; i < total_channels; i++)
        {
            ch = channels[i];
            if (ch.sfx == null)
            {
                continue;
            }

            SND_Spatialize(ch); // respatialize channel
            if (ch.leftvol == 0 && ch.rightvol == 0)
            {
                continue;
            }

            // try to combine static sounds with a previous channel of the same sound effect so we don't mix five
            // torches every frame
            if (i >= MAX_DYNAMIC_CHANNELS + NUM_AMBIENTS)
            {
                // see if it can just use the last one
                if (combine != null && combine.sfx == ch.sfx)
                {
                    combine.leftvol += ch.leftvol;
                    combine.rightvol += ch.rightvol;
                    ch.leftvol = ch.rightvol = 0;
                    continue;
                }

                // search for one
                for (j = MAX_DYNAMIC_CHANNELS + NUM_AMBIENTS; j < i; j++)
                {
                    if (channels[j].sfx == ch.sfx)
                    {
                        break;
                    }
                }

                combine = channels[j];

                if (j == total_channels)
                {
                    combine = null;
                }
                else
                {
                    if (combine != ch)
                    {
                        combine.leftvol += ch.leftvol;
                        combine.rightvol += ch.rightvol;
                        ch.leftvol = ch.rightvol = 0;
                    }

                    continue;
                }
            }
        }

        // debugging output
        if (snd_show.value != 0)
        {
            total = 0;
            for (i = 0; i < total_channels; i++)
            {
                ch = channels[i];
                if (ch.sfx != null && (ch.leftvol != 0 || ch.rightvol != 0))
                {
                    total++;
                }
            }

            Con_Printf("----(%i)----\n", total);
        }

        // mix some sound
        S_Update_();
    }

    // snd_dma.c: GetSoundtime
    public void GetSoundtime()
    {
        int samplepos;
        int fullsamples;

        fullsamples = shm!.samples / shm.channels;

        // it is possible to miscount buffers if it has wrapped twice between calls to S_Update. Oh well.
        samplepos = fakedma ? 0 : sounddma!.GetDMAPos();

        if (samplepos < snd_oldsamplepos)
        {
            snd_buffers++; // buffer wrapped

            if (paintedtime > 0x40000000)
            {
                // time to chop things off to avoid 32 bit limits
                snd_buffers = 0;
                paintedtime = fullsamples;
                S_StopAllSounds(true);
            }
        }

        snd_oldsamplepos = samplepos;

        soundtime = unchecked((snd_buffers * fullsamples) + (samplepos / shm.channels));
    }

    // snd_dma.c: S_ExtraUpdate
    public void S_ExtraUpdate()
    {
        IN_Accumulate();

        if (snd_noextraupdate.value != 0)
        {
            return; // don't pollute timings
        }

        S_Update_();
    }

    // snd_dma.c: S_Update_
    public void S_Update_()
    {
        uint endtime;
        int samps;

        if (sound_started == 0 || snd_blocked > 0)
        {
            return;
        }

        // Updates DMA time
        GetSoundtime();

        // check to make sure that we haven't overshot
        if (paintedtime < soundtime)
        {
            paintedtime = soundtime;
        }

        // mix ahead of current position
        endtime = (uint)(soundtime + (_snd_mixahead.value * shm!.speed));
        samps = shm.samples >> (shm.channels - 1);
        if (unchecked(endtime - (uint)soundtime) > (uint)samps)
        {
            endtime = unchecked((uint)(soundtime + samps));
        }

        // the enhanced mode's mixer paints the same span from the channels as they are before this paint moves them
        if (enhanced?.Sound is { } enhancedSound)
        {
            enhancedSound.Listen(listener_origin, listener_forward, listener_right, cl.viewentity);
            enhancedSound.Paint(channels, total_channels, paintedtime, (int)endtime, volume.value);
        }

        S_PaintChannels((int)endtime);

        if (!fakedma)
        {
            sounddma!.Submit();
        }
    }

    // ===============================================================================
    // console functions
    // ===============================================================================

    // snd_dma.c: S_Play
    public void S_Play()
    {
        int i;
        string name;
        Sfx? sfx;

        i = 1;
        while (i < Cmd_Argc())
        {
            if (!Cmd_Argv(i).Contains('.', StringComparison.Ordinal))
            {
                name = Cmd_Argv(i) + ".wav";
            }
            else
            {
                name = Cmd_Argv(i);
            }

            sfx = S_PrecacheSound(name);
            S_StartSound(s_play_hash++, 0, sfx, listener_origin, 1.0f, 1.0f);
            i++;
        }
    }

    // snd_dma.c: S_PlayVol
    public void S_PlayVol()
    {
        int i;
        float vol;
        string name;
        Sfx? sfx;

        i = 1;
        while (i < Cmd_Argc())
        {
            if (!Cmd_Argv(i).Contains('.', StringComparison.Ordinal))
            {
                name = Cmd_Argv(i) + ".wav";
            }
            else
            {
                name = Cmd_Argv(i);
            }

            sfx = S_PrecacheSound(name);
            vol = Q_atof(Cmd_Argv(i + 1));
            S_StartSound(s_playvol_hash++, 0, sfx, listener_origin, vol, 1.0f);
            i += 2;
        }
    }

    // snd_dma.c: S_SoundList
    public void S_SoundList()
    {
        int i;
        Sfx sfx;
        SfxCache? sc;
        int size, total;

        total = 0;
        for (i = 0; i < num_sfx; i++)
        {
            sfx = known_sfx![i];
            sc = sfx.cache;
            if (sc == null)
            {
                continue;
            }

            size = sc.length * sc.width * (sc.stereo + 1);
            total += size;
            if (sc.loopstart >= 0)
            {
                Con_Printf("L");
            }
            else
            {
                Con_Printf(" ");
            }

            Con_Printf("(%2db) %6i : %s\n", sc.width * 8, size, sfx.name);
        }

        Con_Printf("Total resident: %i\n", total);
    }

    // snd_dma.c: S_LocalSound
    public void S_LocalSound(string sound)
    {
        Sfx? sfx;

        if (nosound.value != 0)
        {
            return;
        }

        if (sound_started == 0)
        {
            return;
        }

        sfx = S_PrecacheSound(sound);
        if (sfx == null)
        {
            Con_Printf("S_LocalSound: can't cache %s\n", sound);
            return;
        }

        S_StartSound(cl.viewentity, -1, sfx, vec3_origin, 1, 1);
    }

    // snd_dma.c: S_ClearPrecache
    public static void S_ClearPrecache()
    {
    }

    // snd_dma.c: S_BeginPrecaching
    public static void S_BeginPrecaching()
    {
    }

    // snd_dma.c: S_EndPrecaching
    public static void S_EndPrecaching()
    {
    }
}
