/*
 * The numerics reference: musl's libm (reference/musl, built into the oracle) and the C runtime's rand(), on input
 * sets QuakeSharp's tests generate the same way. Prints one line per set, "<set> <count> <hash>": an FNV-1a hash of
 * the results' bits (NaNs as one value), which tests/QuakeSharp.Core.Tests/LibmTests.cs and RandTests.cs keep.
 *
 *   numerics             every set
 *   numerics <set> <n>   the first n inputs and results of one set, as bits
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#define COUNT 100000

static uint64_t state;

/* splitmix64 */
static uint64_t next(void)
{
	uint64_t z = (state += 0x9e3779b97f4a7c15ULL);
	z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9ULL;
	z = (z ^ (z >> 27)) * 0x94d049bb133111ebULL;
	return z ^ (z >> 31);
}

static double bits(void)
{
	uint64_t i = next();
	double d;
	memcpy(&d, &i, 8);
	return d;
}

/* a float's bit pattern, as a double: what Quake passes */
static double floatbits(void)
{
	uint32_t i = (uint32_t)(next() >> 32);
	float f;
	memcpy(&f, &i, 4);
	return f;
}

/* uniform in [lo, hi) */
static double uniform(double lo, double hi)
{
	return lo + (double)(next() >> 11) * (1.0 / 9007199254740992.0) * (hi - lo);
}

static uint64_t asbits(double d)
{
	uint64_t i;
	if (d != d)
		return 0x7ff8000000000000ULL;
	memcpy(&i, &d, 8);
	return i;
}

typedef struct
{
	const char *name;
	int kind; /* 1: f(x), 2: f(y, x) */
	double (*f1)(double);
	double (*f2)(double, double);
	void (*inputs)(double *a, double *b);
} set_t;

static void in_bits(double *a, double *b) { *a = bits(); *b = bits(); }
static void in_floats(double *a, double *b) { *a = floatbits(); *b = floatbits(); }
static void in_trig(double *a, double *b) { *a = uniform(-10000, 10000); *b = uniform(-10000, 10000); }
static void in_small(double *a, double *b) { *a = uniform(-100, 100); *b = uniform(-100, 100); }
static void in_gamma(double *a, double *b) { *a = uniform(0, 1); *b = uniform(0.3, 3); }

static double f_sin(double x) { return sin(x); }
static double f_cos(double x) { return cos(x); }
static double f_tan(double x) { return tan(x); }
static double f_atan(double x) { return atan(x); }
static double f_atan2(double y, double x) { return atan2(y, x); }
static double f_pow(double x, double y) { return pow(x, y); }

static const set_t sets[] =
{
	{"sin.bits", 1, f_sin, 0, in_bits},
	{"sin.floats", 1, f_sin, 0, in_floats},
	{"sin.range", 1, f_sin, 0, in_trig},
	{"cos.bits", 1, f_cos, 0, in_bits},
	{"cos.floats", 1, f_cos, 0, in_floats},
	{"cos.range", 1, f_cos, 0, in_trig},
	{"tan.bits", 1, f_tan, 0, in_bits},
	{"tan.floats", 1, f_tan, 0, in_floats},
	{"tan.range", 1, f_tan, 0, in_trig},
	{"atan.bits", 1, f_atan, 0, in_bits},
	{"atan.floats", 1, f_atan, 0, in_floats},
	{"atan.range", 1, f_atan, 0, in_small},
	{"atan2.bits", 2, 0, f_atan2, in_bits},
	{"atan2.floats", 2, 0, f_atan2, in_floats},
	{"atan2.range", 2, 0, f_atan2, in_trig},
	{"pow.bits", 2, 0, f_pow, in_bits},
	{"pow.floats", 2, 0, f_pow, in_floats},
	{"pow.gamma", 2, 0, f_pow, in_gamma},
};

static void run(const set_t *s, int index, int detail)
{
	uint64_t hash = 0xcbf29ce484222325ULL;
	int i, j;

	state = (uint64_t)index + 1;
	for (i = 0; i < COUNT && (detail < 0 || i < detail); i++)
	{
		double a, b, r;
		uint64_t rb;
		s->inputs(&a, &b);
		r = s->kind == 1 ? s->f1(a) : s->f2(a, b);
		rb = asbits(r);
		for (j = 0; j < 8; j++)
		{
			hash ^= (rb >> (j * 8)) & 0xff;
			hash *= 0x100000001b3ULL;
		}
		if (detail >= 0)
		{
			if (s->kind == 1)
				printf("%016llx -> %016llx\n", (unsigned long long)asbits(a), (unsigned long long)rb);
			else
				printf("%016llx %016llx -> %016llx\n", (unsigned long long)asbits(a), (unsigned long long)asbits(b), (unsigned long long)rb);
		}
	}
	if (detail < 0)
		printf("%s %d %016llx\n", s->name, i, (unsigned long long)hash);
}

/* rand(): the hash of the first COUNT values, and the first few */
static void run_rand(void)
{
	uint64_t hash = 0xcbf29ce484222325ULL;
	int i, first[4];

	srand(1);
	for (i = 0; i < COUNT; i++)
	{
		int r = rand();
		if (i < 4)
			first[i] = r;
		hash ^= (uint64_t)(r & 0xff);
		hash *= 0x100000001b3ULL;
		hash ^= (uint64_t)((r >> 8) & 0xff);
		hash *= 0x100000001b3ULL;
	}
	printf("rand %d %016llx (%d %d %d %d)\n", COUNT, (unsigned long long)hash, first[0], first[1], first[2], first[3]);
}

int main(int argc, char **argv)
{
	int i, n = sizeof(sets) / sizeof(sets[0]);

	if (argc == 3)
	{
		for (i = 0; i < n; i++)
			if (!strcmp(argv[1], sets[i].name))
				run(&sets[i], i, atoi(argv[2]));
		return 0;
	}

	for (i = 0; i < n; i++)
		run(&sets[i], i, -1);
	run_rand();
	return 0;
}
