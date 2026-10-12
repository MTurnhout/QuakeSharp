/*
 * scenarios.c: quake-oracle.exe -oraclescenario <file> runs a scenario of tools/oracle/units on the command, cvar,
 * console and key code, set up as Host_Init sets it up (Memory_Init, Cbuf_Init, Cmd_Init, the cvars, Key_Init,
 * Con_Init) without the rest of the engine, and prints "<file> <hash>": the hash of its transcript, which is
 * everything printed and the state dumps. With -print it prints the transcript instead. One scenario per run: the
 * engine's state cannot be reset. tests/QuakeSharp.Core.Tests/ScenarioTests.cs runs the same files.
 *
 * A scenario is lines of one action each ("#" starts a comment; text takes the C escapes \n \r \\ \" and \xHH):
 *   text <text>       Cbuf_AddText (text)
 *   execute           Cbuf_Execute ()
 *   key <n> <0|1>     Key_Event (n, down)
 *   dest <n>          key_dest = n (0 game, 1 console, 2 message, 3 menu)
 *   state <n>         cls.state = n (0 dedicated, 1 disconnected, 2 connected)
 *   vid <w> <h>       vid.width, vid.height, then Con_CheckResize ()
 *   time <seconds>    realtime
 *   print <text>      Con_Printf ("%s", text)
 *   dump              the state, as text, into the transcript
 */
#include "quakedef.h"

// cmd.c's cmdalias_t, which no header declares
typedef struct units_alias_s
{
	struct units_alias_s	*next;
	char	name[32];
	char	*value;
} units_alias_t;

extern units_alias_t *cmd_alias;
extern sizebuf_t cmd_text;

extern char key_lines[32][256];	// keys.c: MAXCMDLINE
extern int key_linepos, edit_line, history_line, shift_down, key_count;
extern char chat_buffer[];
extern qboolean team_message;

extern int con_totallines, con_backscroll, con_x, con_current, con_linewidth;
extern char *con_text;
extern float con_times[4];

extern cvar_t developer, skill, deathmatch, teamplay;

extern void (*oracle_print_hook) (const char *text);

static cvar_t unit_archive = {"unit_archive", "1.5", true};
static cvar_t unit_server = {"unit_server", "0", false, true};
static cvar_t unit_value = {"unit_value", "0x10"};

static char transcript[1 << 20];
static int transcript_length;

static void Capture (const char *text)
{
	int length = strlen (text);

	if (transcript_length + length < sizeof(transcript))
	{
		memcpy (transcript + transcript_length, text, length);
		transcript_length += length;
	}
}

static void Out (const char *fmt, ...)
{
	va_list argptr;
	char text[4096];

	va_start (argptr, fmt);
	vsprintf (text, fmt, argptr);
	va_end (argptr);
	Capture (text);
}

// a file written by fprintf, read back
static void CaptureFile (FILE *f)
{
	char line[1024];

	rewind (f);
	while (fgets (line, sizeof(line), f))
		Capture (line);
	fclose (f);
}

static void Dump (void)
{
	units_alias_t *a;
	cvar_t *v;
	int i, j;

	Out ("== dump\n");
	Out ("con current %d x %d backscroll %d totallines %d linewidth %d\n", con_current, con_x, con_backscroll, con_totallines, con_linewidth);
	Out ("times %08x %08x %08x %08x\n", *(int *)&con_times[0], *(int *)&con_times[1], *(int *)&con_times[2], *(int *)&con_times[3]);
	for (i = con_current - 11; i <= con_current; i++)
	{
		char line[256];
		if (i < 0)
			continue;
		for (j = 0; j < con_linewidth; j++)
			line[j] = con_text[(i % con_totallines) * con_linewidth + j];
		line[j] = 0;
		Out ("line %d |%s|\n", i, line);
	}
	Out ("keys dest %d edit %d history %d pos %d shift %d count %d |%s|\n", key_dest, edit_line, history_line, key_linepos, shift_down, key_count, key_lines[edit_line]);
	Out ("chat |%s| team %d\n", chat_buffer, team_message);
	Out ("cbuf %d |", cmd_text.cursize);
	for (i = 0; i < cmd_text.cursize; i++)
		Out ("%c", cmd_text.data[i]);
	Out ("|\n");
	Out ("args %d", Cmd_Argc ());
	for (i = 0; i < Cmd_Argc (); i++)
		Out (" |%s|", Cmd_Argv (i));
	Out ("\n");
	for (v = cvar_vars; v; v = v->next)
		Out ("cvar %s |%s| %08x %d %d\n", v->name, v->string, *(int *)&v->value, v->archive, v->server);
	for (a = cmd_alias; a; a = a->next)
		Out ("alias %s |%s|\n", a->name, a->value);
	{
		FILE *f = tmpfile ();
		Key_WriteBindings (f);
		Cvar_WriteVariables (f);
		CaptureFile (f);
	}
}

