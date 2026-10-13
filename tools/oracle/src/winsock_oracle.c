/*
 * winsock_oracle.c: the oracle's Winsock (design.md §11.5). WinQuake's net_win.c, net_wins.c and net_wipx.c are
 * compiled unchanged; LoadLibrary ("wsock32.dll") and GetProcAddress (renamed to Oracle_LoadLibraryA and
 * Oracle_GetProcAddress for those files by Build-Oracle.ps1) answer with the functions here, and what they call of
 * wsock32 directly (bind, the byte order functions, inet_addr, inet_ntoa, the blocking hook's) is defined here instead.
 * A virtual machine answers them:
 *
 * - its name quake<n> and address 10.0.0.<n> (-oraclehost <n>, 1 by default), on a virtual LAN whose hosts are
 *   quake1-quake254 at 10.0.0.1-254 (gethostbyname, gethostbyaddr); localhost is 127.0.0.1;
 * - UDP sockets (IPX's family is refused, as today's Windows has none): bound to a port (0: the next free one from
 *   49152), non-blocking with FIONBIO, broadcast-capable with SO_BROADCAST, each with a queue of what arrived;
 * - what the machine sends to itself (its address, 127.0.0.1, or a broadcast, which also leaves) arrives at the next
 *   step of the clock; one to a port no socket has gives the sender WSAECONNRESET at the next step, as Windows reports
 *   the ICMP answer; what is sent to another host leaves (the trace's U line);
 * - the other hosts' datagrams, from -tracedatagrams <file>: lines "<step> <port> <a.b.c.d:port> <hex bytes>", a
 *   datagram arriving at the machine's port at that step of the clock, or "<step> <port> <a.b.c.d:port> reset", the ICMP
 *   answer to what that socket sent there; at each step the file's arrivals come before the machine's own. A frame
 *   that waits for an answer moves the clock (sys_oracle.c), so what is due arrives while it waits.
 *
 * QuakeSharp's VirtualWinsock is the same.
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "winsock_oracle.h"

#define MAX_SOCKETS		64
#define FIRST_SOCKET	1			// the sockets' numbers, as the system hands them out
#define FIRST_EPHEMERAL	49152
#define MAX_DATAGRAM_SIZE	65507

int Q_atoi (char *str);				// common.c
int COM_CheckParm (char *parm);		// common.c
extern int com_argc;				// common.c
extern char **com_argv;
void Sys_Error (char *error, ...);	// sys_oracle.c

typedef struct datagram_s
{
	struct datagram_s	*next;
	int					reset;		// the ICMP answer instead of a datagram: WSAECONNRESET
	unsigned int		from_addr;	// network byte order
	unsigned short		from_port;	// network byte order
	int					length;
	unsigned char		*data;
} datagram_t;

typedef struct
{
	int					used;
	int					number;
	int					port;		// host byte order; 0 until bound
	int					bound;
	int					nonblocking;
	int					broadcast;
	datagram_t			*queue, *queue_tail;
} vsocket_t;

// what arrives at the machine at a step: the file's, then its own
typedef struct pending_s
{
	struct pending_s	*next;
	unsigned int		step;
	int					port;		// host byte order
	datagram_t			*datagram;
} pending_t;

static int host_number = 1;
static char host_name[16];
static unsigned int host_addr;		// network byte order

static vsocket_t sockets[MAX_SOCKETS];
static int next_socket_number = FIRST_SOCKET;
static int next_ephemeral = FIRST_EPHEMERAL;
static int last_error;
static int started;

static pending_t *own_pending, *own_pending_tail;
static FILE *datagram_file;
static unsigned int next_file_step;
static int next_file_valid;
static int next_file_port;
static datagram_t *next_file_datagram;
static unsigned int delivered_step;	// the steps up to it have arrived

// the U line: what was sent and received during the frame
static unsigned long long datagram_hash = 0xcbf29ce484222325ULL;
static int datagram_count;

/*
===============================================================================

BYTES AND ADDRESSES

===============================================================================
*/

u_short PASCAL FAR htons (u_short hostshort)
{
	return (u_short)((hostshort >> 8) | (hostshort << 8));
}

u_short PASCAL FAR ntohs (u_short netshort)
{
	return htons (netshort);
}

