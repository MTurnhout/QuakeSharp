/*
 * sys_oracle.c: the oracle's system driver, after sys_null.c and sys_win.c. Headless: no window, no input; time is a
 * virtual clock that moves a fixed step per frame (-oraclestep, 1/64 s by default), so every run is the same
 * (design.md §6.3, §10).
 */
#include "quakedef.h"
#include <errno.h>
#include <direct.h>
#include "trace.h"
#include "winsock_oracle.h"

qboolean isDedicated;
static qboolean sc_return_on_enter = false;

double oracle_time;
double oracle_step = 1.0 / 64.0;
static unsigned int oracle_steps;	// the clock's steps so far: frame 1 runs at step 1, a frame that waits takes more
static int oracle_reads;			// Sys_FloatTime's calls since the clock last moved

/*
 * Waiting (design.md §11.5): WinQuake waits within a frame in loops that read the clock until an answer comes or a
 * time has passed (NET_Connect's server search, _Datagram_Connect, NET_SendToAll, Host_ShutdownServer's flush). On a
 * clock that moves only between frames they would never end, so a frame that reads the clock ORACLE_WAIT_READS times
 * without it moving is waiting: that read moves the clock one step first, the time a real machine spends there. An
 * ordinary frame reads it a few dozen times.
 */
#define ORACLE_WAIT_READS	1000

unsigned int Oracle_Steps (void)
{
	return oracle_steps;
}

static void Oracle_Step (void)
{
	oracle_time += oracle_step;
	oracle_steps++;
	oracle_reads = 0;
}

/*
===============================================================================

THE C RUNTIME'S rand (oracle.h)

===============================================================================
*/

static unsigned int holdrand = 1;
unsigned int oracle_rand_calls;

int oracle_rand (void)
{
	oracle_rand_calls++;
	holdrand = holdrand * 214013 + 2531011;
	return (holdrand >> 16) & 0x7fff;
}

void oracle_srand (unsigned int seed)
{
	holdrand = seed;
}

unsigned int oracle_rand_seed (void)
{
	return holdrand;
}

/*
===============================================================================

FILE IO (sys_null.c)

===============================================================================
*/

#define MAX_HANDLES             10
FILE    *sys_handles[MAX_HANDLES];

int             findhandle (void)
{
	int             i;

	for (i=1 ; i<MAX_HANDLES ; i++)
		if (!sys_handles[i])
			return i;
	Sys_Error ("out of handles");
	return -1;
}

int filelength (FILE *f)
{
	int             pos;
	int             end;

	pos = ftell (f);
	fseek (f, 0, SEEK_END);
	end = ftell (f);
	fseek (f, pos, SEEK_SET);

	return end;
}

int Sys_FileOpenRead (char *path, int *hndl)
{
	FILE    *f;
	int             i;

	i = findhandle ();

	f = fopen(path, "rb");
	if (!f)
	{
		*hndl = -1;
		return -1;
	}
	sys_handles[i] = f;
	*hndl = i;

	return filelength(f);
}

int Sys_FileOpenWrite (char *path)
{
	FILE    *f;
	int             i;

	i = findhandle ();

	f = fopen(path, "wb");
	if (!f)
		Sys_Error ("Error opening %s: %s", path,strerror(errno));
	sys_handles[i] = f;

	return i;
}

void Sys_FileClose (int handle)
{
	fclose (sys_handles[handle]);
	sys_handles[handle] = NULL;
}

void Sys_FileSeek (int handle, int position)
{
	fseek (sys_handles[handle], position, SEEK_SET);
}

int Sys_FileRead (int handle, void *dest, int count)
{
	return fread (dest, 1, count, sys_handles[handle]);
}

int Sys_FileWrite (int handle, void *data, int count)
{
	return fwrite (data, 1, count, sys_handles[handle]);
}

int     Sys_FileTime (char *path)
{
	FILE    *f;

	f = fopen(path, "rb");
	if (f)
	{
		fclose(f);
		return 1;
	}

	return -1;
}

void Sys_mkdir (char *path)
{
	_mkdir (path);
}

/*
===============================================================================

SYSTEM IO

===============================================================================
*/

void Sys_MakeCodeWriteable (unsigned long startaddr, unsigned long length)
{
}

void Sys_DebugLog(char *file, char *fmt, ...)
{
	va_list argptr;
	FILE *f;

	f = fopen (file, "a");
	if (!f)
		return;
	va_start (argptr, fmt);
	vfprintf (f, fmt, argptr);
	va_end (argptr);
	fclose (f);
}

qboolean oracle_verbose;

