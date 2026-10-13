using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Net;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Model.BspFile;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// sv_main.c: server main program
public sealed partial class QuakeEngine
{
    /// <summary>sv_main.c: sv</summary>
    public ServerState sv = new();

    /// <summary>sv_main.c: svs</summary>
    public readonly ServerStatic svs = new();

    /// <summary>sv_main.c: localmodels (char[MAX_MODELS][5]), inline model names for precache</summary>
    public readonly string[] localmodels = new string[MAX_MODELS];

    // sv_phys.c, sv_user.c, pr_cmds.c: the server's cvars, which SV_Init registers
    public readonly Cvar sv_friction = new("sv_friction", "4", false, true);
    public readonly Cvar sv_stopspeed = new("sv_stopspeed", "100");
    public readonly Cvar sv_gravity = new("sv_gravity", "800", false, true);
    public readonly Cvar sv_maxvelocity = new("sv_maxvelocity", "2000");
    public readonly Cvar sv_nostep = new("sv_nostep", "0");
    public readonly Cvar sv_edgefriction = new("edgefriction", "2");
    public readonly Cvar sv_idealpitchscale = new("sv_idealpitchscale", "0.8");
    public readonly Cvar sv_maxspeed = new("sv_maxspeed", "320", false, true);
    public readonly Cvar sv_accelerate = new("sv_accelerate", "10");
    public readonly Cvar sv_aim = new("sv_aim", "0.93");

    // ============================================================================

    // sv_main.c: SV_Init
    public void SV_Init()
    {
        int i;

        Cvar_RegisterVariable(sv_maxvelocity);
        Cvar_RegisterVariable(sv_gravity);
        Cvar_RegisterVariable(sv_friction);
        Cvar_RegisterVariable(sv_edgefriction);
        Cvar_RegisterVariable(sv_stopspeed);
        Cvar_RegisterVariable(sv_maxspeed);
        Cvar_RegisterVariable(sv_accelerate);
        Cvar_RegisterVariable(sv_idealpitchscale);
        Cvar_RegisterVariable(sv_aim);
        Cvar_RegisterVariable(sv_nostep);

        for (i = 0; i < MAX_MODELS; i++)
        {
            localmodels[i] = CFormat.Sprintf("*%i", i);
        }
    }

    // =============================================================================
    // EVENT MESSAGES
    // =============================================================================

    // sv_main.c: SV_StartParticle
    // Make sure the event gets sent to all clients
    public void SV_StartParticle(in Vec3 org, in Vec3 dir, int color, int count)
    {
        int i, v;

        if (sv.datagram.cursize > MAX_DATAGRAM - 16)
        {
            return;
        }

        MSG_WriteByte(sv.datagram, svc_particle);
        MSG_WriteCoord(sv.datagram, org[0]);
        MSG_WriteCoord(sv.datagram, org[1]);
        MSG_WriteCoord(sv.datagram, org[2]);
        for (i = 0; i < 3; i++)
        {
            v = X86.FtoI(dir[i] * 16);
            if (v > 127)
            {
                v = 127;
            }
            else if (v < -128)
            {
                v = -128;
            }

            MSG_WriteChar(sv.datagram, v);
        }

        MSG_WriteByte(sv.datagram, count);
        MSG_WriteByte(sv.datagram, color);
    }

