/*
 * trace.c: writes the oracle's traces, one block of lines per host frame (the format is in tools/oracle/README.md),
 * and plays scripted input (-traceevents). QuakeSharp writes the same lines; every field hashed here is hashed there
 * in the same order, as FNV-1a over its bytes (floats and doubles as their bits, strings without their 0, pointers
 * as what they point at).
 */
#include "quakedef.h"
#include "r_local.h"
#include "trace.h"

extern double oracle_time;
extern double oracle_step;
extern unsigned int oracle_rand_calls;
unsigned int oracle_rand_seed (void);
extern byte vid_curpal[256*3];
extern particle_t *active_particles;
extern channel_t channels[MAX_CHANNELS];
extern int total_channels;
extern int paintedtime;
extern int soundtime;
extern int con_linewidth, con_current, con_x;
extern char *con_text;
#define NUM_CON_TIMES 4
#define CON_TEXTSIZE 16384
extern float con_times[NUM_CON_TIMES];
char *va (char *format, ...);
int Oracle_AreaLinks (int *out, int max);	// world.c, as quake-oracle.patch adds it

static FILE *trace;
static char trace_name[MAX_OSPATH];
static int trace_frame;

// -traceshot <frame>: the frames written as <trace>.<frame>.shot (width, height, palette, pixels)
#define MAX_SHOTS 64
static int shots[MAX_SHOTS], num_shots;

// -tracedump <frame>: that frame's entities field by field as <trace>.<frame>.dump (floats as their bits)
static int dumps[MAX_SHOTS], num_dumps;
static int quit_after_demo, was_playing, frame_limit;
static unsigned long long palette_hash;

// -traceevents: lines of "<frame> <console text>", run before that frame's commands, or "<frame> @key <key> <down>"
static FILE *events;
static int next_event_frame = -1;
static char next_event[1024];

// the M line: the messages sent during the frame (and, for -tracedump, their text)
static unsigned long long message_hash;
static int message_count;
static char *message_log;
static int message_log_length, message_log_size;

/*
===============================================================================

HASHES

===============================================================================
*/

typedef unsigned long long hash_t;

#define FNV_OFFSET 0xcbf29ce484222325ULL
#define FNV_PRIME 0x100000001b3ULL

static void H_Bytes (hash_t *h, const void *data, int length)
{
	const unsigned char *p = data;
	int i;

	for (i = 0; i < length; i++)
	{
		*h ^= p[i];
		*h *= FNV_PRIME;
	}
}

static void H_Int (hash_t *h, int v) { H_Bytes (h, &v, 4); }
static void H_Float (hash_t *h, float v) { H_Bytes (h, &v, 4); }
static void H_Double (hash_t *h, double v) { H_Bytes (h, &v, 8); }
static void H_Vec (hash_t *h, const float *v) { H_Bytes (h, v, 12); }

static void H_String (hash_t *h, const char *s)
{
	H_Bytes (h, s, strlen (s));
	H_Int (h, 0);	// ends the string, so "a" "b" and "ab" differ
}

static void H_Model (hash_t *h, model_t *m)
{
	H_String (h, m ? m->name : "");
}

// a colormap: 0 the normal one, 1 + n player n's translation, -1 anything else
static void H_Colormap (hash_t *h, byte *colormap)
{
	int i;

	if (colormap == vid.colormap)
	{
		H_Int (h, 0);
		return;
	}
	for (i = 0; i < cl.maxclients && cl.scores; i++)
	{
		if (colormap == cl.scores[i].translations)
		{
			H_Int (h, 1 + i);
			return;
		}
	}
	H_Int (h, -1);
}

static void H_EntityState (hash_t *h, entity_state_t *s)
{
	H_Vec (h, s->origin);
	H_Vec (h, s->angles);
	H_Int (h, s->modelindex);
	H_Int (h, s->frame);
	H_Int (h, s->colormap);
	H_Int (h, s->skin);
	H_Int (h, s->effects);
}

static void H_Entity (hash_t *h, entity_t *e)
{
	H_Int (h, e->forcelink);
	H_Int (h, e->update_type);
	H_EntityState (h, &e->baseline);
	H_Double (h, e->msgtime);
	H_Vec (h, e->msg_origins[0]);
	H_Vec (h, e->msg_origins[1]);
	H_Vec (h, e->origin);
	H_Vec (h, e->msg_angles[0]);
	H_Vec (h, e->msg_angles[1]);
	H_Vec (h, e->angles);
	H_Model (h, e->model);
	H_Int (h, e->frame);
	H_Float (h, e->syncbase);
	H_Colormap (h, e->colormap);
	H_Int (h, e->effects);
	H_Int (h, e->skinnum);
}