/*
 * sys_win.c's dedicated console (AllocConsole, design.md §11.5): what WriteFile writes to houtput goes to the trace's O
 * line (and with -oracleverbose to stdout); the keys ReadConsoleInput reads come from -traceevents' @stdin lines
 * (Oracle_ConsoleType), only their releases, as WinQuake takes them: a character each.
 */
#define ORACLE_CONSOLE_KEYS	4096
static unsigned char console_keys[ORACLE_CONSOLE_KEYS];
static int console_keys_in, console_keys_out;

static void Oracle_ConsoleWrite (const char *data, int length)
{
	Trace_Console (data, length);
	if (oracle_verbose)
		fwrite (data, 1, length, stdout);
}

// "@stdin <text>": its characters typed in the console; \r is Enter, \b Backspace, \\ a backslash, \xHH any byte
void Oracle_ConsoleType (const char *text)
{
	int c, i, digit;

	while (*text)
	{
		c = (unsigned char)*text++;
		if (c == '\\' && *text)
		{
			c = (unsigned char)*text++;
			if (c == 'r')
				c = '\r';
			else if (c == 'b')
				c = '\b';
			else if (c == 'x')
			{
				c = 0;
				for (i = 0; i < 2 && *text; i++, text++)
				{
					digit = *text >= 'a' ? *text - 'a' + 10 : *text >= 'A' ? *text - 'A' + 10 : *text - '0';
					c = c * 16 + digit;
				}
			}
		}
		if ((console_keys_in + 1) % ORACLE_CONSOLE_KEYS != console_keys_out)
		{
			console_keys[console_keys_in] = c;
			console_keys_in = (console_keys_in + 1) % ORACLE_CONSOLE_KEYS;
		}
	}
}

void Sys_Error (char *error, ...)
{
	va_list         argptr;
	char            text[1024], text2[1024];
	char		*text3 = "Press Enter to exit\n";
	char		*text4 = "***********************************\n";
	char		*text5 = "\n";

	va_start (argptr,error);
	vsprintf (text, error, argptr);
	va_end (argptr);

	// sys_win.c: a dedicated server shows the error in its console (then waits for Enter, up to
	// CONSOLE_ERROR_TIMEOUT seconds: the real console's, not the trace's, which ends here)
	if (isDedicated)
	{
		sprintf (text2, "ERROR: %s\n", text);
		Oracle_ConsoleWrite (text5, strlen (text5));
		Oracle_ConsoleWrite (text4, strlen (text4));
		Oracle_ConsoleWrite (text2, strlen (text2));
		Oracle_ConsoleWrite (text3, strlen (text3));
		Oracle_ConsoleWrite (text4, strlen (text4));
	}

	fprintf (stderr, "Sys_Error: %s\n", text);
	Trace_Error (text);
	Trace_Close ();
	exit (1);
}

// units.c's scenarios capture what is printed
void (*oracle_print_hook) (const char *text);

// sys_win.c: a dedicated server's console shows it (WinQuake prints nothing otherwise; -oracleverbose does)
void Sys_Printf (char *fmt, ...)
{
	va_list         argptr;
	char		text[4096];

	va_start (argptr,fmt);
	vsprintf (text, fmt, argptr);
	va_end (argptr);

	if (oracle_print_hook)
		oracle_print_hook (text);
	if (isDedicated)
		Oracle_ConsoleWrite (text, strlen (text));
	else if (oracle_verbose)
		printf ("%s", text);
}

void Sys_Quit (void)
{
	Host_Shutdown ();
	Trace_Close ();
	exit (0);
}

double Sys_FloatTime (void)
{
	if (++oracle_reads >= ORACLE_WAIT_READS)
		Oracle_Step ();
	return oracle_time;
}

// sys_win.c: Sys_ConsoleInput, over the virtual console's keys
char *Sys_ConsoleInput (void)
{
	static char	text[256];
	static int		len;
	int		ch;

	if (!isDedicated)
		return NULL;


	for ( ;; )
	{
		// GetNumberOfConsoleInputEvents, ReadConsoleInput: the next key released (AsciiChar, a CHAR: signed)
		if (console_keys_out == console_keys_in)
			break;

		ch = (signed char)console_keys[console_keys_out];
		console_keys_out = (console_keys_out + 1) % ORACLE_CONSOLE_KEYS;

		switch (ch)
		{
			case '\r':
				Oracle_ConsoleWrite ("\r\n", 2);

				if (len)
				{
					text[len] = 0;
					len = 0;
					return text;
				}
				else if (sc_return_on_enter)
				{
				// special case to allow exiting from the error handler on Enter
					text[0] = '\r';
					len = 0;
					return text;
				}

				break;

			case '\b':
				Oracle_ConsoleWrite ("\b \b", 3);
				if (len)
				{
					len--;
				}
				break;

			default:
				if (ch >= ' ')
				{
					Oracle_ConsoleWrite ((char *)&ch, 1);
					text[len] = ch;
					len = (len + 1) & 0xff;
				}

				break;

		}
	}

	return NULL;
}