    // sv_main.c: SV_StartSound
    // Each entity can have eight independant sound sources, like voice, weapon, feet, etc.
    //
    // Channel 0 is an auto-allocate channel, the others override anything allready running on that entity/channel
    // pair.
    //
    // An attenuation of 0 will play full volume everywhere in the level. Larger attenuations will drop off. (max 4
    // attenuation)
    public void SV_StartSound(Edict entity, int channel, string sample, int volume, float attenuation)
    {
        int sound_num;
        int field_mask;
        int i;
        int ent;

        if (volume < 0 || volume > 255)
        {
            throw Sys_Error("SV_StartSound: volume = %i", volume);
        }

        if (attenuation < 0 || attenuation > 4)
        {
            throw Sys_Error("SV_StartSound: attenuation = %f", attenuation);
        }

        if (channel < 0 || channel > 7)
        {
            throw Sys_Error("SV_StartSound: channel = %i", channel);
        }

        if (sv.datagram.cursize > MAX_DATAGRAM - 16)
        {
            return;
        }

        // find precache number for sound
        for (sound_num = 1; sound_num < MAX_SOUNDS && sv.sound_precache[sound_num] != null; sound_num++)
        {
            if (Q_strcmp(sample, sv.sound_precache[sound_num]!) == 0)
            {
                break;
            }
        }

        if (sound_num == MAX_SOUNDS || sv.sound_precache[sound_num] == null)
        {
            Con_Printf("SV_StartSound: %s not precacheed\n", sample);
            return;
        }

        ent = NUM_FOR_EDICT(entity);

        channel = (ent << 3) | channel;

        field_mask = 0;
        if (volume != DEFAULT_SOUND_PACKET_VOLUME)
        {
            field_mask |= SND_VOLUME;
        }

        if (attenuation != DEFAULT_SOUND_PACKET_ATTENUATION)
        {
            field_mask |= SND_ATTENUATION;
        }

        // directed messages go only to the entity the are targeted on
        MSG_WriteByte(sv.datagram, svc_sound);
        MSG_WriteByte(sv.datagram, field_mask);
        if ((field_mask & SND_VOLUME) != 0)
        {
            MSG_WriteByte(sv.datagram, volume);
        }

        if ((field_mask & SND_ATTENUATION) != 0)
        {
            MSG_WriteByte(sv.datagram, X86.FtoI(attenuation * 64));
        }

        MSG_WriteShort(sv.datagram, channel);
        MSG_WriteByte(sv.datagram, sound_num);
        for (i = 0; i < 3; i++)
        {
            MSG_WriteCoord(sv.datagram, (float)(entity.v.origin[i] + (0.5 * (entity.v.mins[i] + entity.v.maxs[i]))));
        }
    }

    // ==============================================================================
    // CLIENT SPAWNING
    // ==============================================================================

    // sv_main.c: SV_SendServerinfo
    // Sends the first message from the server to a connected client. This will be sent on the initial connection and
    // upon each server load.
    public void SV_SendServerinfo(ServerClient client)
    {
        string message;
        int s;

        MSG_WriteByte(client.message, svc_print);
        message = CFormat.Sprintf("%c\nVERSION %4.2f SERVER (%i CRC)", 2, VERSION, (int)pr_crc);
        MSG_WriteString(client.message, message);

        MSG_WriteByte(client.message, svc_serverinfo);
        MSG_WriteLong(client.message, Protocol.PROTOCOL_VERSION);
        MSG_WriteByte(client.message, svs.maxclients);

        if (coop.value == 0 && deathmatch.value != 0)
        {
            MSG_WriteByte(client.message, GAME_DEATHMATCH);
        }
        else
        {
            MSG_WriteByte(client.message, GAME_COOP);
        }

        // sprintf (message, pr_strings+sv.edicts->v.message): the level's message is the format
        message = CFormat.Sprintf(PR_GetString(sv.edicts[0].v.message));

        MSG_WriteString(client.message, message);

        for (s = 1; sv.model_precache[s] != null; s++)
        {
            MSG_WriteString(client.message, sv.model_precache[s]);
        }

        MSG_WriteByte(client.message, 0);

        for (s = 1; sv.sound_precache[s] != null; s++)
        {
            MSG_WriteString(client.message, sv.sound_precache[s]);
        }

        MSG_WriteByte(client.message, 0);

        // send music
        MSG_WriteByte(client.message, svc_cdtrack);
        MSG_WriteByte(client.message, X86.FtoI(sv.edicts[0].v.sounds));
        MSG_WriteByte(client.message, X86.FtoI(sv.edicts[0].v.sounds));

        // set view
        MSG_WriteByte(client.message, svc_setview);
        MSG_WriteShort(client.message, NUM_FOR_EDICT(client.edict!));

        MSG_WriteByte(client.message, svc_signonnum);
        MSG_WriteByte(client.message, 1);

        client.sendsignon = true;
        client.spawned = false; // need prespawn, spawn, etc
    }