// what the renderer writes into an entity (the D line), apart from what the client keeps in it
static void H_EntityDrawn (hash_t *h, entity_t *e)
{
	H_Int (h, e->visframe);
	H_Int (h, e->dlightframe);
	H_Int (h, e->dlightbits);
	H_Int (h, e->trivial_accept);
	// R_SplitEntityOnNode and R_SplitEntityOnNode2 can stop at a leaf: its index in the leafs, 0x10000 on
	if (!e->topnode || !cl.worldmodel)
		H_Int (h, -1);
	else if (e->topnode->contents < 0)
		H_Int (h, 0x10000 + (int)((mleaf_t *)e->topnode - cl.worldmodel->leafs));
	else
		H_Int (h, (int)(e->topnode - cl.worldmodel->nodes));
}

// an entity pointer: which array it is in and where
static void H_EntityRef (hash_t *h, entity_t *e)
{
	if (e >= cl_entities && e < cl_entities + MAX_EDICTS)
		H_Int (h, (int)(e - cl_entities));
	else if (e >= cl_static_entities && e < cl_static_entities + MAX_STATIC_ENTITIES)
		H_Int (h, 0x10000 + (int)(e - cl_static_entities));
	else if (e >= cl_temp_entities && e < cl_temp_entities + MAX_TEMP_ENTITIES)
		H_Int (h, 0x20000 + (int)(e - cl_temp_entities));
	else if (e == &cl.viewent)
		H_Int (h, 0x30000);
	else
		H_Int (h, -1);
}

static void H_Cshift (hash_t *h, cshift_t *c)
{
	H_Int (h, c->destcolor[0]);
	H_Int (h, c->destcolor[1]);
	H_Int (h, c->destcolor[2]);
	H_Int (h, c->percent);
}

/*
===============================================================================

THE LINES

===============================================================================
*/

// C: the client state (cls, cl)
static hash_t Client (void)
{
	hash_t h = FNV_OFFSET;
	int i;

	H_Int (&h, cls.state);
	H_Int (&h, cls.signon);
	H_Int (&h, cls.demoplayback);
	H_Int (&h, cls.demonum);

	H_Int (&h, cl.movemessages);
	H_Vec (&h, cl.cmd.viewangles);
	H_Float (&h, cl.cmd.forwardmove);
	H_Float (&h, cl.cmd.sidemove);
	H_Float (&h, cl.cmd.upmove);
	for (i = 0; i < MAX_CL_STATS; i++)
		H_Int (&h, cl.stats[i]);
	H_Int (&h, cl.items);
	for (i = 0; i < 32; i++)
		H_Float (&h, cl.item_gettime[i]);
	H_Float (&h, cl.faceanimtime);
	for (i = 0; i < NUM_CSHIFTS; i++)
		H_Cshift (&h, &cl.cshifts[i]);
	for (i = 0; i < NUM_CSHIFTS; i++)
		H_Cshift (&h, &cl.prev_cshifts[i]);
	H_Vec (&h, cl.mviewangles[0]);
	H_Vec (&h, cl.mviewangles[1]);
	H_Vec (&h, cl.viewangles);
	H_Vec (&h, cl.mvelocity[0]);
	H_Vec (&h, cl.mvelocity[1]);
	H_Vec (&h, cl.velocity);
	H_Vec (&h, cl.punchangle);
	H_Float (&h, cl.idealpitch);
	H_Float (&h, cl.pitchvel);
	H_Int (&h, cl.nodrift);
	H_Float (&h, cl.driftmove);
	H_Double (&h, cl.laststop);
	H_Float (&h, cl.viewheight);
	H_Float (&h, cl.crouch);
	H_Int (&h, cl.paused);
	H_Int (&h, cl.onground);
	H_Int (&h, cl.inwater);
	H_Int (&h, cl.intermission);
	H_Int (&h, cl.completed_time);
	H_Double (&h, cl.mtime[0]);
	H_Double (&h, cl.mtime[1]);
	H_Double (&h, cl.time);
	H_Double (&h, cl.oldtime);
	H_Float (&h, cl.last_received_message);
	H_String (&h, cl.levelname);
	H_Int (&h, cl.viewentity);
	H_Int (&h, cl.maxclients);
	H_Int (&h, cl.gametype);
	H_Model (&h, cl.worldmodel);
	H_Int (&h, cl.num_entities);
	H_Int (&h, cl.num_statics);
	H_Int (&h, cl.cdtrack);
	H_Int (&h, cl.looptrack);
	for (i = 0; i < cl.maxclients && cl.scores; i++)
	{
		H_String (&h, cl.scores[i].name);
		H_Float (&h, cl.scores[i].entertime);
		H_Int (&h, cl.scores[i].frags);
		H_Int (&h, cl.scores[i].colors);
	}
	return h;
}