// sys_win.c: Sleep (1), which on Windows' default timer of 15.6 ms takes about a step of the clock
void Sys_Sleep (void)
{
	Oracle_Step ();
}

void Sys_SendKeyEvents (void)
{
	Trace_Events ();
}

void Sys_HighFPPrecision (void)
{
}

void Sys_LowFPPrecision (void)
{
}

void Sys_SetFPCW (void)
{
}

//=============================================================================

// oracle.h: the harness's switches and how many values each takes
int Oracle_HarnessSwitch (char **argv, int j, int argc)
{
	static const struct { const char *name; int values; } switches[] =
	{
		{"-trace", 1}, {"-traceevents", 1}, {"-tracedatagrams", 1}, {"-tracedump", 1}, {"-traceshot", 1},
		{"-tracequitafterdemo", 0}, {"-oracleframes", 1}, {"-oraclevid", 2}, {"-oraclestep", 1}, {"-oraclehost", 1},
		{"-oracleverbose", 0}, {"-netunplug", 0}, {"-recordevents", 1}, {"-quitafter", 1},
	};
	int i, n;

	for (i = 0; i < (int)(sizeof(switches) / sizeof(switches[0])); i++)
		if (!Q_strcmp (argv[j], (char *)switches[i].name))
		{
			n = 1 + switches[i].values;
			return j + n <= argc ? n : argc - j;
		}
	return 0;
}

void Oracle_Units (int argc, char **argv);
unsigned long long Oracle_Scenario (const char *path, int print);

int main (int argc, char **argv)
{
	static quakeparms_t    parms;
	int t;
	double time, oldtime, newtime;

	// sys_win.c: argv[0] is empty (WinMain gets the command line without the program)
	argv[0] = "";
	COM_InitArgv (argc, argv);

	isDedicated = (COM_CheckParm ("-dedicated") != 0);

	// -oraclescenario <file> [-print]: one scenario (scenarios.c), then quit
	t = COM_CheckParm ("-oraclescenario");
	if (t && t < com_argc - 1)
	{
		int print = COM_CheckParm ("-print") != 0;
		unsigned long long hash = Oracle_Scenario (com_argv[t + 1], print);
		if (!print)
			printf ("%s %016llx\n", COM_SkipPath (com_argv[t + 1]), hash);
		return 0;
	}

	// -oracleunits [<set> <n>]: the unit sets (units.c), then quit
	t = COM_CheckParm ("-oracleunits");
	if (t)
	{
		Oracle_Units (com_argc - t - 1, com_argv + t + 1);
		return 0;
	}

	// sys_win.c: the memory WinQuake takes on any machine with 32 MB or more (MAXIMUM_WIN_MEMORY), or -heapsize
	parms.memsize = 0x1000000;
	if (COM_CheckParm ("-heapsize"))
	{
		t = COM_CheckParm("-heapsize") + 1;
		if (t < com_argc)
			parms.memsize = Q_atoi (com_argv[t]) * 1024;
	}
	parms.membase = malloc (parms.memsize);
	parms.basedir = ".";
	parms.cachedir = NULL;
	parms.argc = com_argc;
	parms.argv = com_argv;

	t = COM_CheckParm ("-oraclestep");
	if (t && t < com_argc - 1)
		oracle_step = 1.0 / Q_atoi (com_argv[t + 1]);
	oracle_verbose = COM_CheckParm ("-oracleverbose") != 0;

	Trace_Init ();
	Oracle_WinsockInit ();

	Sys_Printf ("Host_Init\n");
	Host_Init (&parms);
	oldtime = oracle_time;
	while (1)
	{
		if (isDedicated)
		{
			// sys_win.c's loop for a dedicated server: a frame once sys_ticrate has passed, sleeping until then
			newtime = Sys_FloatTime ();
			time = newtime - oldtime;

			while (time < sys_ticrate.value )
			{
				Sys_Sleep();
				newtime = Sys_FloatTime ();
				time = newtime - oldtime;
			}
		}
		else
		{
			// sys_win.c's loop: the time since the last frame began, a step, or more after a frame that waited
			Oracle_Step ();
			newtime = oracle_time;
			time = newtime - oldtime;
		}

		Host_Frame (time);
		oldtime = newtime;
		Trace_Frame ();
	}
}
