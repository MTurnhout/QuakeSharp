using QuakeSharp.Core.Net;

namespace QuakeSharp.Core;

// net_win.c: WinQuake's network drivers, loopback then datagram, and the datagram driver's LAN drivers, net_wins.c's
// TCP/IP then net_wipx.c's IPX, as the C initializes them statically
public sealed partial class QuakeEngine
{
    private void NetWin_Drivers()
    {
        net_drivers =
        [
            new()
            {
                name = "Loopback", Init = Loop_Init, Listen = Loop_Listen, SearchForHosts = Loop_SearchForHosts,
                Connect = Loop_Connect, CheckNewConnections = Loop_CheckNewConnections, QGetMessage = Loop_GetMessage,
                QSendMessage = Loop_SendMessage, SendUnreliableMessage = Loop_SendUnreliableMessage,
                CanSendMessage = Loop_CanSendMessage, CanSendUnreliableMessage = Loop_CanSendUnreliableMessage,
                Close = Loop_Close, Shutdown = Loop_Shutdown,
            },
            new()
            {
                name = "Datagram", Init = Datagram_Init, Listen = Datagram_Listen, SearchForHosts = Datagram_SearchForHosts,
                Connect = Datagram_Connect, CheckNewConnections = Datagram_CheckNewConnections,
                QGetMessage = Datagram_GetMessage, QSendMessage = Datagram_SendMessage,
                SendUnreliableMessage = Datagram_SendUnreliableMessage, CanSendMessage = Datagram_CanSendMessage,
                CanSendUnreliableMessage = Datagram_CanSendUnreliableMessage, Close = Datagram_Close,
                Shutdown = Datagram_Shutdown,
            },
        ];
        net_numdrivers = 2;

        net_landrivers =
        [
            new()
            {
                name = "Winsock TCPIP", Init = WINS_Init, Shutdown = WINS_Shutdown, Listen = WINS_Listen,
                OpenSocket = WINS_OpenSocket, CloseSocket = WINS_CloseSocket, Connect = WINS_Connect,
                CheckNewConnections = WINS_CheckNewConnections, Read = WINS_Read, Write = WINS_Write,
                Broadcast = WINS_Broadcast, AddrToString = WINS_AddrToString, StringToAddr = WINS_StringToAddr,
                GetSocketAddr = WINS_GetSocketAddr, GetNameFromAddr = WINS_GetNameFromAddr,
                GetAddrFromName = WINS_GetAddrFromName, AddrCompare = WINS_AddrCompare,
                GetSocketPort = WINS_GetSocketPort, SetSocketPort = WINS_SetSocketPort,
            },
            new()
            {
                name = "Winsock IPX", Init = WIPX_Init, Shutdown = WIPX_Shutdown, Listen = WIPX_Listen,
                OpenSocket = WIPX_OpenSocket, CloseSocket = WIPX_CloseSocket, Connect = WIPX_Connect,
                CheckNewConnections = WIPX_CheckNewConnections, Read = WIPX_Read, Write = WIPX_Write,
                Broadcast = WIPX_Broadcast, AddrToString = WIPX_AddrToString, StringToAddr = WIPX_StringToAddr,
                GetSocketAddr = WIPX_GetSocketAddr, GetNameFromAddr = WIPX_GetNameFromAddr,
                GetAddrFromName = WIPX_GetAddrFromName, AddrCompare = WIPX_AddrCompare,
                GetSocketPort = WIPX_GetSocketPort, SetSocketPort = WIPX_SetSocketPort,
            },
        ];
        net_numlandrivers = 2;
    }
}
