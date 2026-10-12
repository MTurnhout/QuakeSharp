/*
 * net_oracle.c: the oracle's network drivers, in place of net_win.c (design.md decision 33). WinQuake's driver list,
 * loopback then datagram (net_dgrm.c), over one LAN driver: net_wins.c without sockets. TCP/IP is available at a fixed
 * address, the driver prints what net_wins.c prints, no name or address resolves (so nothing waits for an answer that
 * cannot come) and no packet ever arrives; what is written goes nowhere. QuakeSharp's VirtualLanDriver is the same.
 */
#include "quakedef.h"

#include "net_loop.h"
#include "net_dgrm.h"

net_driver_t net_drivers[MAX_NET_DRIVERS] =
{
	{
	"Loopback",
	false,
	Loop_Init,
	Loop_Listen,
	Loop_SearchForHosts,
	Loop_Connect,
	Loop_CheckNewConnections,
	Loop_GetMessage,
	Loop_SendMessage,
	Loop_SendUnreliableMessage,
	Loop_CanSendMessage,
	Loop_CanSendUnreliableMessage,
	Loop_Close,
	Loop_Shutdown
	}
	,
	{
	"Datagram",
	false,
	Datagram_Init,
	Datagram_Listen,
	Datagram_SearchForHosts,
	Datagram_Connect,
	Datagram_CheckNewConnections,
	Datagram_GetMessage,
	Datagram_SendMessage,
	Datagram_SendUnreliableMessage,
	Datagram_CanSendMessage,
	Datagram_CanSendUnreliableMessage,
	Datagram_Close,
	Datagram_Shutdown
	}
};

int net_numdrivers = 2;

/*
===============================================================================

THE VIRTUAL TCP/IP DRIVER

net_wins.c's functions, in its order, with the sockets taken out. An address is a sockaddr_in: the family, the port
and the address in network byte order.

===============================================================================
*/

#define VIRTUAL_AF_INET		2
#define VIRTUAL_INADDR_ANY	0x00000000
#define VIRTUAL_BROADCAST	0xffffffff
#define VIRTUAL_LOOPBACK	0x7f000001	// the address the machine has: 127.0.0.1

typedef struct
{
	short			sin_family;
	unsigned short	sin_port;		// network byte order
	unsigned int	sin_addr;		// network byte order
	char			sin_zero[8];
} virtual_sockaddr_t;

static int net_acceptsocket = -1;		// socket for fielding new connections
static int net_controlsocket;
static int net_broadcastsocket = 0;
static struct qsockaddr broadcastaddr;

static unsigned long myAddr;

static int virtual_nextsocket = 1;	// the sockets' numbers, as the system gives them

static unsigned short V_htons (unsigned short v)
{
	return (unsigned short)((v >> 8) | (v << 8));
}

static unsigned int V_htonl (unsigned int v)
{
	return (v >> 24) | ((v >> 8) & 0xff00) | ((v << 8) & 0xff0000) | (v << 24);
}

#define V_ntohs V_htons
#define V_ntohl V_htonl

int  VLAN_OpenSocket (int port);
int  VLAN_CloseSocket (int socket);
int  VLAN_Write (int socket, byte *buf, int len, struct qsockaddr *addr);
char *VLAN_AddrToString (struct qsockaddr *addr);

// net_wins.c: WINS_GetLocalAddress (the machine's address: 127.0.0.1)
static void VLAN_GetLocalAddress (void)
{
	unsigned long	addr;

	if (myAddr != VIRTUAL_INADDR_ANY)
		return;

	myAddr = V_htonl (VIRTUAL_LOOPBACK);

	addr = V_ntohl (myAddr);
	sprintf(my_tcpip_address, "%d.%d.%d.%d", (addr >> 24) & 0xff, (addr >> 16) & 0xff, (addr >> 8) & 0xff, addr & 0xff);
}