static int IsStatic (entity_t *e)
{
	return e >= cl_static_entities && e < cl_static_entities + MAX_STATIC_ENTITIES;
}

// E: the entities (cl_entities up to cl.num_entities, the statics, the view model, the visible list)
static hash_t Entities (void)
{
	hash_t h = FNV_OFFSET;
	int i, n;

	for (i = 0; i < cl.num_entities; i++)
		H_Entity (&h, &cl_entities[i]);
	for (i = 0; i < cl.num_statics; i++)
		H_Entity (&h, &cl_static_entities[i]);
	H_Entity (&h, &cl.viewent);
	// the visible entities the client listed (the renderer adds the static ones as it finds them: the D line)
	for (i = 0, n = 0; i < cl_numvisedicts; i++)
		if (!IsStatic (cl_visedicts[i]))
			n++;
	H_Int (&h, n);
	for (i = 0; i < cl_numvisedicts; i++)
		if (!IsStatic (cl_visedicts[i]))
			H_EntityRef (&h, cl_visedicts[i]);
	return h;
}

// D: what the renderer writes into the entities (cl_entities, the statics, the view model, the temp entities) and
// the visible list it completes
static hash_t Drawn (void)
{
	hash_t h = FNV_OFFSET;
	int i;

	for (i = 0; i < cl.num_entities; i++)
		H_EntityDrawn (&h, &cl_entities[i]);
	for (i = 0; i < cl.num_statics; i++)
		H_EntityDrawn (&h, &cl_static_entities[i]);
	H_EntityDrawn (&h, &cl.viewent);
	for (i = 0; i < MAX_TEMP_ENTITIES; i++)
		H_EntityDrawn (&h, &cl_temp_entities[i]);
	// the visible list, with the static entities R_StoreEfrags added
	H_Int (&h, cl_numvisedicts);
	for (i = 0; i < cl_numvisedicts; i++)
		H_EntityRef (&h, cl_visedicts[i]);
	return h;
}

// X: the effects (temp entities, beams, dynamic lights, light styles, particles)
static hash_t Effects (int *particles)
{
	hash_t h = FNV_OFFSET;
	particle_t *p;
	int i;

	for (i = 0; i < MAX_TEMP_ENTITIES; i++)
		H_Entity (&h, &cl_temp_entities[i]);
	for (i = 0; i < MAX_BEAMS; i++)
	{
		H_Int (&h, cl_beams[i].entity);
		H_Model (&h, cl_beams[i].model);
		H_Float (&h, cl_beams[i].endtime);
		H_Vec (&h, cl_beams[i].start);
		H_Vec (&h, cl_beams[i].end);
	}
	for (i = 0; i < MAX_DLIGHTS; i++)
	{
		H_Vec (&h, cl_dlights[i].origin);
		H_Float (&h, cl_dlights[i].radius);
		H_Float (&h, cl_dlights[i].die);
		H_Float (&h, cl_dlights[i].decay);
		H_Float (&h, cl_dlights[i].minlight);
		H_Int (&h, cl_dlights[i].key);
	}
	for (i = 0; i < MAX_LIGHTSTYLES; i++)
	{
		H_Int (&h, cl_lightstyle[i].length);
		H_String (&h, cl_lightstyle[i].map);
	}
	for (i = 0; i < 256; i++)
		H_Int (&h, d_lightstylevalue[i]);

	*particles = 0;
	for (p = active_particles; p; p = p->next)
	{
		H_Vec (&h, p->org);
		H_Float (&h, p->color);
		H_Vec (&h, p->vel);
		H_Float (&h, p->ramp);
		H_Float (&h, p->die);
		H_Int (&h, p->type);
		(*particles)++;
	}
	return h;
}

