/*
 * sys_oracle.c: the oracle's system driver, after sys_null.c and sys_win.c. Headless: no window, no input; time is a
 * virtual clock that moves a fixed step per frame (-oraclestep, 1/64 s by default), so every run is the same
 * (design.md §6.3, §10).
 */
#include "quakedef.h"
#include <errno.h>
#include <direct.h>
#include "trace.h"

qboolean isDedicated;

double oracle_time;
double oracle_step = 1.0 / 64.0;

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

void Sys_Error (char *error, ...)
{
	va_list         argptr;
	char            text[1024];

	va_start (argptr,error);
	vsprintf (text, error, argptr);
	va_end (argptr);

	fprintf (stderr, "Sys_Error: %s\n", text);
	Trace_Error (text);
	Trace_Close ();
	exit (1);
}

qboolean oracle_verbose;

// units.c's scenarios capture what is printed
void (*oracle_print_hook) (const char *text);

void Sys_Printf (char *fmt, ...)
{
	va_list         argptr;
	char		text[4096];

	va_start (argptr,fmt);
	vsprintf (text, fmt, argptr);
	va_end (argptr);

	if (oracle_print_hook)
		oracle_print_hook (text);
	if (oracle_verbose)
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
	return oracle_time;
}

char *Sys_ConsoleInput (void)
{
	return NULL;
}

void Sys_Sleep (void)
{
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

void Oracle_Units (int argc, char **argv);
unsigned long long Oracle_Scenario (const char *path, int print);

int main (int argc, char **argv)
{
	static quakeparms_t    parms;
	int t;

	COM_InitArgv (argc, argv);

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

	Host_Init (&parms);
	while (1)
	{
		oracle_time += oracle_step;
		Host_Frame (oracle_step);
		Trace_Frame ();
	}
}