    // sv_main.c: SV_ConnectClient
    // Initializes a client_t for a new net connection. This will only be called once for a player each game, not
    // once for each level change.
    public void SV_ConnectClient(int clientnum)
    {
        Edict ent;
        ServerClient client;
        int edictnum;
        QSocket? netconnection;
        int i;
        float[] spawn_parms = new float[NUM_SPAWN_PARMS];

        client = svs.clients[clientnum];

        Con_DPrintf("Client %s connected\n", client.netconnection!.address);

        edictnum = clientnum + 1;

        ent = EDICT_NUM(edictnum);

        // set up the client_t
        netconnection = client.netconnection;

        if (sv.loadgame)
        {
            Array.Copy(client.spawn_parms, spawn_parms, NUM_SPAWN_PARMS);
        }

        client.Clear();
        client.netconnection = netconnection;

        client.name = "unconnected";
        client.active = true;
        client.spawned = false;
        client.edict = ent;
        client.message = new SizeBuf(MAX_MSGLEN)
        {
            allowoverflow = true, // we can catch it
        };

        client.privileged = false;

        if (sv.loadgame)
        {
            Array.Copy(spawn_parms, client.spawn_parms, NUM_SPAWN_PARMS);
        }
        else
        {
            // call the progs to get default spawn parms for the new client
            PR_ExecuteProgram(pr_global_struct.SetNewParms);
            for (i = 0; i < NUM_SPAWN_PARMS; i++)
            {
                client.spawn_parms[i] = Parm(i);
            }
        }

        SV_SendServerinfo(client);
    }

    // sv_main.c: SV_CheckForNewClients
    public void SV_CheckForNewClients()
    {
        QSocket? ret;
        int i;

        // check for new connections
        while (true)
        {
            ret = NET_CheckNewConnections();
            if (ret == null)
            {
                break;
            }

            // init a new client structure
            for (i = 0; i < svs.maxclients; i++)
            {
                if (!svs.clients[i].active)
                {
                    break;
                }
            }

            if (i == svs.maxclients)
            {
                throw Sys_Error("Host_CheckForNewClients: no free clients");
            }

            svs.clients[i].netconnection = ret;
            SV_ConnectClient(i);

            net_activeconnections++;
        }
    }

    // ===============================================================================
    // FRAME UPDATES
    // ===============================================================================

    // sv_main.c: SV_ClearDatagram
    public void SV_ClearDatagram() => SZ_Clear(sv.datagram);

    // =============================================================================
    // The PVS must include a small area around the client to allow head bobbing or other small motion on the client
    // side. Otherwise, a bob might cause an entity that should be visible to not show up, especially when the bob
    // crosses a waterline.
    // =============================================================================

    private int fatbytes;
    private readonly byte[] fatpvs = new byte[MAX_MAP_LEAFS / 8];

    // sv_main.c: SV_AddToFatPVS
    public void SV_AddToFatPVS(in Vec3 org, MNodeBase node)
    {
        int i;
        byte[] pvs;
        MPlane plane;
        float d;

        while (true)
        {
            // if this is a leaf, accumulate the pvs bits
            if (node.contents < 0)
            {
                if (node.contents != CONTENTS_SOLID)
                {
                    pvs = Mod_LeafPVS((MLeaf)node, sv.worldmodel!);
                    for (i = 0; i < fatbytes; i++)
                    {
                        fatpvs[i] |= pvs[i];
                    }
                }

                return;
            }

            var mnode = (MNode)node;
            plane = mnode.plane;
            d = DotProduct(org, plane.normal) - plane.dist;
            if (d > 8)
            {
                node = mnode.children[0];
            }
            else if (d < -8)
            {
                node = mnode.children[1];
            }
            else
            {
                // go down both
                SV_AddToFatPVS(org, mnode.children[0]);
                node = mnode.children[1];
            }
        }
    }

    // sv_main.c: SV_FatPVS
    // Calculates a PVS that is the inclusive or of all leafs within 8 pixels of the given point.
    public byte[] SV_FatPVS(in Vec3 org)
    {
        fatbytes = (sv.worldmodel!.numleafs + 31) >> 3;
        Array.Clear(fatpvs, 0, fatbytes);
        SV_AddToFatPVS(org, sv.worldmodel.nodes[0]);
        return fatpvs;
    }

    // =============================================================================