// S: the sound channels (snd_dma.c), which decide S_StartSound's rand() draws, and the mixer's times
static hash_t Sound (void)
{
	hash_t h = FNV_OFFSET;
	channel_t *ch;
	int i;

	H_Int (&h, paintedtime);
	H_Int (&h, soundtime);
	H_Int (&h, total_channels);
	for (i = 0, ch = channels; i < total_channels; i++, ch++)
	{
		H_String (&h, ch->sfx ? ch->sfx->name : "");
		H_Int (&h, ch->leftvol);
		H_Int (&h, ch->rightvol);
		H_Int (&h, ch->end);
		H_Int (&h, ch->pos);
		H_Int (&h, ch->looping);
		H_Int (&h, ch->entnum);
		H_Int (&h, ch->entchannel);
		H_Vec (&h, ch->origin);
		H_Float (&h, ch->dist_mult);
		H_Int (&h, ch->master_vol);
	}
	return h;
}

// T: the console's text (console.c)
static hash_t Text (void)
{
	hash_t h = FNV_OFFSET;
	int i;

	H_Int (&h, con_linewidth);
	H_Int (&h, con_totallines);
	H_Int (&h, con_current);
	H_Int (&h, con_x);
	for (i = 0; i < NUM_CON_TIMES; i++)
		H_Float (&h, con_times[i]);
	if (con_text)
		H_Bytes (&h, con_text, CON_TEXTSIZE);
	return h;
}

// R: the view (r_refdef)
static hash_t View (void)
{
	hash_t h = FNV_OFFSET;

	H_Int (&h, r_refdef.vrect.x);
	H_Int (&h, r_refdef.vrect.y);
	H_Int (&h, r_refdef.vrect.width);
	H_Int (&h, r_refdef.vrect.height);
	H_Vec (&h, r_refdef.vieworg);
	H_Vec (&h, r_refdef.viewangles);
	H_Float (&h, r_refdef.fov_x);
	H_Float (&h, r_refdef.fov_y);
	return h;
}

// V: the frame (vid.buffer, row by row)
static hash_t Frame (void)
{
	hash_t h = FNV_OFFSET;
	int y;

	for (y = 0; y < (int)vid.height; y++)
		H_Bytes (&h, vid.buffer + y * vid.rowbytes, vid.width);
	return h;
}

/*
===============================================================================

THE SERVER'S LINES

Without an active server, G hashes the flags and svs, and Q, N and W nothing: what sv points into belongs to a game
that is over.

===============================================================================
*/

static void H_StringPtr (hash_t *h, const char *s)
{
	H_Int (h, s != NULL);
	if (s)
		H_String (h, s);
}

static void H_Sizebuf (hash_t *h, sizebuf_t *b)
{
	H_Int (h, b->overflowed);
	H_Int (h, b->cursize);
	H_Bytes (h, b->data, b->cursize);
}

// an edict's number (from its address, so stale pointers give the C's number too), -1 without a server
static int EdictNumber (edict_t *e)
{
	if (!e || !sv.active || !sv.edicts)
		return -1;
	return (int)(((byte *)e - (byte *)sv.edicts) / pr_edict_size);
}

// G: the server (sv, svs and the client slots up to svs.maxclients)
static hash_t Server (void)
{
	hash_t h = FNV_OFFSET;
	client_t *c;
	int i, j;

	H_Int (&h, sv.active);
	H_Int (&h, sv.paused);
	H_Int (&h, sv.loadgame);
	if (sv.active)
	{
		H_Double (&h, sv.time);
		H_Int (&h, sv.lastcheck);
		H_Double (&h, sv.lastchecktime);
		H_String (&h, sv.name);
		H_String (&h, sv.modelname);
		H_Model (&h, sv.worldmodel);
		for (i = 0; i < MAX_MODELS; i++)
		{
			H_StringPtr (&h, sv.model_precache[i]);
			H_Model (&h, sv.models[i]);
		}
		for (i = 0; i < MAX_SOUNDS; i++)
			H_StringPtr (&h, sv.sound_precache[i]);
		for (i = 0; i < MAX_LIGHTSTYLES; i++)
			H_StringPtr (&h, sv.lightstyles[i]);
		H_Int (&h, sv.num_edicts);
		H_Int (&h, sv.max_edicts);
		H_Int (&h, sv.state);
		H_Sizebuf (&h, &sv.datagram);
		H_Sizebuf (&h, &sv.reliable_datagram);
		H_Sizebuf (&h, &sv.signon);
	}

	H_Int (&h, svs.maxclients);
	H_Int (&h, svs.maxclientslimit);
	H_Int (&h, svs.serverflags);
	H_Int (&h, svs.changelevel_issued);
	for (i = 0, c = svs.clients; i < svs.maxclients; i++, c++)
	{
		H_Int (&h, c->active);
		H_Int (&h, c->spawned);
		H_Int (&h, c->dropasap);
		H_Int (&h, c->privileged);
		H_Int (&h, c->sendsignon);
		H_Double (&h, c->last_message);
		H_Int (&h, c->netconnection != NULL);
		H_Vec (&h, c->cmd.viewangles);
		H_Float (&h, c->cmd.forwardmove);
		H_Float (&h, c->cmd.sidemove);
		H_Float (&h, c->cmd.upmove);
		H_Vec (&h, c->wishdir);
		H_Sizebuf (&h, &c->message);
		H_Int (&h, EdictNumber (c->edict));
		H_String (&h, c->name);
		H_Int (&h, c->colors);
		for (j = 0; j < NUM_PING_TIMES; j++)
			H_Float (&h, c->ping_times[j]);
		H_Int (&h, c->num_pings);
		for (j = 0; j < NUM_SPAWN_PARMS; j++)
			H_Float (&h, c->spawn_parms[j]);
		H_Int (&h, c->old_frags);
	}
	return h;
}

