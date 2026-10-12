/*
 * units.c: quake-oracle.exe -oracleunits runs WinQuake's own functions on generated input sets and prints one line per
 * set, "<set> <count> <hash>" (FNV-1a over the results' bytes), for QuakeSharp's unit tests to keep
 * (tests/QuakeSharp.Core.Tests/OracleUnitTests.cs generates the same inputs). "-oracleunits <set> <n>" prints the
 * first n inputs and results of a set instead.
 */
#include "quakedef.h"

// mathlib.c defines it, but no header declares it (only GLQuake calls it): without this prototype the float argument
// would be passed as a double
void RotatePointAroundVector (vec3_t dst, const vec3_t dir, const vec3_t point, float degrees);

#define COUNT 20000

typedef unsigned long long u64;

static u64 state;
static u64 hash;
static int detail = -1;
static int item;

/*
===============================================================================

INPUTS (as tools/oracle/numerics.c)

===============================================================================
*/

static u64 Next (void)
{
	u64 z = (state += 0x9e3779b97f4a7c15ULL);
	z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9ULL;
	z = (z ^ (z >> 27)) * 0x94d049bb133111ebULL;
	return z ^ (z >> 31);
}

// uniform in [lo, hi)
static double Uniform (double lo, double hi)
{
	return lo + (double)(Next () >> 11) * (1.0 / 9007199254740992.0) * (hi - lo);
}

// 0 to n - 1
static int Below (int n)
{
	return (int)(Next () % (u64)n);
}

static float FloatBits (void)
{
	unsigned int i = (unsigned int)(Next () >> 32);
	float f;
	memcpy (&f, &i, 4);
	return f;
}

// A float: mostly in [-range, range), some whole or with few decimals, some any bit pattern that is finite
static float SomeFloat (double range)
{
	float f;

	switch (Below (4))
	{
	case 0:
		return (float)Below ((int)range * 2) - (float)range;
	case 1:
		return (float)(Below ((int)range * 2000) - (int)range * 1000) / 1000.0f;
	case 2:
		do
			f = FloatBits ();
		while (f != f || f - f != 0);
		return f;
	default:
		return (float)Uniform (-range, range);
	}
}

static void SomeVec (vec3_t v, double range)
{
	v[0] = SomeFloat (range);
	v[1] = SomeFloat (range);
	v[2] = SomeFloat (range);
}

// A string of 0 to 11 characters from an alphabet of digits, signs, hex letters, quotes and high characters
static void SomeString (char *s)
{
	static const char alphabet[] = "0123456789000111.-x'aAfFgz \x80\xb0\xff";
	int length = Below (12), i;

	for (i = 0; i < length; i++)
		s[i] = alphabet[Below (sizeof(alphabet) - 1)];
	s[length] = 0;
}

/*
===============================================================================

RESULTS

===============================================================================
*/

static void H_Bytes (const void *data, int length)
{
	const unsigned char *p = data;
	int i;

	for (i = 0; i < length; i++)
	{
		hash ^= p[i];
		hash *= 0x100000001b3ULL;
	}
}

static void Detail (const char *text)
{
	if (detail >= 0 && item < detail)
		printf ("%d: %s\n", item, text);
}

static void R_Int (int v)
{
	H_Bytes (&v, 4);
	Detail (va ("int %d", v));
}

static void R_Float (float v)
{
	H_Bytes (&v, 4);
	Detail (va ("float %08x", *(unsigned int *)&v));
}

static void R_Vec (vec3_t v)
{
	R_Float (v[0]);
	R_Float (v[1]);
	R_Float (v[2]);
}

static void R_String (const char *s)
{
	H_Bytes (s, strlen (s) + 1);
	Detail (va ("string \"%s\"", s));
}

/*
===============================================================================

THE SETS

===============================================================================
*/

static const char *float_formats[] = { "%f", "%5.1f", "%4.2f", "%4.1f", "%2.2f", "%3.1f", "%.0f", "%10.4f" };
static const char *int_formats[] = { "%i", "%d", "%3i", "%5d", "%8i", "%02i", "%2u", "%-2u", "%u", "%x", "%02x", "%c", "% i", "%-5i" };
static const char *string_formats[] = { "%s", "%-15.15s", "%-16.16s", "%8s", "%12s", "%3.1s" };

static void Set_atoi (void) { char s[16]; SomeString (s); R_Int (Q_atoi (s)); }
static void Set_atof (void) { char s[16]; SomeString (s); R_Float (Q_atof (s)); }

static void Set_printf_float (void)
{
	char s[512];
	double v;
	int i;

	switch (Below (4))
	{
	case 0: v = Uniform (-1e6, 1e6); break;
	case 1: v = Below (2000000) / 1000.0 - 1000; break; // halves and decimals that are not exact in binary
	case 2: v = SomeFloat (100000); break; // what Quake prints: floats
	default: v = (Below (200001) - 100000) / 8.0; break; // exact halves and quarters: ties
	}
	for (i = 0; i < sizeof(float_formats) / sizeof(float_formats[0]); i++)
	{
		sprintf (s, float_formats[i], v);
		R_String (s);
	}
}

static void Set_printf_int (void)
{
	char s[64];
	int v = Below (2) ? (int)(Next () >> 32) : Below (2000) - 1000, i;

	for (i = 0; i < sizeof(int_formats) / sizeof(int_formats[0]); i++)
	{
		sprintf (s, int_formats[i], i == 11 ? (v & 0x7f) + 32 : v);
		R_String (s);
	}
}