    // sv_main.c: SV_WriteEntitiesToClient
    public void SV_WriteEntitiesToClient(Edict clent, SizeBuf msg)
    {
        int e, i;
        int bits;
        byte[] pvs;
        Vec3 org = default;
        float miss;
        Edict ent;

        // find the client's PVS
        VectorAdd(clent.v.origin, clent.v.view_ofs, ref org);
        pvs = SV_FatPVS(org);

        // send over all entities (excpet the client) that touch the pvs
        ent = NEXT_EDICT(sv.edicts[0]);
        for (e = 1; e < sv.num_edicts; e++, ent = NEXT_EDICT(ent))
        {
            // ignore if not touching a PV leaf
            if (ent != clent)
            {
                // clent is ALLWAYS sent

                // ignore ents without visible models
                if (ent.v.modelindex == 0 || PR_GetString(ent.v.model).Length == 0)
                {
                    continue;
                }

                for (i = 0; i < ent.num_leafs; i++)
                {
                    if ((pvs[ent.leafnums[i] >> 3] & (1 << (ent.leafnums[i] & 7))) != 0)
                    {
                        break;
                    }
                }

                if (i == ent.num_leafs)
                {
                    continue; // not visible
                }
            }

            if (msg.maxsize - msg.cursize < 16)
            {
                Con_Printf("packet overflow\n");
                return;
            }

            // send an update
            bits = 0;

            for (i = 0; i < 3; i++)
            {
                miss = ent.v.origin[i] - ent.baseline.origin[i];
                if (miss < -0.1 || miss > 0.1)
                {
                    bits |= U_ORIGIN1 << i;
                }
            }

            if (ent.v.angles[0] != ent.baseline.angles[0])
            {
                bits |= U_ANGLE1;
            }

            if (ent.v.angles[1] != ent.baseline.angles[1])
            {
                bits |= U_ANGLE2;
            }

            if (ent.v.angles[2] != ent.baseline.angles[2])
            {
                bits |= U_ANGLE3;
            }

            if (ent.v.movetype == MOVETYPE_STEP)
            {
                bits |= U_NOLERP; // don't mess up the step animation
            }

            if (ent.baseline.colormap != ent.v.colormap)
            {
                bits |= U_COLORMAP;
            }

            if (ent.baseline.skin != ent.v.skin)
            {
                bits |= U_SKIN;
            }

            if (ent.baseline.frame != ent.v.frame)
            {
                bits |= U_FRAME;
            }

            if (ent.baseline.effects != ent.v.effects)
            {
                bits |= U_EFFECTS;
            }

            if (ent.baseline.modelindex != ent.v.modelindex)
            {
                bits |= U_MODEL;
            }

            if (e >= 256)
            {
                bits |= U_LONGENTITY;
            }

            if (bits >= 256)
            {
                bits |= U_MOREBITS;
            }

            // write the message
            MSG_WriteByte(msg, bits | U_SIGNAL);

            if ((bits & U_MOREBITS) != 0)
            {
                MSG_WriteByte(msg, bits >> 8);
            }

            if ((bits & U_LONGENTITY) != 0)
            {
                MSG_WriteShort(msg, e);
            }
            else
            {
                MSG_WriteByte(msg, e);
            }

            if ((bits & U_MODEL) != 0)
            {
                MSG_WriteByte(msg, X86.FtoI(ent.v.modelindex));
            }

            if ((bits & U_FRAME) != 0)
            {
                MSG_WriteByte(msg, X86.FtoI(ent.v.frame));
            }

            if ((bits & U_COLORMAP) != 0)
            {
                MSG_WriteByte(msg, X86.FtoI(ent.v.colormap));
            }

            if ((bits & U_SKIN) != 0)
            {
                MSG_WriteByte(msg, X86.FtoI(ent.v.skin));
            }

            if ((bits & U_EFFECTS) != 0)
            {
                MSG_WriteByte(msg, X86.FtoI(ent.v.effects));
            }

            if ((bits & U_ORIGIN1) != 0)
            {
                MSG_WriteCoord(msg, ent.v.origin[0]);
            }

            if ((bits & U_ANGLE1) != 0)
            {
                MSG_WriteAngle(msg, ent.v.angles[0]);
            }

            if ((bits & U_ORIGIN2) != 0)
            {
                MSG_WriteCoord(msg, ent.v.origin[1]);
            }

            if ((bits & U_ANGLE2) != 0)
            {
                MSG_WriteAngle(msg, ent.v.angles[1]);
            }

            if ((bits & U_ORIGIN3) != 0)
            {
                MSG_WriteCoord(msg, ent.v.origin[2]);
            }

            if ((bits & U_ANGLE3) != 0)
            {
                MSG_WriteAngle(msg, ent.v.angles[2]);
            }
        }
    }

    // sv_main.c: SV_CleanupEnts
    public void SV_CleanupEnts()
    {
        int e;
        Edict ent;

        ent = NEXT_EDICT(sv.edicts[0]);
        for (e = 1; e < sv.num_edicts; e++, ent = NEXT_EDICT(ent))
        {
            ent.v.effects = X86.FtoI(ent.v.effects) & ~EF_MUZZLEFLASH;
        }
    }