// what each slot of the globals or of an edict's fields holds, from the progs' definitions: 0 no definition covers it
// (the compiler's temporaries, the return value, the parameters), 1 a number, an entity, a field or a function, 2 a
// string (hashed by content: its offset depends on the oracle's addresses)
#define SLOT_NONE	0
#define SLOT_VALUE	1
#define SLOT_STRING	2

static byte *slot_kinds;
static int slot_kinds_size;

static byte *SlotKinds (ddef_t *defs, int numdefs, int numslots)
{
	int i, j, type, size;

	if (numslots > slot_kinds_size)
	{
		slot_kinds = realloc (slot_kinds, numslots);
		slot_kinds_size = numslots;
	}
	memset (slot_kinds, SLOT_NONE, numslots);
	for (i = 0; i < numdefs; i++)
	{
		type = defs[i].type & ~DEF_SAVEGLOBAL;
		size = type == ev_vector ? 3 : 1;
		for (j = 0; j < size; j++)
		{
			if (defs[i].ofs + j >= numslots)
				break;
			if (type == ev_string)
				slot_kinds[defs[i].ofs + j] = SLOT_STRING;
			else if (slot_kinds[defs[i].ofs + j] == SLOT_NONE)
				slot_kinds[defs[i].ofs + j] = SLOT_VALUE;
		}
	}
	return slot_kinds;
}

static void H_Slots (hash_t *h, byte *kinds, int *slots, int count)
{
	int i;

	for (i = 0; i < count; i++)
	{
		if (kinds[i] == SLOT_STRING)
			H_String (h, pr_strings + slots[i]);
		else if (kinds[i] == SLOT_VALUE)
			H_Int (h, slots[i]);
	}
}

// Q: the QuakeC globals
static hash_t Globals (void)
{
	hash_t h = FNV_OFFSET;
	byte *kinds;

	if (!sv.active || !progs)
		return h;
	kinds = SlotKinds (pr_globaldefs, progs->numglobaldefs, progs->numglobals);
	H_Int (&h, progs->numglobals);
	H_Slots (&h, kinds, (int *)pr_globals, progs->numglobals);
	return h;
}

// N: the edicts up to sv.num_edicts, their header and their fields
static hash_t Edicts (int *count)
{
	hash_t h = FNV_OFFSET;
	byte *kinds;
	edict_t *e;
	int i, j;

	*count = 0;
	if (!sv.active || !progs)
		return h;
	kinds = SlotKinds (pr_fielddefs, progs->numfielddefs, progs->entityfields);
	for (i = 0; i < sv.num_edicts; i++)
	{
		e = EDICT_NUM(i);
		H_Int (&h, e->free);
		H_Int (&h, e->num_leafs);
		for (j = 0; j < e->num_leafs; j++)
			H_Int (&h, e->leafnums[j]);
		H_EntityState (&h, &e->baseline);
		H_Float (&h, e->freetime);
		H_Slots (&h, kinds, (int *)&e->v, progs->entityfields);
	}
	*count = sv.num_edicts;
	return h;
}