// net_wins.c: WINS_Init (the machine has no name of its own: hostname is left as it is)
int VLAN_Init (void)
{
	int		i;

	if (COM_CheckParm ("-noudp"))
		return -1;

	i = COM_CheckParm ("-ip");
	if (i)
	{
		if (i < com_argc-1)
		{
			int ha1, ha2, ha3, ha4;

			if (sscanf (com_argv[i+1], "%d.%d.%d.%d", &ha1, &ha2, &ha3, &ha4) != 4)
				Sys_Error ("%s is not a valid IP address", com_argv[i+1]);
			myAddr = V_htonl ((ha1 << 24) | (ha2 << 16) | (ha3 << 8) | ha4);
			strcpy(my_tcpip_address, com_argv[i+1]);
		}
		else
		{
			Sys_Error ("NET_Init: you must specify an IP address after -ip");
		}
	}
	else
	{
		myAddr = VIRTUAL_INADDR_ANY;
		strcpy(my_tcpip_address, "INADDR_ANY");
	}

	if ((net_controlsocket = VLAN_OpenSocket (0)) == -1)
	{
		Con_Printf("WINS_Init: Unable to open control socket\n");
		return -1;
	}

	((virtual_sockaddr_t *)&broadcastaddr)->sin_family = VIRTUAL_AF_INET;
	((virtual_sockaddr_t *)&broadcastaddr)->sin_addr = VIRTUAL_BROADCAST;
	((virtual_sockaddr_t *)&broadcastaddr)->sin_port = V_htons((unsigned short)net_hostport);

	Con_Printf("Winsock TCP/IP Initialized\n");
	tcpipAvailable = true;

	return net_controlsocket;
}

void VLAN_Listen (qboolean state);

// net_wins.c: WINS_Shutdown
void VLAN_Shutdown (void)
{
	VLAN_Listen (false);
	VLAN_CloseSocket (net_controlsocket);
}

// net_wins.c: WINS_Listen
void VLAN_Listen (qboolean state)
{
	// enable listening
	if (state)
	{
		if (net_acceptsocket != -1)
			return;
		VLAN_GetLocalAddress();
		if ((net_acceptsocket = VLAN_OpenSocket (net_hostport)) == -1)
			Sys_Error ("WINS_Listen: Unable to open accept socket\n");
		return;
	}

	// disable listening
	if (net_acceptsocket == -1)
		return;
	VLAN_CloseSocket (net_acceptsocket);
	net_acceptsocket = -1;
}

// net_wins.c: WINS_OpenSocket (a socket is a number; binding never fails)
int VLAN_OpenSocket (int port)
{
	return virtual_nextsocket++;
}

// net_wins.c: WINS_CloseSocket
int VLAN_CloseSocket (int socket)
{
	if (socket == net_broadcastsocket)
		net_broadcastsocket = 0;
	return 0;
}

// net_wins.c: WINS_Connect
int VLAN_Connect (int socket, struct qsockaddr *addr)
{
	return 0;
}

// net_wins.c: WINS_CheckNewConnections (nothing ever arrives)
int VLAN_CheckNewConnections (void)
{
	if (net_acceptsocket == -1)
		return -1;
	return -1;
}

// net_wins.c: WINS_Read (nothing ever arrives)
int VLAN_Read (int socket, byte *buf, int len, struct qsockaddr *addr)
{
	return 0;
}

// net_wins.c: WINS_MakeSocketBroadcastCapable
static int VLAN_MakeSocketBroadcastCapable (int socket)
{
	net_broadcastsocket = socket;
	return 0;
}

// net_wins.c: WINS_Broadcast
int VLAN_Broadcast (int socket, byte *buf, int len)
{
	int ret;

	if (socket != net_broadcastsocket)
	{
		if (net_broadcastsocket != 0)
			Sys_Error("Attempted to use multiple broadcasts sockets\n");
		VLAN_GetLocalAddress();
		ret = VLAN_MakeSocketBroadcastCapable (socket);
		if (ret == -1)
		{
			Con_Printf("Unable to make socket broadcast capable\n");
			return ret;
		}
	}

	return VLAN_Write (socket, buf, len, &broadcastaddr);
}

// net_wins.c: WINS_Write (sent, to nowhere)
int VLAN_Write (int socket, byte *buf, int len, struct qsockaddr *addr)
{
	return len;
}