    // sv_main.c: SV_WriteClientdataToMessage
    public void SV_WriteClientdataToMessage(Edict ent, SizeBuf msg)
    {
        int bits;
        int i;
        Edict other;
        int items;
        int val;

        // send a damage message
        if (ent.v.dmg_take != 0 || ent.v.dmg_save != 0)
        {
            other = PROG_TO_EDICT(ent.v.dmg_inflictor);
            MSG_WriteByte(msg, svc_damage);
            MSG_WriteByte(msg, X86.FtoI(ent.v.dmg_save));
            MSG_WriteByte(msg, X86.FtoI(ent.v.dmg_take));
            for (i = 0; i < 3; i++)
            {
                MSG_WriteCoord(msg, (float)(other.v.origin[i] + (0.5 * (other.v.mins[i] + other.v.maxs[i]))));
            }

            ent.v.dmg_take = 0;
            ent.v.dmg_save = 0;
        }

        // send the current viewpos offset from the view entity
        SV_SetIdealPitch(); // how much to look up / down ideally

        // a fixangle might get lost in a dropped packet. Oh well.
        if (ent.v.fixangle != 0)
        {
            MSG_WriteByte(msg, svc_setangle);
            for (i = 0; i < 3; i++)
            {
                MSG_WriteAngle(msg, ent.v.angles[i]);
            }

            ent.v.fixangle = 0;
        }

        bits = 0;

        if (ent.v.view_ofs[2] != DEFAULT_VIEWHEIGHT)
        {
            bits |= SU_VIEWHEIGHT;
        }

        if (ent.v.idealpitch != 0)
        {
            bits |= SU_IDEALPITCH;
        }

        // stuff the sigil bits into the high bits of items for sbar, or else mix in items2
        val = GetEdictFieldValue(ent, "items2");

        if (val >= 0)
        {
            items = X86.FtoI(ent.v.items) | (X86.FtoI(BitConverter.Int32BitsToSingle(sv.edictmem[val])) << 23);
        }
        else
        {
            items = X86.FtoI(ent.v.items) | (X86.FtoI(pr_global_struct.serverflags) << 28);
        }

        bits |= SU_ITEMS;

        if ((X86.FtoI(ent.v.flags) & FL_ONGROUND) != 0)
        {
            bits |= SU_ONGROUND;
        }

        if (ent.v.waterlevel >= 2)
        {
            bits |= SU_INWATER;
        }

        for (i = 0; i < 3; i++)
        {
            if (ent.v.punchangle[i] != 0)
            {
                bits |= SU_PUNCH1 << i;
            }

            if (ent.v.velocity[i] != 0)
            {
                bits |= SU_VELOCITY1 << i;
            }
        }

        if (ent.v.weaponframe != 0)
        {
            bits |= SU_WEAPONFRAME;
        }

        if (ent.v.armorvalue != 0)
        {
            bits |= SU_ARMOR;
        }

        // if (ent->v.weapon)
        bits |= SU_WEAPON;

        // send the data
        MSG_WriteByte(msg, svc_clientdata);
        MSG_WriteShort(msg, bits);

        if ((bits & SU_VIEWHEIGHT) != 0)
        {
            MSG_WriteChar(msg, X86.FtoI(ent.v.view_ofs[2]));
        }

        if ((bits & SU_IDEALPITCH) != 0)
        {
            MSG_WriteChar(msg, X86.FtoI(ent.v.idealpitch));
        }

        for (i = 0; i < 3; i++)
        {
            if ((bits & (SU_PUNCH1 << i)) != 0)
            {
                MSG_WriteChar(msg, X86.FtoI(ent.v.punchangle[i]));
            }

            if ((bits & (SU_VELOCITY1 << i)) != 0)
            {
                MSG_WriteChar(msg, X86.FtoI(ent.v.velocity[i] / 16));
            }
        }

        // [always sent]	if (bits & SU_ITEMS)
        MSG_WriteLong(msg, items);

        if ((bits & SU_WEAPONFRAME) != 0)
        {
            MSG_WriteByte(msg, X86.FtoI(ent.v.weaponframe));
        }

        if ((bits & SU_ARMOR) != 0)
        {
            MSG_WriteByte(msg, X86.FtoI(ent.v.armorvalue));
        }

        if ((bits & SU_WEAPON) != 0)
        {
            MSG_WriteByte(msg, SV_ModelIndex(PR_GetString(ent.v.weaponmodel)));
        }

        MSG_WriteShort(msg, X86.FtoI(ent.v.health));
        MSG_WriteByte(msg, X86.FtoI(ent.v.currentammo));
        MSG_WriteByte(msg, X86.FtoI(ent.v.ammo_shells));
        MSG_WriteByte(msg, X86.FtoI(ent.v.ammo_nails));
        MSG_WriteByte(msg, X86.FtoI(ent.v.ammo_rockets));
        MSG_WriteByte(msg, X86.FtoI(ent.v.ammo_cells));

        if (com_argv.standard_quake)
        {
            MSG_WriteByte(msg, X86.FtoI(ent.v.weapon));
        }
        else
        {
            for (i = 0; i < 32; i++)
            {
                if ((X86.FtoI(ent.v.weapon) & (1 << i)) != 0)
                {
                    MSG_WriteByte(msg, i);
                    break;
                }
            }
        }
    }