// W: the area nodes' trigger and solid lists, as edict numbers in their order
static hash_t World (void)
{
	static int links[4096];
	hash_t h = FNV_OFFSET;
	int i, n;

	if (!sv.active)
		return h;
	n = Oracle_AreaLinks (links, sizeof(links) / sizeof(links[0]));
	H_Int (&h, n);
	for (i = 0; i < n; i++)
		H_Int (&h, links[i]);
	return h;
}

static int DumpThisFrame (int frame);

// who sent a message: -1 the client, n the server to client n, -2 another socket
static int MessageSender (qsocket_t *sock)
{
	int i;

	if (sock == cls.netcon)
		return -1;
	for (i = 0; i < svs.maxclients; i++)
		if (svs.clients[i].netconnection == sock)
			return i;
	return -2;
}

// net_main.c (quake-oracle.patch): NET_SendMessage and NET_SendUnreliableMessage, after the driver took the message
void Trace_Message (qsocket_t *sock, sizebuf_t *data, int reliable, int result)
{
	hash_t h = message_hash;
	int sender, i, need;

	sender = MessageSender (sock);
	H_Int (&h, sender);
	H_Int (&h, reliable);
	H_Int (&h, result);
	H_Int (&h, data->cursize);
	H_Bytes (&h, data->data, data->cursize);
	message_hash = h;
	message_count++;

	// the frame running is trace_frame + 1
	if (DumpThisFrame (trace_frame + 1))
	{
		need = message_log_length + 64 + data->cursize * 2;
		if (need > message_log_size)
		{
			message_log_size = need * 2;
			message_log = realloc (message_log, message_log_size);
		}
		message_log_length += sprintf (message_log + message_log_length, "message %d %d %d %d ", sender, reliable,
			result, data->cursize);
		for (i = 0; i < data->cursize; i++)
			message_log_length += sprintf (message_log + message_log_length, "%02x", data->data[i]);
		message_log_length += sprintf (message_log + message_log_length, "\n");
	}
}

/*
===============================================================================

THE TRACE

===============================================================================
*/

void Trace_Init (void)
{
	int t;

	t = COM_CheckParm ("-trace");
	if (t && t < com_argc - 1)
	{
		Q_strncpy (trace_name, com_argv[t + 1], sizeof(trace_name) - 1);
		trace = fopen (trace_name, "w");
		if (!trace)
			Sys_Error ("can't write the trace %s", trace_name);
	}

	for (t = 1; t < com_argc - 1; t++)
	{
		if (!Q_strcmp (com_argv[t], "-traceshot") && num_shots < MAX_SHOTS)
			shots[num_shots++] = Q_atoi (com_argv[t + 1]);
		if (!Q_strcmp (com_argv[t], "-tracedump") && num_dumps < MAX_SHOTS)
			dumps[num_dumps++] = Q_atoi (com_argv[t + 1]);
	}

	t = COM_CheckParm ("-traceevents");
	if (t && t < com_argc - 1)
	{
		events = fopen (com_argv[t + 1], "r");
		if (!events)
			Sys_Error ("can't read the events %s", com_argv[t + 1]);
	}

	t = COM_CheckParm ("-oracleframes");
	if (t && t < com_argc - 1)
		frame_limit = Q_atoi (com_argv[t + 1]);

	quit_after_demo = COM_CheckParm ("-tracequitafterdemo") != 0;
	message_hash = FNV_OFFSET;
}

static void Shot (void)
{
	FILE *f;
	int y, size[2];

	f = fopen (va ("%s.%d.shot", trace_name, trace_frame), "wb");
	if (!f)
		return;
	size[0] = vid.width;
	size[1] = vid.height;
	fwrite (size, 4, 2, f);
	fwrite (vid_curpal, 1, 768, f);
	for (y = 0; y < (int)vid.height; y++)
		fwrite (vid.buffer + y * vid.rowbytes, 1, vid.width, f);
	fclose (f);
}

// a colormap's number, as H_Colormap hashes it
static int ColormapNumber (byte *colormap)
{
	int i;

	if (colormap == vid.colormap)
		return 0;
	for (i = 0; i < cl.maxclients && cl.scores; i++)
		if (colormap == cl.scores[i].translations)
			return 1 + i;
	return -1;
}

static void DumpVec (FILE *f, const char *name, const float *v)
{
	fprintf (f, " %s %08x %08x %08x", name, *(unsigned *)&v[0], *(unsigned *)&v[1], *(unsigned *)&v[2]);
}