// net_wins.c: WINS_AddrToString
char *VLAN_AddrToString (struct qsockaddr *addr)
{
	static char buffer[22];
	int haddr;

	haddr = V_ntohl(((virtual_sockaddr_t *)addr)->sin_addr);
	sprintf(buffer, "%d.%d.%d.%d:%d", (haddr >> 24) & 0xff, (haddr >> 16) & 0xff, (haddr >> 8) & 0xff, haddr & 0xff, V_ntohs(((virtual_sockaddr_t *)addr)->sin_port));
	return buffer;
}

// net_wins.c: WINS_StringToAddr
int VLAN_StringToAddr (char *string, struct qsockaddr *addr)
{
	int ha1, ha2, ha3, ha4, hp;
	int ipaddr;

	ha1 = ha2 = ha3 = ha4 = hp = 0;	// what sscanf does not reach: zeros rather than the C's stack
	sscanf(string, "%d.%d.%d.%d:%d", &ha1, &ha2, &ha3, &ha4, &hp);
	ipaddr = (ha1 << 24) | (ha2 << 16) | (ha3 << 8) | ha4;

	addr->sa_family = VIRTUAL_AF_INET;
	((virtual_sockaddr_t *)addr)->sin_addr = V_htonl(ipaddr);
	((virtual_sockaddr_t *)addr)->sin_port = V_htons((unsigned short)hp);
	return 0;
}

// net_wins.c: WINS_GetSocketAddr (every socket is bound to the machine's address, port 0)
int VLAN_GetSocketAddr (int socket, struct qsockaddr *addr)
{
	Q_memset(addr, 0, sizeof(struct qsockaddr));
	((virtual_sockaddr_t *)addr)->sin_family = VIRTUAL_AF_INET;
	((virtual_sockaddr_t *)addr)->sin_addr = myAddr;
	return 0;
}

// net_wins.c: WINS_GetNameFromAddr (no name is known)
int VLAN_GetNameFromAddr (struct qsockaddr *addr, char *name)
{
	Q_strcpy (name, VLAN_AddrToString (addr));
	return 0;
}

// net_wins.c: WINS_GetAddrFromName (nothing resolves, not even a number: a connection would wait for an answer)
int VLAN_GetAddrFromName (char *name, struct qsockaddr *addr)
{
	return -1;
}

// net_wins.c: WINS_AddrCompare
int VLAN_AddrCompare (struct qsockaddr *addr1, struct qsockaddr *addr2)
{
	if (addr1->sa_family != addr2->sa_family)
		return -1;

	if (((virtual_sockaddr_t *)addr1)->sin_addr != ((virtual_sockaddr_t *)addr2)->sin_addr)
		return -1;

	if (((virtual_sockaddr_t *)addr1)->sin_port != ((virtual_sockaddr_t *)addr2)->sin_port)
		return 1;

	return 0;
}

// net_wins.c: WINS_GetSocketPort
int VLAN_GetSocketPort (struct qsockaddr *addr)
{
	return V_ntohs(((virtual_sockaddr_t *)addr)->sin_port);
}

// net_wins.c: WINS_SetSocketPort
int VLAN_SetSocketPort (struct qsockaddr *addr, int port)
{
	((virtual_sockaddr_t *)addr)->sin_port = V_htons((unsigned short)port);
	return 0;
}

net_landriver_t	net_landrivers[MAX_NET_DRIVERS] =
{
	{
	"Winsock TCPIP",
	false,
	0,
	VLAN_Init,
	VLAN_Shutdown,
	VLAN_Listen,
	VLAN_OpenSocket,
	VLAN_CloseSocket,
	VLAN_Connect,
	VLAN_CheckNewConnections,
	VLAN_Read,
	VLAN_Write,
	VLAN_Broadcast,
	VLAN_AddrToString,
	VLAN_StringToAddr,
	VLAN_GetSocketAddr,
	VLAN_GetNameFromAddr,
	VLAN_GetAddrFromName,
	VLAN_AddrCompare,
	VLAN_GetSocketPort,
	VLAN_SetSocketPort
	}
};

int net_numlandrivers = 1;