u_long PASCAL FAR htonl (u_long hostlong)
{
	return (hostlong >> 24) | ((hostlong >> 8) & 0xff00) | ((hostlong << 8) & 0xff0000) | (hostlong << 24);
}

u_long PASCAL FAR ntohl (u_long netlong)
{
	return htonl (netlong);
}

static int IsDigit (char c)
{
	return c >= '0' && c <= '9';
}

static int HexDigit (char c)
{
	if (c >= '0' && c <= '9')
		return c - '0';
	if (c >= 'a' && c <= 'f')
		return c - 'a' + 10;
	if (c >= 'A' && c <= 'F')
		return c - 'A' + 10;
	return -1;
}

// "a.b.c.d", each part decimal, octal with 0 or hexadecimal with 0x; fewer parts fill the last one's low bytes, as the
// BSD function does; INADDR_NONE when invalid
unsigned long PASCAL FAR inet_addr (const char FAR *cp)
{
	unsigned int parts[4], value, radix, host;
	int count, d;

	count = 0;
	while (1)
	{
		if (!IsDigit (*cp))
			return INADDR_NONE;
		value = 0;
		radix = 10;
		if (*cp == '0')
		{
			radix = 8;
			cp++;
			if (*cp == 'x' || *cp == 'X')
			{
				radix = 16;
				cp++;
			}
		}
		while (*cp)
		{
			d = IsDigit (*cp) ? *cp - '0' : radix == 16 ? HexDigit (*cp) : -1;
			if (d < 0 || d >= (int)radix)
				break;
			value = value * radix + d;
			cp++;
		}
		parts[count++] = value;
		if (*cp == '.' && count < 4)
		{
			cp++;
			continue;
		}
		break;
	}

	if (*cp && *cp != ' ')
		return INADDR_NONE;

	switch (count)
	{
	case 1:
		host = parts[0];
		break;
	case 2:
		if (parts[0] > 0xff || parts[1] > 0xffffff)
			return INADDR_NONE;
		host = (parts[0] << 24) | parts[1];
		break;
	case 3:
		if (parts[0] > 0xff || parts[1] > 0xff || parts[2] > 0xffff)
			return INADDR_NONE;
		host = (parts[0] << 24) | (parts[1] << 16) | parts[2];
		break;
	default:
		if (parts[0] > 0xff || parts[1] > 0xff || parts[2] > 0xff || parts[3] > 0xff)
			return INADDR_NONE;
		host = (parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | parts[3];
		break;
	}
	if (host == INADDR_NONE)
		return INADDR_NONE;
	return htonl (host);
}

char FAR * PASCAL FAR inet_ntoa (struct in_addr in)
{
	static char buffer[16];
	unsigned int a = in.s_addr;

	sprintf (buffer, "%d.%d.%d.%d", a & 0xff, (a >> 8) & 0xff, (a >> 16) & 0xff, a >> 24);
	return buffer;
}

static unsigned int MakeAddr (int a, int b, int c, int d)
{
	return htonl ((a << 24) | (b << 16) | (c << 8) | d);
}

/*
===============================================================================

THE HOSTS

===============================================================================
*/

static struct hostent host_entry;
static char host_entry_name[32];
static unsigned int host_entry_addr;
static char *host_entry_addrs[2];
static char *host_entry_aliases[1];

static struct hostent *HostEntry (const char *name, unsigned int addr)
{
	strcpy (host_entry_name, name);
	host_entry_addr = addr;
	host_entry_addrs[0] = (char *)&host_entry_addr;
	host_entry_addrs[1] = NULL;
	host_entry_aliases[0] = NULL;
	host_entry.h_name = host_entry_name;
	host_entry.h_aliases = host_entry_aliases;
	host_entry.h_addrtype = AF_INET;
	host_entry.h_length = 4;
	host_entry.h_addr_list = host_entry_addrs;
	return &host_entry;
}

static int LanHost (unsigned int addr)
{
	unsigned int h = ntohl (addr);

	if ((h >> 8) == 0x0a0000 && (h & 0xff) >= 1 && (h & 0xff) <= 254)
		return h & 0xff;
	return 0;
}

static int IsLoopback (unsigned int addr)
{
	return (ntohl (addr) >> 24) == 127;
}

static struct hostent FAR * PASCAL FAR V_gethostbyname (const char FAR *name)
{
	unsigned int addr;
	int n;

	if (!_stricmp (name, "localhost"))
		return HostEntry ("localhost", MakeAddr (127, 0, 0, 1));
	// quake<n>, the whole name ("quake1:26001" resolves no more than on Windows)
	if (!_strnicmp (name, "quake", 5) && IsDigit (name[5]) && strspn (name + 5, "0123456789") == strlen (name + 5)
		&& strlen (name + 5) <= 3)
	{
		n = atoi (name + 5);
		if (n >= 1 && n <= 254)
		{
			sprintf (host_entry_name, "quake%d", n);
			return HostEntry (host_entry_name, MakeAddr (10, 0, 0, n));
		}
	}
	addr = inet_addr (name);
	if (addr != INADDR_NONE)
		return HostEntry (name, addr);
	last_error = WSAHOST_NOT_FOUND;
	return NULL;
}

static struct hostent FAR * PASCAL FAR V_gethostbyaddr (const char FAR *addr, int len, int type)
{
	unsigned int a;
	char name[16];

	if (len != 4 || type != AF_INET)
	{
		last_error = WSAEINVAL;
		return NULL;
	}
	memcpy (&a, addr, 4);
	if (LanHost (a))
	{
		sprintf (name, "quake%d", LanHost (a));
		return HostEntry (name, a);
	}
	if (IsLoopback (a))
		return HostEntry ("localhost", a);
	last_error = WSAHOST_NOT_FOUND;
	return NULL;
}

static int PASCAL FAR V_gethostname (char FAR *name, int namelen)
{
	if ((int)strlen (host_name) + 1 > namelen)
	{
		last_error = WSAEFAULT;
		return SOCKET_ERROR;
	}
	strcpy (name, host_name);
	return 0;
}

/*
===============================================================================

DELIVERY

===============================================================================
*/

static vsocket_t *SocketByNumber (SOCKET s)
{
	int i;

	for (i = 0; i < MAX_SOCKETS; i++)
		if (sockets[i].used && sockets[i].number == (int)s)
			return &sockets[i];
	return NULL;
}

static vsocket_t *SocketByPort (int port)
{
	int i;

	for (i = 0; i < MAX_SOCKETS; i++)
		if (sockets[i].used && sockets[i].bound && sockets[i].port == port)
			return &sockets[i];
	return NULL;
}

static datagram_t *NewDatagram (unsigned int from_addr, unsigned short from_port, const unsigned char *data, int length)
{
	datagram_t *d = calloc (1, sizeof(datagram_t));

	d->from_addr = from_addr;
	d->from_port = from_port;
	d->length = length;
	d->data = malloc (length > 0 ? length : 1);
	memcpy (d->data, data, length);
	return d;
}

static void FreeDatagram (datagram_t *d)
{
	free (d->data);
	free (d);
}

static void Enqueue (vsocket_t *s, datagram_t *d)
{
	d->next = NULL;
	if (s->queue_tail)
		s->queue_tail->next = d;
	else
		s->queue = d;
	s->queue_tail = d;
}

// an arrival at the machine's port: queued at the socket bound to it, or dropped
static void Arrive (int port, datagram_t *d)
{
	vsocket_t *s = SocketByPort (port);

	if (s)
		Enqueue (s, d);
	else
		FreeDatagram (d);
}

static int ReadAddress (const char *text, unsigned int *addr, unsigned short *port)
{
	int a, b, c, d, p;

	if (sscanf (text, "%d.%d.%d.%d:%d", &a, &b, &c, &d, &p) != 5)
		return 0;
	*addr = MakeAddr (a, b, c, d);
	*port = htons ((unsigned short)p);
	return 1;
}

// the file's next line, kept until its step comes
static void ReadFileLine (void)
{
	static char line[2 * MAX_DATAGRAM_SIZE + 64];
	char address[64], *bytes;
	unsigned int addr;
	unsigned short port;
	int step, dport, n, length, i, offset;

	next_file_valid = 0;
	while (datagram_file && fgets (line, sizeof(line), datagram_file))
	{
		if (sscanf (line, "%d %d %63s %n", &step, &dport, address, &n) != 3 || !ReadAddress (address, &addr, &port))
			continue;
		bytes = line + n;
		length = (int)strcspn (bytes, "\r\n");
		bytes[length] = 0;
		if (!strcmp (bytes, "reset"))
		{
			next_file_datagram = NewDatagram (addr, port, (unsigned char *)"", 0);
			next_file_datagram->reset = 1;
		}
		else
		{
			next_file_datagram = NewDatagram (addr, port, (unsigned char *)"", 0);
			free (next_file_datagram->data);
			next_file_datagram->length = length / 2;
			next_file_datagram->data = malloc (length / 2 + 1);
			for (i = 0, offset = 0; i < length / 2; i++, offset += 2)
				next_file_datagram->data[i] = (unsigned char)(HexDigit (bytes[offset]) * 16 + HexDigit (bytes[offset + 1]));
		}
		next_file_step = step;
		next_file_port = dport;
		next_file_valid = 1;
		return;
	}
	if (datagram_file)
	{
		fclose (datagram_file);
		datagram_file = NULL;
	}
}

// what has arrived by now, step by step: the file's, then the machine's own
static void Deliver (void)
{
	unsigned int now = Oracle_Steps (), step;
	pending_t *p;

	for (step = delivered_step + 1; step <= now; step++)
	{
		while (next_file_valid && next_file_step <= step)
		{
			Arrive (next_file_port, next_file_datagram);
			ReadFileLine ();
		}
		while (own_pending && own_pending->step <= step)
		{
			p = own_pending;
			own_pending = p->next;
			if (!own_pending)
				own_pending_tail = NULL;
			Arrive (p->port, p->datagram);
			free (p);
		}
	}
	if (now > delivered_step)
		delivered_step = now;
}

static void SendOwn (int port, datagram_t *d)
{
	pending_t *p = calloc (1, sizeof(pending_t));

	p->step = Oracle_Steps () + 1;
	p->port = port;
	p->datagram = d;
	if (own_pending_tail)
		own_pending_tail->next = p;
	else
		own_pending = p;
	own_pending_tail = p;
}

static void H_Bytes (const void *data, int length)
{
	const unsigned char *p = data;
	int i;

	for (i = 0; i < length; i++)
	{
		datagram_hash ^= p[i];
		datagram_hash *= 0x100000001b3ULL;
	}
}

static void H_Int (int v)
{
	H_Bytes (&v, 4);
}

// -tracedump's datagram lines: those of the frame running, and those of the frame last traced
static char *datagram_log, *last_datagram_log;
static int datagram_log_length, datagram_log_size, last_datagram_log_size;

// the U line: 0 sent, 1 received, 2 the ICMP answer received; the socket's port, the other end, the bytes
static void H_Datagram (int kind, int port, unsigned int addr, unsigned short addr_port, const unsigned char *data, int length)
{
	unsigned int a = ntohl (addr);
	int i, need;

	H_Int (kind);
	H_Int (port);
	H_Int ((int)a);
	H_Int (ntohs (addr_port));
	H_Int (length);
	H_Bytes (data, length);
	datagram_count++;

	need = datagram_log_length + 80 + length * 2;
	if (need > datagram_log_size)
	{
		datagram_log_size = need * 2;
		datagram_log = realloc (datagram_log, datagram_log_size);
	}
	datagram_log_length += sprintf (datagram_log + datagram_log_length, "datagram %d %d %u.%u.%u.%u:%u %d ", kind, port,
		a >> 24, (a >> 16) & 0xff, (a >> 8) & 0xff, a & 0xff, ntohs (addr_port), length);
	for (i = 0; i < length; i++)
		datagram_log_length += sprintf (datagram_log + datagram_log_length, "%02x", data[i]);
	datagram_log_length += sprintf (datagram_log + datagram_log_length, "\n");
}

unsigned long long Oracle_Datagrams (int *count)
{
	unsigned long long h = datagram_hash;
	char *log;
	int size;

	*count = datagram_count;
	datagram_hash = 0xcbf29ce484222325ULL;
	datagram_count = 0;

	// the frame's log becomes the last frame's
	log = last_datagram_log;
	size = last_datagram_log_size;
	last_datagram_log = datagram_log;
	last_datagram_log_size = datagram_log_size;
	datagram_log = log;
	datagram_log_size = size;
	if (!last_datagram_log)
	{
		last_datagram_log = malloc (1);
		last_datagram_log_size = 1;
	}
	last_datagram_log[datagram_log_length < last_datagram_log_size ? datagram_log_length : 0] = 0;
	datagram_log_length = 0;
	return h;
}

const char *Oracle_DatagramLog (void)
{
	return last_datagram_log ? last_datagram_log : "";
}

/*
===============================================================================

THE SOCKETS

===============================================================================
*/

static int PASCAL FAR V_WSAStartup (WORD wVersionRequired, LPWSADATA lpWSAData)
{
	memset (lpWSAData, 0, sizeof(*lpWSAData));
	lpWSAData->wVersion = MAKEWORD(1, 1);
	lpWSAData->wHighVersion = MAKEWORD(2, 2);
	strcpy (lpWSAData->szDescription, "WinSock 2.0");
	strcpy (lpWSAData->szSystemStatus, "Running");
	lpWSAData->iMaxSockets = 0;
	lpWSAData->iMaxUdpDg = 0;
	started++;
	return 0;
}

static int PASCAL FAR V_WSACleanup (void)
{
	if (!started)
	{
		last_error = WSANOTINITIALISED;
		return SOCKET_ERROR;
	}
	started--;
	return 0;
}

static int PASCAL FAR V_WSAGetLastError (void)
{
	return last_error;
}

static SOCKET PASCAL FAR V_socket (int af, int type, int protocol)
{
	int i;

	if (af != AF_INET)
	{
		last_error = WSAEAFNOSUPPORT;
		return INVALID_SOCKET;
	}
	if (type != SOCK_DGRAM)
	{
		last_error = WSAESOCKTNOSUPPORT;
		return INVALID_SOCKET;
	}
	for (i = 0; i < MAX_SOCKETS; i++)
		if (!sockets[i].used)
			break;
	if (i == MAX_SOCKETS)
	{
		last_error = WSAEMFILE;
		return INVALID_SOCKET;
	}
	memset (&sockets[i], 0, sizeof(sockets[i]));
	sockets[i].used = 1;
	sockets[i].number = next_socket_number++;
	return sockets[i].number;
}

static int PASCAL FAR V_ioctlsocket (SOCKET s, long cmd, u_long FAR *argp)
{
	vsocket_t *v = SocketByNumber (s);

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	if (cmd == FIONREAD)
	{
		// for a datagram socket, the size of the first datagram queued
		Deliver ();
		*argp = v->queue && !v->queue->reset ? v->queue->length : 0;
		return 0;
	}
	if (cmd != FIONBIO)
	{
		last_error = WSAEINVAL;
		return SOCKET_ERROR;
	}
	v->nonblocking = *argp != 0;
	return 0;
}

static int PASCAL FAR V_setsockopt (SOCKET s, int level, int optname, const char FAR *optval, int optlen)
{
	vsocket_t *v = SocketByNumber (s);

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	if (level != SOL_SOCKET || optname != SO_BROADCAST || optlen < 4)
	{
		last_error = WSAENOPROTOOPT;
		return SOCKET_ERROR;
	}
	v->broadcast = *(const int *)optval != 0;
	return 0;
}

int PASCAL FAR bind (SOCKET s, const struct sockaddr FAR *addr, int namelen)
{
	vsocket_t *v = SocketByNumber (s);
	const struct sockaddr_in *in = (const struct sockaddr_in *)addr;
	int port;

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	if (v->bound || namelen < (int)sizeof(struct sockaddr_in) || in->sin_family != AF_INET)
	{
		last_error = WSAEINVAL;
		return SOCKET_ERROR;
	}
	if (in->sin_addr.s_addr != INADDR_ANY && in->sin_addr.s_addr != host_addr && !IsLoopback (in->sin_addr.s_addr))
	{
		last_error = WSAEADDRNOTAVAIL;
		return SOCKET_ERROR;
	}
	port = ntohs (in->sin_port);
	if (port == 0)
	{
		while (SocketByPort (next_ephemeral))
			next_ephemeral = next_ephemeral == 65535 ? FIRST_EPHEMERAL : next_ephemeral + 1;
		port = next_ephemeral;
		next_ephemeral = next_ephemeral == 65535 ? FIRST_EPHEMERAL : next_ephemeral + 1;
	}
	else if (SocketByPort (port))
	{
		last_error = WSAEADDRINUSE;
		return SOCKET_ERROR;
	}
	v->port = port;
	v->bound = 1;
	return 0;
}

static int PASCAL FAR V_recvfrom (SOCKET s, char FAR *buf, int len, int flags, struct sockaddr FAR *from, int FAR *fromlen)
{
	vsocket_t *v = SocketByNumber (s);
	datagram_t *d;
	struct sockaddr_in *in;
	int n;

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	if (!v->bound)
	{
		last_error = WSAEINVAL;
		return SOCKET_ERROR;
	}

	Deliver ();
	if (!v->queue)
	{
		last_error = WSAEWOULDBLOCK;
		return SOCKET_ERROR;
	}

	d = v->queue;
	if (from)
	{
		if (!fromlen || *fromlen < (int)sizeof(struct sockaddr_in))
		{
			last_error = WSAEFAULT;
			return SOCKET_ERROR;
		}
		in = (struct sockaddr_in *)from;
		memset (in, 0, sizeof(*in));
		in->sin_family = AF_INET;
		in->sin_port = d->from_port;
		in->sin_addr.s_addr = d->from_addr;
		*fromlen = sizeof(struct sockaddr_in);
	}

	if (d->reset)
	{
		v->queue = d->next;
		if (!v->queue)
			v->queue_tail = NULL;
		H_Datagram (2, v->port, d->from_addr, d->from_port, d->data, 0);
		FreeDatagram (d);
		last_error = WSAECONNRESET;
		return SOCKET_ERROR;
	}

	n = d->length < len ? d->length : len;
	memcpy (buf, d->data, n);
	if (flags & MSG_PEEK)
	{
		if (d->length > len)
		{
			last_error = WSAEMSGSIZE;
			return SOCKET_ERROR;
		}
		return n;
	}

	v->queue = d->next;
	if (!v->queue)
		v->queue_tail = NULL;
	H_Datagram (1, v->port, d->from_addr, d->from_port, d->data, n);
	if (d->length > len)
	{
		FreeDatagram (d);
		last_error = WSAEMSGSIZE;
		return SOCKET_ERROR;
	}
	FreeDatagram (d);
	return n;
}

static int PASCAL FAR V_sendto (SOCKET s, const char FAR *buf, int len, int flags, const struct sockaddr FAR *to, int tolen)
{
	vsocket_t *v = SocketByNumber (s);
	const struct sockaddr_in *in = (const struct sockaddr_in *)to;
	unsigned int addr, from_addr;
	int port;
	datagram_t *d;

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	if (!v->bound)
	{
		last_error = WSAEINVAL;
		return SOCKET_ERROR;
	}
	if (tolen < (int)sizeof(struct sockaddr_in) || in->sin_family != AF_INET)
	{
		last_error = WSAEAFNOSUPPORT;
		return SOCKET_ERROR;
	}
	if (len > MAX_DATAGRAM_SIZE)
	{
		last_error = WSAEMSGSIZE;
		return SOCKET_ERROR;
	}
	addr = in->sin_addr.s_addr;
	port = ntohs (in->sin_port);
	if (addr == INADDR_BROADCAST && !v->broadcast)
	{
		last_error = WSAEACCES;
		return SOCKET_ERROR;
	}

	H_Datagram (0, v->port, addr, in->sin_port, (const unsigned char *)buf, len);

	// to the machine itself: from its address, or from 127.0.0.1 when sent to the loopback
	if (addr == host_addr || addr == INADDR_BROADCAST || IsLoopback (addr))
	{
		from_addr = IsLoopback (addr) ? MakeAddr (127, 0, 0, 1) : host_addr;
		if (SocketByPort (port))
			SendOwn (port, NewDatagram (from_addr, htons ((unsigned short)v->port), (const unsigned char *)buf, len));
		else if (addr != INADDR_BROADCAST)
		{
			// no socket there: the ICMP answer
			d = NewDatagram (addr, in->sin_port, (const unsigned char *)"", 0);
			d->reset = 1;
			SendOwn (v->port, d);
		}
	}
	return len;
}

static int PASCAL FAR V_closesocket (SOCKET s)
{
	vsocket_t *v = SocketByNumber (s);
	datagram_t *d;

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	while ((d = v->queue) != NULL)
	{
		v->queue = d->next;
		FreeDatagram (d);
	}
	memset (v, 0, sizeof(*v));
	return 0;
}

static int PASCAL FAR V_getsockname (SOCKET s, struct sockaddr FAR *name, int FAR *namelen)
{
	vsocket_t *v = SocketByNumber (s);
	struct sockaddr_in *in = (struct sockaddr_in *)name;

	if (!v)
	{
		last_error = WSAENOTSOCK;
		return SOCKET_ERROR;
	}
	if (!v->bound)
	{
		last_error = WSAEINVAL;
		return SOCKET_ERROR;
	}
	if (*namelen < (int)sizeof(struct sockaddr_in))
	{
		last_error = WSAEFAULT;
		return SOCKET_ERROR;
	}
	memset (in, 0, sizeof(*in));
	in->sin_family = AF_INET;
	in->sin_port = htons ((unsigned short)v->port);
	in->sin_addr.s_addr = INADDR_ANY;
	*namelen = sizeof(struct sockaddr_in);
	return 0;
}

/*
===============================================================================

THE BLOCKING HOOK AND THE LIBRARY

===============================================================================
*/

FARPROC PASCAL FAR WSASetBlockingHook (FARPROC lpBlockFunc)
{
	return NULL;
}

int PASCAL FAR WSAUnhookBlockingHook (void)
{
	return 0;
}

int PASCAL FAR WSACancelBlockingCall (void)
{
	return 0;
}

// what net_wins.c's BlockingHook would pump while a name resolves (never: nothing blocks here): no window messages
BOOL WINAPI PeekMessageA (LPMSG lpMsg, HWND hWnd, UINT wMsgFilterMin, UINT wMsgFilterMax, UINT wRemoveMsg)
{
	return FALSE;
}

BOOL WINAPI TranslateMessage (CONST MSG *lpMsg)
{
	return FALSE;
}

LRESULT WINAPI DispatchMessageA (CONST MSG *lpMsg)
{
	return 0;
}

#define WSOCK32_HANDLE	((HMODULE)0x0350000)

HMODULE WINAPI Oracle_LoadLibraryA (LPCSTR name)
{
	if (!_stricmp (name, "wsock32.dll"))
		return WSOCK32_HANDLE;
	return NULL;
}

FARPROC WINAPI Oracle_GetProcAddress (HMODULE module, LPCSTR name)
{
	static const struct { const char *name; FARPROC proc; } procs[] =
	{
		{"WSAStartup", (FARPROC)V_WSAStartup},
		{"WSACleanup", (FARPROC)V_WSACleanup},
		{"WSAGetLastError", (FARPROC)V_WSAGetLastError},
		{"socket", (FARPROC)V_socket},
		{"ioctlsocket", (FARPROC)V_ioctlsocket},
		{"setsockopt", (FARPROC)V_setsockopt},
		{"recvfrom", (FARPROC)V_recvfrom},
		{"sendto", (FARPROC)V_sendto},
		{"closesocket", (FARPROC)V_closesocket},
		{"gethostname", (FARPROC)V_gethostname},
		{"gethostbyname", (FARPROC)V_gethostbyname},
		{"gethostbyaddr", (FARPROC)V_gethostbyaddr},
		{"getsockname", (FARPROC)V_getsockname},
	};
	int i;

	if (module != WSOCK32_HANDLE)
		return NULL;
	for (i = 0; i < (int)(sizeof(procs) / sizeof(procs[0])); i++)
		if (!strcmp (procs[i].name, name))
			return procs[i].proc;
	return NULL;
}

// the machine (-oraclehost) and the other hosts' datagrams (-tracedatagrams), before Host_Init
void Oracle_WinsockInit (void)
{
	int t;

	t = COM_CheckParm ("-oraclehost");
	if (t && t < com_argc - 1)
		host_number = Q_atoi (com_argv[t + 1]);
	if (host_number < 1 || host_number > 254)
		Sys_Error ("-oraclehost: %d is not a host of the virtual LAN (1-254)", host_number);
	sprintf (host_name, "quake%d", host_number);
	host_addr = MakeAddr (10, 0, 0, host_number);

	t = COM_CheckParm ("-tracedatagrams");
	if (t && t < com_argc - 1)
	{
		datagram_file = fopen (com_argv[t + 1], "r");
		if (!datagram_file)
			Sys_Error ("can't read the datagrams %s", com_argv[t + 1]);
		ReadFileLine ();
	}
}