static void DumpEntity (FILE *f, const char *what, int i, entity_t *e)
{
	fprintf (f, "%s %d forcelink %d update_type %d", what, i, e->forcelink, e->update_type);
	DumpVec (f, "baseline.origin", e->baseline.origin);
	DumpVec (f, "baseline.angles", e->baseline.angles);
	fprintf (f, " modelindex %d frame %d colormap %d skin %d effects %d", e->baseline.modelindex, e->baseline.frame,
		e->baseline.colormap, e->baseline.skin, e->baseline.effects);
	fprintf (f, " msgtime %016llx", *(unsigned long long *)&e->msgtime);
	DumpVec (f, "msg_origins0", e->msg_origins[0]);
	DumpVec (f, "msg_origins1", e->msg_origins[1]);
	DumpVec (f, "origin", e->origin);
	DumpVec (f, "msg_angles0", e->msg_angles[0]);
	DumpVec (f, "msg_angles1", e->msg_angles[1]);
	DumpVec (f, "angles", e->angles);
	fprintf (f, " model %s frame %d syncbase %08x colormap %d effects %d skinnum %d\n", e->model ? e->model->name : "",
		e->frame, *(unsigned *)&e->syncbase, ColormapNumber (e->colormap), e->effects, e->skinnum);
}

// a string in a dump: quoted, with \\, \" and the characters outside 32-126 as \xHH
static void DumpString (FILE *f, const char *s)
{
	const unsigned char *p;

	fputc ('"', f);
	for (p = (const unsigned char *)s; *p; p++)
	{
		if (*p == '\\' || *p == '"')
			fprintf (f, "\\%c", *p);
		else if (*p < 32 || *p > 126)
			fprintf (f, "\\x%02x", *p);
		else
			fputc (*p, f);
	}
	fputc ('"', f);
}

// the slots of a definition: strings by content, the rest as their bits in hexadecimal
static void DumpDef (FILE *f, const char *what, ddef_t *def, int *slots, int numslots)
{
	int type, size, j;

	type = def->type & ~DEF_SAVEGLOBAL;
	size = type == ev_vector ? 3 : 1;
	fprintf (f, "%s %s", what, pr_strings + def->s_name);
	for (j = 0; j < size && def->ofs + j < numslots; j++)
	{
		fputc (' ', f);
		if (type == ev_string)
			DumpString (f, pr_strings + slots[def->ofs + j]);
		else
			fprintf (f, "%08x", slots[def->ofs + j]);
	}
	fputc ('\n', f);
}

// -tracedump: the server's state, the globals and the edicts by name, the frame's messages
static void DumpServer (FILE *f)
{
	edict_t *e;
	char what[32];
	int i, j;

	fprintf (f, "server active %d paused %d", sv.active, sv.paused);
	if (sv.active)
		fprintf (f, " time %016llx name %s num_edicts %d", *(unsigned long long *)&sv.time, sv.name, sv.num_edicts);
	fprintf (f, "\n");
	if (sv.active && progs)
	{
		for (i = 0; i < progs->numglobaldefs; i++)
			DumpDef (f, "global", &pr_globaldefs[i], (int *)pr_globals, progs->numglobals);
		for (i = 0; i < sv.num_edicts; i++)
		{
			e = EDICT_NUM(i);
			fprintf (f, "edict %d free %d freetime %08x leafs", i, e->free, *(unsigned *)&e->freetime);
			for (j = 0; j < e->num_leafs; j++)
				fprintf (f, " %d", e->leafnums[j]);
			DumpVec (f, "baseline.origin", e->baseline.origin);
			DumpVec (f, "baseline.angles", e->baseline.angles);
			fprintf (f, " modelindex %d frame %d colormap %d skin %d effects %d\n", e->baseline.modelindex,
				e->baseline.frame, e->baseline.colormap, e->baseline.skin, e->baseline.effects);
			sprintf (what, "edict %d", i);
			for (j = 0; j < progs->numfielddefs; j++)
				DumpDef (f, what, &pr_fielddefs[j], (int *)&e->v, progs->entityfields);
		}
	}
	if (message_log_length)
		fwrite (message_log, 1, message_log_length, f);
}