// the C escapes \n \r \\ \" \xHH
static void Unescape (const char *in, char *out)
{
	while (*in)
	{
		if (*in == '\\' && in[1])
		{
			in++;
			switch (*in)
			{
			case 'n': *out++ = '\n'; in++; break;
			case 'r': *out++ = '\r'; in++; break;
			case 'x':
				*out++ = (char)strtol (va ("%.2s", in + 1), NULL, 16);
				in += 3;
				break;
			default: *out++ = *in++; break;
			}
		}
		else
			*out++ = *in++;
	}
	*out = 0;
}

static void Run (const char *path)
{
	FILE *f;
	char line[2048], text[2048];

	f = fopen (path, "r");
	if (!f)
		Sys_Error ("can't read %s", path);
	while (fgets (line, sizeof(line), f))
	{
		int length = strlen (line);
		while (length && (line[length - 1] == '\n' || line[length - 1] == '\r'))
			line[--length] = 0;
		if (!length || line[0] == '#')
			continue;

		if (!strncmp (line, "text ", 5))
		{
			Unescape (line + 5, text);
			Cbuf_AddText (text);
		}
		else if (!strcmp (line, "execute"))
			Cbuf_Execute ();
		else if (!strncmp (line, "key ", 4))
		{
			int key, down;
			sscanf (line + 4, "%d %d", &key, &down);
			Key_Event (key, down);
		}
		else if (!strncmp (line, "dest ", 5))
			key_dest = atoi (line + 5);
		else if (!strncmp (line, "state ", 6))
			cls.state = atoi (line + 6);
		else if (!strncmp (line, "vid ", 4))
		{
			int w, h;
			sscanf (line + 4, "%d %d", &w, &h);
			vid.width = w;
			vid.height = h;
			Con_CheckResize ();
		}
		else if (!strncmp (line, "time ", 5))
			realtime = atof (line + 5);
		else if (!strncmp (line, "print ", 6))
		{
			Unescape (line + 6, text);
			Con_Printf ("%s", text);
		}
		else if (!strcmp (line, "dump"))
			Dump ();
		else
			Sys_Error ("%s: unknown action %s", path, line);
	}
	fclose (f);
}

// Host_Init's order for the parts the scenarios use
static void Init (void)
{
	static int initialized;
	int memsize = 0x1000000;

	if (initialized)
		Sys_Error ("one scenario per run");
	initialized = 1;

	Memory_Init (malloc (memsize), memsize);
	Cbuf_Init ();
	Cmd_Init ();
	Cvar_RegisterVariable (&developer);
	Cvar_RegisterVariable (&skill);
	Cvar_RegisterVariable (&deathmatch);
	Cvar_RegisterVariable (&teamplay);
	Cvar_RegisterVariable (&unit_archive);
	Cvar_RegisterVariable (&unit_server);
	Cvar_RegisterVariable (&unit_value);
	Key_Init ();
	scr_disabled_for_loading = true;	// no screen to update
	cls.state = ca_disconnected;
	Con_Init ();
}

// runs one scenario file; returns its transcript's hash, and prints the transcript too if asked
unsigned long long Oracle_Scenario (const char *path, int print)
{
	unsigned long long hash = 0xcbf29ce484222325ULL;
	int i;

	transcript_length = 0;
	oracle_print_hook = Capture;
	Init ();
	Run (path);
	oracle_print_hook = NULL;

	for (i = 0; i < transcript_length; i++)
	{
		hash ^= (unsigned char)transcript[i];
		hash *= 0x100000001b3ULL;
	}
	if (print)
		fwrite (transcript, 1, transcript_length, stdout);
	return hash;
}
