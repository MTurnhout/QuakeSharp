using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Net;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Net.NetDef;

namespace QuakeSharp.Core;

// net_loop.c: the loopback driver, joining a listen server and its local client
public sealed partial class QuakeEngine
{
    public bool localconnectpending;
    public QSocket? loop_client;
    public QSocket? loop_server;

    // net_loop.c: Loop_Init
    public int Loop_Init()
    {
        if (cls.state == cactive_t.ca_dedicated)
        {
            return -1;
        }

        return 0;
    }

    // net_loop.c: Loop_Shutdown
    public void Loop_Shutdown()
    {
    }

    // net_loop.c: Loop_Listen
    public void Loop_Listen(bool state)
    {
    }

    // net_loop.c: Loop_SearchForHosts
    public void Loop_SearchForHosts(bool xmit)
    {
        if (!sv.active)
        {
            return;
        }

        hostCacheCount = 1;
        if (Q_strcmp(hostname.@string, "UNNAMED") == 0)
        {
            hostcache[0].name = "local";
        }
        else
        {
            hostcache[0].name = hostname.@string;
        }

        hostcache[0].map = sv.name;
        hostcache[0].users = net_activeconnections;
        hostcache[0].maxusers = svs.maxclients;
        hostcache[0].driver = net_driverlevel;
        hostcache[0].cname = "local";
    }

    // net_loop.c: Loop_Connect
    public QSocket? Loop_Connect(string? host)
    {
        if (host == null || Q_strcmp(host, "local") != 0)
        {
            return null;
        }

        localconnectpending = true;

        if (loop_client == null)
        {
            if ((loop_client = NET_NewQSocket()) == null)
            {
                Con_Printf("Loop_Connect: no qsocket available\n");
                return null;
            }

            loop_client.address = "localhost";
        }

        loop_client.receiveMessageLength = 0;
        loop_client.sendMessageLength = 0;
        loop_client.canSend = true;

        if (loop_server == null)
        {
            if ((loop_server = NET_NewQSocket()) == null)
            {
                Con_Printf("Loop_Connect: no qsocket available\n");
                return null;
            }

            loop_server.address = "LOCAL";
        }

        loop_server.receiveMessageLength = 0;
        loop_server.sendMessageLength = 0;
        loop_server.canSend = true;

        loop_client.driverdata = loop_server;
        loop_server.driverdata = loop_client;

        return loop_client;
    }

    // net_loop.c: Loop_CheckNewConnections
    public QSocket? Loop_CheckNewConnections()
    {
        if (!localconnectpending)
        {
            return null;
        }

        localconnectpending = false;
        loop_server!.sendMessageLength = 0;
        loop_server.receiveMessageLength = 0;
        loop_server.canSend = true;
        loop_client!.sendMessageLength = 0;
        loop_client.receiveMessageLength = 0;
        loop_client.canSend = true;
        return loop_server;
    }

    // net_loop.c: IntAlign
    private static int IntAlign(int value) => (value + (sizeof(int) - 1)) & ~(sizeof(int) - 1);

    // net_loop.c: Loop_GetMessage
    public int Loop_GetMessage(QSocket sock)
    {
        int ret;
        int length;

        if (sock.receiveMessageLength == 0)
        {
            return 0;
        }

        ret = sock.receiveMessage[0];
        length = sock.receiveMessage[1] + (sock.receiveMessage[2] << 8);

        // alignment byte skipped here
        SZ_Clear(net_message);
        SZ_Write(net_message, sock.receiveMessage.AsSpan(4, length));

        length = IntAlign(length + 4);
        sock.receiveMessageLength -= length;

        if (sock.receiveMessageLength != 0)
        {
            Array.Copy(sock.receiveMessage, length, sock.receiveMessage, 0, sock.receiveMessageLength);
        }

        if (sock.driverdata != null && ret == 1)
        {
            sock.driverdata.canSend = true;
        }

        return ret;
    }

    // net_loop.c: Loop_SendMessage
    public int Loop_SendMessage(QSocket sock, SizeBuf data)
    {
        QSocket? other = sock.driverdata;
        int buffer;

        if (other == null)
        {
            return -1;
        }

        if (other.receiveMessageLength + data.cursize + 4 > NET_MAXMESSAGE)
        {
            throw Sys_Error("Loop_SendMessage: overflow\n");
        }

        buffer = other.receiveMessageLength;

        // message type
        other.receiveMessage[buffer++] = 1;

        // length
        other.receiveMessage[buffer++] = (byte)(data.cursize & 0xff);
        other.receiveMessage[buffer++] = (byte)(data.cursize >> 8);

        // align
        buffer++;

        // message
        Array.Copy(data.data, 0, other.receiveMessage, buffer, data.cursize);
        other.receiveMessageLength = IntAlign(other.receiveMessageLength + data.cursize + 4);

        sock.canSend = false;
        return 1;
    }

    // net_loop.c: Loop_SendUnreliableMessage
    public int Loop_SendUnreliableMessage(QSocket sock, SizeBuf data)
    {
        QSocket? other = sock.driverdata;
        int buffer;

        if (other == null)
        {
            return -1;
        }

        // sizeof(byte) + sizeof(short)
        if (other.receiveMessageLength + data.cursize + 1 + 2 > NET_MAXMESSAGE)
        {
            return 0;
        }

        buffer = other.receiveMessageLength;

        // message type
        other.receiveMessage[buffer++] = 2;

        // length
        other.receiveMessage[buffer++] = (byte)(data.cursize & 0xff);
        other.receiveMessage[buffer++] = (byte)(data.cursize >> 8);

        // align
        buffer++;

        // message (past the end of receiveMessage when the length check above let it through by 1: the C writes
        // into the next fields; never with Quake's message sizes)
        Array.Copy(data.data, 0, other.receiveMessage, buffer, data.cursize);
        other.receiveMessageLength = IntAlign(other.receiveMessageLength + data.cursize + 4);
        return 1;
    }

    // net_loop.c: Loop_CanSendMessage
    public bool Loop_CanSendMessage(QSocket sock)
    {
        if (sock.driverdata == null)
        {
            return false;
        }

        return sock.canSend;
    }

    // net_loop.c: Loop_CanSendUnreliableMessage
    public bool Loop_CanSendUnreliableMessage(QSocket sock) => true;

    // net_loop.c: Loop_Close
    public void Loop_Close(QSocket sock)
    {
        if (sock.driverdata != null)
        {
            sock.driverdata.driverdata = null;
        }

        sock.receiveMessageLength = 0;
        sock.sendMessageLength = 0;
        sock.canSend = true;
        if (sock == loop_client)
        {
            loop_client = null;
        }
        else
        {
            loop_server = null;
        }
    }
}