// -tracedump: the entities, field by field
static void Dump (void)
{
	FILE *f;
	int i;

	f = fopen (va ("%s.%d.dump", trace_name, trace_frame), "w");
	if (!f)
		return;
	for (i = 0; i < cl.num_entities; i++)
		DumpEntity (f, "entity", i, &cl_entities[i]);
	for (i = 0; i < cl.num_statics; i++)
		DumpEntity (f, "static", i, &cl_static_entities[i]);
	DumpEntity (f, "viewent", 0, &cl.viewent);
	for (i = 0; i < MAX_TEMP_ENTITIES; i++)
		DumpEntity (f, "temp", i, &cl_temp_entities[i]);
	for (i = 0; i < cl_numvisedicts; i++)
		fprintf (f, "visedict %d %d\n", i, cl_visedicts[i] >= cl_entities && cl_visedicts[i] < cl_entities + MAX_EDICTS
			? (int)(cl_visedicts[i] - cl_entities) : cl_visedicts[i] >= cl_temp_entities && cl_visedicts[i] < cl_temp_entities + MAX_TEMP_ENTITIES
			? 0x20000 + (int)(cl_visedicts[i] - cl_temp_entities) : IsStatic (cl_visedicts[i])
			? 0x10000 + (int)(cl_visedicts[i] - cl_static_entities) : -1);
	DumpServer (f);
	fclose (f);
}

static int DumpThisFrame (int frame)
{
	int i;

	for (i = 0; i < num_dumps; i++)
		if (dumps[i] == frame)
			return 1;
	return 0;
}

void Trace_Frame (void)
{
	int particles, i;

	trace_frame++;

	if (trace)
	{
		if (trace_frame == 1)
			fprintf (trace, "H 4 %d %d %d\n", vid.width, vid.height, (int)(1.0 / oracle_step + 0.5));
		fprintf (trace, "F %d %d %016llx %08x %u\n", trace_frame, host_framecount,
			*(unsigned long long *)&realtime, oracle_rand_seed (), oracle_rand_calls);
		fprintf (trace, "C %016llx\n", Client ());
		fprintf (trace, "E %016llx\n", Entities ());
		fprintf (trace, "D %016llx\n", Drawn ());
		{
			hash_t x = Effects (&particles);
			fprintf (trace, "X %016llx %d\n", x, particles);
		}
		fprintf (trace, "S %016llx\n", Sound ());
		fprintf (trace, "T %016llx\n", Text ());
		fprintf (trace, "R %016llx\n", View ());
		fprintf (trace, "V %016llx %016llx\n", Frame (), palette_hash);
		fprintf (trace, "G %016llx\n", Server ());
		fprintf (trace, "Q %016llx\n", Globals ());
		{
			int edicts;
			hash_t n = Edicts (&edicts);
			fprintf (trace, "N %016llx %d\n", n, edicts);
		}
		fprintf (trace, "W %016llx\n", World ());
		fprintf (trace, "M %016llx %d\n", message_hash, message_count);
	}

	for (i = 0; i < num_shots; i++)
		if (shots[i] == trace_frame)
			Shot ();
	if (DumpThisFrame (trace_frame))
		Dump ();
	message_hash = FNV_OFFSET;
	message_count = 0;
	message_log_length = 0;

	if (cls.demoplayback)
		was_playing = 1;
	else if (quit_after_demo && was_playing)
		Sys_Quit ();

	if (frame_limit && trace_frame >= frame_limit)
		Sys_Quit ();
}

// Sys_SendKeyEvents: the events of the frame about to run
void Trace_Events (void)
{
	while (events)
	{
		if (next_event_frame < 0)
		{
			if (fscanf (events, "%d %1023[^\n]", &next_event_frame, next_event) != 2)
			{
				fclose (events);
				events = NULL;
				return;
			}
		}
		if (next_event_frame > trace_frame + 1)
			return;
		if (!Q_strncmp (next_event, "@key ", 5))
		{
			// a key event: "@key <key number> <1 down, 0 up>"
			int key, down;

			if (sscanf (next_event + 5, "%d %d", &key, &down) == 2)
				Key_Event (key, down);
		}
		else
		{
			Cbuf_AddText (next_event);
			Cbuf_AddText ("\n");
		}
		next_event_frame = -1;
	}
}

void Trace_Palette (unsigned char *palette)
{
	hash_t h = FNV_OFFSET;

	H_Bytes (&h, palette, 768);
	palette_hash = h;
}

void Trace_Error (char *text)
{
	if (trace)
		fprintf (trace, "Err %s\n", text);
}

void Trace_Close (void)
{
	if (trace)
		fclose (trace);
	trace = NULL;
}
