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

// -traceevents: lines of "<frame> <console text>", run before that frame's commands
static FILE *events;
static int next_event_frame = -1;
static char next_event[1024];

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
	H_Int (h, e->topnode && cl.worldmodel ? (int)(e->topnode - cl.worldmodel->nodes) : -1);
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
	fclose (f);
}

void Trace_Frame (void)
{
	int particles, i;

	trace_frame++;

	if (trace)
	{
		if (trace_frame == 1)
			fprintf (trace, "H 2 %d %d %d\n", vid.width, vid.height, (int)(1.0 / oracle_step + 0.5));
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
	}

	for (i = 0; i < num_shots; i++)
		if (shots[i] == trace_frame)
			Shot ();
	for (i = 0; i < num_dumps; i++)
		if (dumps[i] == trace_frame)
			Dump ();

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
		Cbuf_AddText (next_event);
		Cbuf_AddText ("\n");
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