    // sv_main.c: SV_SendClientDatagram
    public bool SV_SendClientDatagram(ServerClient client)
    {
        var msg = new SizeBuf(MAX_DATAGRAM);

        MSG_WriteByte(msg, svc_time);
        MSG_WriteFloat(msg, (float)sv.time);

        // add the client specific data to the datagram
        SV_WriteClientdataToMessage(client.edict!, msg);

        SV_WriteEntitiesToClient(client.edict!, msg);

        // copy the server datagram if there is space
        if (msg.cursize + sv.datagram.cursize < msg.maxsize)
        {
            SZ_Write(msg, sv.datagram.data.AsSpan(0, sv.datagram.cursize));
        }

        // send the datagram
        if (NET_SendUnreliableMessage(client.netconnection, msg) == -1)
        {
            SV_DropClient(true); // if the message couldn't send, kick off
            return false;
        }

        return true;
    }

    // sv_main.c: SV_UpdateToReliableMessages
    public void SV_UpdateToReliableMessages()
    {
        int i, j;
        ServerClient client;

        // check for changes to be sent over the reliable streams
        for (i = 0, host_client = svs.Client(0); i < svs.maxclients; i++, host_client = svs.Client(i))
        {
            if (host_client.old_frags != host_client.edict!.v.frags)
            {
                for (j = 0; j < svs.maxclients; j++)
                {
                    client = svs.clients[j];
                    if (!client.active)
                    {
                        continue;
                    }

                    MSG_WriteByte(client.message, svc_updatefrags);
                    MSG_WriteByte(client.message, i);
                    MSG_WriteShort(client.message, X86.FtoI(host_client.edict.v.frags));
                }

                host_client.old_frags = X86.FtoI(host_client.edict.v.frags);
            }
        }

        for (j = 0; j < svs.maxclients; j++)
        {
            client = svs.clients[j];
            if (!client.active)
            {
                continue;
            }

            SZ_Write(client.message, sv.reliable_datagram.data.AsSpan(0, sv.reliable_datagram.cursize));
        }

        SZ_Clear(sv.reliable_datagram);
    }

    // sv_main.c: SV_SendNop
    // Send a nop message without trashing or sending the accumulated client message buffer
    public void SV_SendNop(ServerClient client)
    {
        var msg = new SizeBuf(4) { maxsize = 4 };

        MSG_WriteChar(msg, svc_nop);

        if (NET_SendUnreliableMessage(client.netconnection, msg) == -1)
        {
            SV_DropClient(true); // if the message couldn't send, kick off
        }

        client.last_message = realtime;
    }

    // sv_main.c: SV_SendClientMessages
    public void SV_SendClientMessages()
    {
        int i;

        // update frags, names, etc
        SV_UpdateToReliableMessages();

        // build individual updates
        for (i = 0, host_client = svs.Client(0); i < svs.maxclients; i++, host_client = svs.Client(i))
        {
            if (!host_client.active)
            {
                continue;
            }

            if (host_client.spawned)
            {
                if (!SV_SendClientDatagram(host_client))
                {
                    continue;
                }
            }
            else
            {
                // the player isn't totally in the game yet
                // send small keepalive messages if too much time has passed
                // send a full message when the next signon stage has been requested
                // some other message data (name changes, etc) may accumulate between signon stages
                if (!host_client.sendsignon)
                {
                    if (realtime - host_client.last_message > 5)
                    {
                        SV_SendNop(host_client);
                    }

                    continue; // don't send out non-signon messages
                }
            }

            // check for an overflowed message. Should only happen on a very fucked up connection that backs up a lot,
            // then changes level
            if (host_client.message.overflowed)
            {
                SV_DropClient(true);
                host_client.message.overflowed = false;
                continue;
            }

            if (host_client.message.cursize != 0 || host_client.dropasap)
            {
                if (!NET_CanSendMessage(host_client.netconnection))
                {
                    continue;
                }

                if (host_client.dropasap)
                {
                    SV_DropClient(false); // went to another level
                }
                else
                {
                    if (NET_SendMessage(host_client.netconnection, host_client.message) == -1)
                    {
                        SV_DropClient(true); // if the message couldn't send, kick off
                    }

                    SZ_Clear(host_client.message);
                    host_client.last_message = realtime;
                    host_client.sendsignon = false;
                }
            }
        }

        // clear muzzle flashes
        SV_CleanupEnts();
    }