static void Set_printf_string (void)
{
	char s[64], in[16];
	int i;

	SomeString (in);
	for (i = 0; i < sizeof(string_formats) / sizeof(string_formats[0]); i++)
	{
		sprintf (s, string_formats[i], in);
		R_String (s);
	}
}

static void Set_anglemod (void) { R_Float (anglemod (SomeFloat (100000))); }

static void Set_AngleVectors (void)
{
	vec3_t angles, f, r, u;
	SomeVec (angles, 720);
	AngleVectors (angles, f, r, u);
	R_Vec (f);
	R_Vec (r);
	R_Vec (u);
}

static void Set_VectorNormalize (void)
{
	vec3_t v;
	SomeVec (v, 4096);
	if (Below (10) == 0)
		v[0] = v[1] = v[2] = 0;
	R_Float (VectorNormalize (v));
	R_Vec (v);
}

static void Set_Length (void) { vec3_t v; SomeVec (v, 4096); R_Float (Length (v)); }

static void Set_vectors (void)
{
	vec3_t a, b, c;
	float s;
	SomeVec (a, 4096);
	SomeVec (b, 4096);
	s = SomeFloat (10);
	CrossProduct (a, b, c);
	R_Vec (c);
	VectorMA (a, s, b, c);
	R_Vec (c);
	VectorScale (a, s, c);
	R_Vec (c);
	R_Float (DotProduct (a, b));
	R_Int (VectorCompare (a, b));
	R_Int (VectorCompare (a, a));
	VectorInverse (a);
	R_Vec (a);
}

static void Set_ConcatRotations (void)
{
	float a[3][3], b[3][3], c[3][3];
	int i, j;
	for (i = 0; i < 3; i++)
		for (j = 0; j < 3; j++)
		{
			a[i][j] = SomeFloat (2);
			b[i][j] = SomeFloat (2);
		}
	R_ConcatRotations (a, b, c);
	for (i = 0; i < 3; i++)
		R_Vec (c[i]);
}

static void Set_ConcatTransforms (void)
{
	float a[3][4], b[3][4], c[3][4];
	int i, j;
	for (i = 0; i < 3; i++)
		for (j = 0; j < 4; j++)
		{
			a[i][j] = SomeFloat (2);
			b[i][j] = SomeFloat (2);
		}
	R_ConcatTransforms (a, b, c);
	for (i = 0; i < 3; i++)
		for (j = 0; j < 4; j++)
			R_Float (c[i][j]);
}

static void Set_FloorDivMod (void)
{
	double numer = (double)(Below (2000001) - 1000000);
	double denom = (double)(1 + Below (1000));
	int q, r;
	FloorDivMod (numer, denom, &q, &r);
	R_Int (q);
	R_Int (r);
}

static void Set_GreatestCommonDivisor (void) { R_Int (GreatestCommonDivisor (Below (1000000), Below (1000000))); }

static void Set_Invert24To16 (void) { R_Int (Invert24To16 (Below (2) ? (int)(Next () >> 33) : Below (1 << 20))); }

static void Set_log2 (void) { R_Int (Q_log2 (Below (2) ? (int)(Next () >> 33) : Below (5000))); }

static void Set_RotatePointAroundVector (void)
{
	vec3_t dir, point, out;
	SomeVec (dir, 1);
	VectorNormalize (dir);
	SomeVec (point, 4096);
	RotatePointAroundVector (out, dir, point, SomeFloat (360));
	R_Vec (out);
}

static void Set_BoxOnPlaneSide (void)
{
	mplane_t p;
	vec3_t mins, maxs;
	int i;

	SomeVec (p.normal, 1);
	p.dist = SomeFloat (4096);
	p.signbits = 0;
	for (i = 0; i < 3; i++)
		if (p.normal[i] < 0)
			p.signbits |= 1 << i;
	SomeVec (mins, 4096);
	SomeVec (maxs, 4096);
	R_Int (BoxOnPlaneSide (mins, maxs, &p));
}

typedef struct
{
	const char *name;
	void (*run) (void);
} set_t;

static const set_t sets[] =
{
	{"atoi", Set_atoi},
	{"atof", Set_atof},
	{"printf.float", Set_printf_float},
	{"printf.int", Set_printf_int},
	{"printf.string", Set_printf_string},
	{"anglemod", Set_anglemod},
	{"AngleVectors", Set_AngleVectors},
	{"VectorNormalize", Set_VectorNormalize},
	{"Length", Set_Length},
	{"vectors", Set_vectors},
	{"R_ConcatRotations", Set_ConcatRotations},
	{"R_ConcatTransforms", Set_ConcatTransforms},
	{"FloorDivMod", Set_FloorDivMod},
	{"GreatestCommonDivisor", Set_GreatestCommonDivisor},
	{"Invert24To16", Set_Invert24To16},
	{"Q_log2", Set_log2},
	{"RotatePointAroundVector", Set_RotatePointAroundVector},
	{"BoxOnPlaneSide", Set_BoxOnPlaneSide},
};

void Oracle_Units (int argc, char **argv)
{
	int i, n = sizeof(sets) / sizeof(sets[0]);
	const char *only = NULL;

	if (argc >= 2)
	{
		only = argv[0];
		detail = atoi (argv[1]);
	}

	for (i = 0; i < n; i++)
	{
		if (only && strcmp (only, sets[i].name))
			continue;
		state = (u64)i + 1;
		hash = 0xcbf29ce484222325ULL;
		for (item = 0; item < COUNT && (detail < 0 || item < detail); item++)
			sets[i].run ();
		if (detail < 0)
			printf ("%s %d %016llx\n", sets[i].name, COUNT, hash);
	}
}