    // ==============================================================================
    // SERVER SPAWNING
    // ==============================================================================

    // sv_main.c: SV_ModelIndex
    public int SV_ModelIndex(string? name)
    {
        int i;

        if (name == null || Ch(name, 0) == 0)
        {
            return 0;
        }

        for (i = 0; i < MAX_MODELS && sv.model_precache[i] != null; i++)
        {
            if (Q_strcmp(sv.model_precache[i]!, name) == 0)
            {
                return i;
            }
        }

        if (i == MAX_MODELS || sv.model_precache[i] == null)
        {
            throw Sys_Error("SV_ModelIndex: model %s not precached", name);
        }

        return i;
    }

    // sv_main.c: SV_CreateBaseline
    public void SV_CreateBaseline()
    {
        int i;
        Edict svent;
        int entnum;

        for (entnum = 0; entnum < sv.num_edicts; entnum++)
        {
            // get the current server version
            svent = EDICT_NUM(entnum);
            if (svent.free)
            {
                continue;
            }

            if (entnum > svs.maxclients && svent.v.modelindex == 0)
            {
                continue;
            }

            // create entity baseline
            VectorCopy(svent.v.origin, ref svent.baseline.origin);
            VectorCopy(svent.v.angles, ref svent.baseline.angles);
            svent.baseline.frame = X86.FtoI(svent.v.frame);
            svent.baseline.skin = X86.FtoI(svent.v.skin);
            if (entnum > 0 && entnum <= svs.maxclients)
            {
                svent.baseline.colormap = entnum;
                svent.baseline.modelindex = SV_ModelIndex("progs/player.mdl");
            }
            else
            {
                svent.baseline.colormap = 0;
                svent.baseline.modelindex = SV_ModelIndex(PR_GetString(svent.v.model));
            }

            // add to the message
            MSG_WriteByte(sv.signon, svc_spawnbaseline);
            MSG_WriteShort(sv.signon, entnum);

            MSG_WriteByte(sv.signon, svent.baseline.modelindex);
            MSG_WriteByte(sv.signon, svent.baseline.frame);
            MSG_WriteByte(sv.signon, svent.baseline.colormap);
            MSG_WriteByte(sv.signon, svent.baseline.skin);
            for (i = 0; i < 3; i++)
            {
                MSG_WriteCoord(sv.signon, svent.baseline.origin[i]);
                MSG_WriteAngle(sv.signon, svent.baseline.angles[i]);
            }
        }
    }

    // sv_main.c: SV_SendReconnect
    // Tell all the clients that the server is changing levels
    public void SV_SendReconnect()
    {
        var msg = new SizeBuf(128) { maxsize = 128 };

        MSG_WriteChar(msg, svc_stufftext);
        MSG_WriteString(msg, "reconnect\n");
        NET_SendToAll(msg, 5);

        if (cls.state != cactive_t.ca_dedicated)
        {
            Cmd_ExecuteString("reconnect\n", cmd_source_t.src_command);
        }
    }

    // sv_main.c: SV_SaveSpawnparms
    // Grabs the current state of each client for saving across the transition to another level
    public void SV_SaveSpawnparms()
    {
        int i, j;

        svs.serverflags = X86.FtoI(pr_global_struct.serverflags);

        for (i = 0, host_client = svs.Client(0); i < svs.maxclients; i++, host_client = svs.Client(i))
        {
            if (!host_client.active)
            {
                continue;
            }

            // call the progs to get default spawn parms for the new client
            pr_global_struct.self = EDICT_TO_PROG(host_client.edict!);
            PR_ExecuteProgram(pr_global_struct.SetChangeParms);
            for (j = 0; j < NUM_SPAWN_PARMS; j++)
            {
                host_client.spawn_parms[j] = Parm(j);
            }
        }
    }

    // sv_main.c: SV_SpawnServer
    // This is called at the start of each level
    public void SV_SpawnServer(string server)
    {
        Edict ent;
        int i;

        // let's not have any servers with no name
        if (Ch(hostname.@string, 0) == 0)
        {
            Cvar_Set("hostname", "UNNAMED");
        }

        scr_centertime_off = 0;

        Con_DPrintf("SpawnServer: %s\n", server);
        svs.changelevel_issued = false; // now safe to issue another

        // tell all connected clients that we are going to a new level
        if (sv.active)
        {
            SV_SendReconnect();
        }

        // make cvars consistant
        if (coop.value != 0)
        {
            Cvar_SetValue("deathmatch", 0);
        }

        current_skill = X86.FtoI(skill.value + 0.5);
        if (current_skill < 0)
        {
            current_skill = 0;
        }

        if (current_skill > 3)
        {
            current_skill = 3;
        }

        Cvar_SetValue("skill", current_skill);

        // set up the new server
        Host_ClearMemory();

        sv = new ServerState
        {
            name = server,
        };

        // load progs to get entity field count
        PR_LoadProgs();

        // allocate server memory
        sv.max_edicts = MAX_EDICTS;

        sv.edictmem = new int[sv.max_edicts * pr_edict_size / 4];
        sv.edicts = new Edict[sv.max_edicts];
        for (i = 0; i < sv.max_edicts; i++)
        {
            sv.edicts[i] = new Edict(i, sv.edictmem, ((i * pr_edict_size) + Edict.HeaderSize) / 4);
        }

        sv.datagram = new SizeBuf(MAX_DATAGRAM);
        sv.reliable_datagram = new SizeBuf(MAX_DATAGRAM);
        sv.signon = new SizeBuf(8192);

        // leave slots at start for clients only
        sv.num_edicts = svs.maxclients + 1;
        for (i = 0; i < svs.maxclients; i++)
        {
            ent = EDICT_NUM(i + 1);
            svs.clients[i].edict = ent;
        }

        sv.state = server_state_t.ss_loading;
        sv.paused = false;

        sv.time = 1.0;

        sv.name = server;
        sv.modelname = CFormat.Sprintf("maps/%s.bsp", server);
        sv.worldmodel = Mod_ForName(sv.modelname, false);
        if (sv.worldmodel == null)
        {
            Con_Printf("Couldn't spawn server %s\n", sv.modelname);
            sv.active = false;
            return;
        }

        sv.models[1] = sv.worldmodel;

        // clear world interaction links
        SV_ClearWorld();

        sv.sound_precache[0] = PR_GetString(0);

        sv.model_precache[0] = PR_GetString(0);
        sv.model_precache[1] = sv.modelname;
        for (i = 1; i < sv.worldmodel.numsubmodels; i++)
        {
            sv.model_precache[1 + i] = localmodels[i];
            sv.models[i + 1] = Mod_ForName(localmodels[i], false);
        }

        // load the rest of the entities
        ent = EDICT_NUM(0);
        Array.Clear(sv.edictmem, ent.fields, progs!.entityfields);
        ent.free = false;
        ent.v.model = PR_SetString(sv.worldmodel.name);
        ent.v.modelindex = 1; // world model
        ent.v.solid = SOLID_BSP;
        ent.v.movetype = MOVETYPE_PUSH;

        if (coop.value != 0)
        {
            pr_global_struct.coop = coop.value;
        }
        else
        {
            pr_global_struct.deathmatch = deathmatch.value;
        }

        pr_global_struct.mapname = PR_SetString(sv.name);

        // serverflags are for cross level information (sigils)
        pr_global_struct.serverflags = svs.serverflags;

        ED_LoadFromFile(sv.worldmodel.entities!);

        sv.active = true;

        // all setup is completed, any further precache statements are errors
        sv.state = server_state_t.ss_active;

        // run two frames to allow everything to settle
        host_frametime = 0.1;
        SV_Physics();
        SV_Physics();

        // create a baseline for more efficient communications
        SV_CreateBaseline();

        // send serverinfo to all connected clients
        for (i = 0, host_client = svs.Client(0); i < svs.maxclients; i++, host_client = svs.Client(i))
        {
            if (host_client.active)
            {
                SV_SendServerinfo(host_client);
            }
        }

        Con_DPrintf("Server spawned.\n");
    }
}
