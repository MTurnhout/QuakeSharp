using System.Buffers.Binary;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Net;
using QuakeSharp.Core.Server;
using QuakeSharp.Core.UI;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Net.NetDef;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// net_dgrm.c: the datagram driver, Quake's protocol over a LAN driver (net_landrivers: the virtual TCP/IP driver,
// decision 33). BAN_TEST is defined, as in WinQuake.
public sealed partial class QuakeEngine
{
    /// <summary>net_win.c: net_landrivers</summary>
    public NetLanDriver[] net_landrivers = [];

    public int net_numlandrivers;

    private int net_landriverlevel;

    // statistic counters
    public int packetsSent;
    public int packetsReSent;
    public int packetsReceived;
    public int receivedDuplicateCount;
    public int shortPacketCount;
    public int droppedDatagrams;

    private int myDriverLevel;

    // net_dgrm.c: packetBuffer { unsigned int length; unsigned int sequence; byte data[MAX_DATAGRAM]; }, the length and
    // sequence in network byte order
    private readonly byte[] packetBuffer = new byte[NET_HEADERSIZE + MAX_DATAGRAM];

    // net_dgrm.c: banAddr, banMask (in_addr values: the address's bytes in memory order)
    public uint banAddr;
    public uint banMask = 0xffffffff;

    private NetLanDriver dfunc => net_landrivers[net_landriverlevel];

    private NetLanDriver sfunc(QSocket sock) => net_landrivers[sock.landriver];

    // the length and sequence of packetBuffer (BigLong: network byte order in memory)
    private void SetPacketHeader(uint length, uint sequence)
    {
        BinaryPrimitives.WriteUInt32BigEndian(packetBuffer, length);
        BinaryPrimitives.WriteUInt32BigEndian(packetBuffer.AsSpan(4), sequence);
    }

    // *((int *)net_message.data) = BigLong(NETFLAG_CTL | (net_message.cursize & NETFLAG_LENGTH_MASK))
    private void SetControlHeader() =>
        BinaryPrimitives.WriteInt32BigEndian(net_message.data, NETFLAG_CTL | (net_message.cursize & NETFLAG_LENGTH_MASK));

    // control = BigLong(*((int *)net_message.data))
    private int ControlHeader() => BinaryPrimitives.ReadInt32BigEndian(net_message.data);

    // winsock's inet_addr: "a.b.c.d" (each part decimal, octal with 0 or hexadecimal with 0x; fewer parts fill the last
    // one's low bytes, as the BSD function does) to the address's bytes in memory order, INADDR_NONE when invalid
    private static uint inet_addr(string cp)
    {
        const uint INADDR_NONE = 0xffffffff;
        var parts = new List<uint>();
        int i = 0;

        while (true)
        {
            if (i >= cp.Length || !char.IsAsciiDigit(cp[i]))
            {
                return INADDR_NONE;
            }

            uint value = 0;
            uint radix = 10;
            if (cp[i] == '0')
            {
                radix = 8;
                i++;
                if (i < cp.Length && (cp[i] == 'x' || cp[i] == 'X'))
                {
                    radix = 16;
                    i++;
                }
            }

            while (i < cp.Length)
            {
                int d = char.IsAsciiDigit(cp[i]) ? cp[i] - '0'
                    : radix == 16 && char.IsAsciiHexDigit(cp[i]) ? (cp[i] | 0x20) - 'a' + 10 : -1;
                if (d < 0 || d >= radix)
                {
                    break;
                }

                value = unchecked((value * radix) + (uint)d);
                i++;
            }

            parts.Add(value);
            if (i < cp.Length && cp[i] == '.' && parts.Count < 4)
            {
                i++;
                continue;
            }

            break;
        }

        if (i < cp.Length && cp[i] != ' ')
        {
            return INADDR_NONE;
        }

        uint host = parts.Count switch
        {
            1 => parts[0],
            2 when parts[0] <= 0xff && parts[1] <= 0xffffff => (parts[0] << 24) | parts[1],
            3 when parts[0] <= 0xff && parts[1] <= 0xff && parts[2] <= 0xffff => (parts[0] << 24) | (parts[1] << 16) | parts[2],
            4 when parts.TrueForAll(p => p <= 0xff) => (parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | parts[3],
            _ => INADDR_NONE,
        };
        return host == INADDR_NONE ? INADDR_NONE : BinaryPrimitives.ReverseEndianness(host);
    }

    // winsock's inet_ntoa: an address (its bytes in memory order) as "a.b.c.d"
    private static string inet_ntoa(uint addr) =>
        CFormat.Sprintf("%d.%d.%d.%d", (int)(addr & 0xff), (int)((addr >> 8) & 0xff), (int)((addr >> 16) & 0xff), (int)(addr >> 24));

    // net_dgrm.c: NET_Ban_f
    public void NET_Ban_f()
    {
        string addrStr;
        string maskStr;
        Action<string, PrintfArg[]> print;

        if (cmd_source == cmd_source_t.src_command)
        {
            if (!sv.active)
            {
                Cmd_ForwardToServer();
                return;
            }

            print = (fmt, args) => Con_Printf(fmt, args);
        }
        else
        {
            if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
            {
                return;
            }

            print = (fmt, args) => SV_ClientPrintf(fmt, args);
        }

        switch (Cmd_Argc())
        {
            case 1:
                if (banAddr != 0)
                {
                    addrStr = inet_ntoa(banAddr);
                    maskStr = inet_ntoa(banMask);
                    print("Banning %s [%s]\n", [addrStr, maskStr]);
                }
                else
                {
                    print("Banning not active\n", []);
                }

                break;

            case 2:
                if (Q_strcasecmp(Cmd_Argv(1), "off") == 0)
                {
                    banAddr = 0x00000000;
                }
                else
                {
                    banAddr = inet_addr(Cmd_Argv(1));
                }

                banMask = 0xffffffff;
                break;

            case 3:
                banAddr = inet_addr(Cmd_Argv(1));
                banMask = inet_addr(Cmd_Argv(2));
                break;

            default:
                print("BAN ip_address [mask]\n", []);
                break;
        }
    }

    // net_dgrm.c: Datagram_SendMessage
    public int Datagram_SendMessage(QSocket sock, SizeBuf data)
    {
        uint packetLen;
        uint dataLen;
        uint eom;

        Array.Copy(data.data, sock.sendMessage, data.cursize);
        sock.sendMessageLength = data.cursize;

        if (data.cursize <= MAX_DATAGRAM)
        {
            dataLen = (uint)data.cursize;
            eom = NETFLAG_EOM;
        }
        else
        {
            dataLen = MAX_DATAGRAM;
            eom = 0;
        }

        packetLen = NET_HEADERSIZE + dataLen;

        SetPacketHeader(packetLen | (NETFLAG_DATA | eom), sock.sendSequence++);
        Array.Copy(sock.sendMessage, 0, packetBuffer, NET_HEADERSIZE, dataLen);

        sock.canSend = false;

        if (sfunc(sock).Write(sock.socket, packetBuffer, (int)packetLen, sock.addr) == -1)
        {
            return -1;
        }

        sock.lastSendTime = net_time;
        packetsSent++;
        return 1;
    }

    // net_dgrm.c: SendMessageNext
    public int SendMessageNext(QSocket sock)
    {
        uint packetLen;
        uint dataLen;
        uint eom;

        if (sock.sendMessageLength <= MAX_DATAGRAM)
        {
            dataLen = (uint)sock.sendMessageLength;
            eom = NETFLAG_EOM;
        }
        else
        {
            dataLen = MAX_DATAGRAM;
            eom = 0;
        }

        packetLen = NET_HEADERSIZE + dataLen;

        SetPacketHeader(packetLen | (NETFLAG_DATA | eom), sock.sendSequence++);
        Array.Copy(sock.sendMessage, 0, packetBuffer, NET_HEADERSIZE, dataLen);

        sock.sendNext = false;

        if (sfunc(sock).Write(sock.socket, packetBuffer, (int)packetLen, sock.addr) == -1)
        {
            return -1;
        }

        sock.lastSendTime = net_time;
        packetsSent++;
        return 1;
    }

    // net_dgrm.c: ReSendMessage
    public int ReSendMessage(QSocket sock)
    {
        uint packetLen;
        uint dataLen;
        uint eom;

        if (sock.sendMessageLength <= MAX_DATAGRAM)
        {
            dataLen = (uint)sock.sendMessageLength;
            eom = NETFLAG_EOM;
        }
        else
        {
            dataLen = MAX_DATAGRAM;
            eom = 0;
        }

        packetLen = NET_HEADERSIZE + dataLen;

        SetPacketHeader(packetLen | (NETFLAG_DATA | eom), sock.sendSequence - 1);
        Array.Copy(sock.sendMessage, 0, packetBuffer, NET_HEADERSIZE, dataLen);

        sock.sendNext = false;

        if (sfunc(sock).Write(sock.socket, packetBuffer, (int)packetLen, sock.addr) == -1)
        {
            return -1;
        }

        sock.lastSendTime = net_time;
        packetsReSent++;
        return 1;
    }

    // net_dgrm.c: Datagram_CanSendMessage
    public bool Datagram_CanSendMessage(QSocket sock)
    {
        if (sock.sendNext)
        {
            SendMessageNext(sock);
        }

        return sock.canSend;
    }

    // net_dgrm.c: Datagram_CanSendUnreliableMessage
    public bool Datagram_CanSendUnreliableMessage(QSocket sock) => true;

    // net_dgrm.c: Datagram_SendUnreliableMessage
    public int Datagram_SendUnreliableMessage(QSocket sock, SizeBuf data)
    {
        int packetLen;

        packetLen = NET_HEADERSIZE + data.cursize;

        SetPacketHeader((uint)packetLen | NETFLAG_UNRELIABLE, sock.unreliableSendSequence++);
        Array.Copy(data.data, 0, packetBuffer, NET_HEADERSIZE, data.cursize);

        if (sfunc(sock).Write(sock.socket, packetBuffer, packetLen, sock.addr) == -1)
        {
            return -1;
        }

        packetsSent++;
        return 1;
    }

    // net_dgrm.c: Datagram_GetMessage
    public int Datagram_GetMessage(QSocket sock)
    {
        uint length;
        uint flags;
        int ret = 0;
        QSockAddr readaddr = default;
        uint sequence;
        uint count;

        if (!sock.canSend)
        {
            if (net_time - sock.lastSendTime > 1.0)
            {
                ReSendMessage(sock);
            }
        }

        while (true)
        {
            length = (uint)sfunc(sock).Read(sock.socket, packetBuffer, NET_DATAGRAMSIZE, ref readaddr);

            if (length == 0)
            {
                break;
            }

            if (length == unchecked((uint)-1))
            {
                Con_Printf("Read error\n");
                return -1;
            }

            if (sfunc(sock).AddrCompare(readaddr, sock.addr) != 0)
            {
                continue;
            }

            if (length < NET_HEADERSIZE)
            {
                shortPacketCount++;
                continue;
            }

            length = BinaryPrimitives.ReadUInt32BigEndian(packetBuffer);
            flags = length & unchecked((uint)~NETFLAG_LENGTH_MASK);
            length &= NETFLAG_LENGTH_MASK;

            if ((flags & unchecked((uint)NETFLAG_CTL)) != 0)
            {
                continue;
            }

            sequence = BinaryPrimitives.ReadUInt32BigEndian(packetBuffer.AsSpan(4));
            packetsReceived++;

            if ((flags & NETFLAG_UNRELIABLE) != 0)
            {
                if (sequence < sock.unreliableReceiveSequence)
                {
                    Con_DPrintf("Got a stale datagram\n");
                    ret = 0;
                    break;
                }

                if (sequence != sock.unreliableReceiveSequence)
                {
                    count = sequence - sock.unreliableReceiveSequence;
                    droppedDatagrams += (int)count;
                    Con_DPrintf("Dropped %u datagram(s)\n", count);
                }

                sock.unreliableReceiveSequence = sequence + 1;

                length -= NET_HEADERSIZE;

                SZ_Clear(net_message);
                SZ_Write(net_message, packetBuffer.AsSpan(NET_HEADERSIZE, (int)length));

                ret = 2;
                break;
            }

            if ((flags & NETFLAG_ACK) != 0)
            {
                if (sequence != sock.sendSequence - 1)
                {
                    Con_DPrintf("Stale ACK received\n");
                    continue;
                }

                if (sequence == sock.ackSequence)
                {
                    sock.ackSequence++;
                    if (sock.ackSequence != sock.sendSequence)
                    {
                        Con_DPrintf("ack sequencing error\n");
                    }
                }
                else
                {
                    Con_DPrintf("Duplicate ACK received\n");
                    continue;
                }

                sock.sendMessageLength -= MAX_DATAGRAM;
                if (sock.sendMessageLength > 0)
                {
                    Array.Copy(sock.sendMessage, MAX_DATAGRAM, sock.sendMessage, 0, sock.sendMessageLength);
                    sock.sendNext = true;
                }
                else
                {
                    sock.sendMessageLength = 0;
                    sock.canSend = true;
                }

                continue;
            }

            if ((flags & NETFLAG_DATA) != 0)
            {
                SetPacketHeader(NET_HEADERSIZE | NETFLAG_ACK, sequence);
                sfunc(sock).Write(sock.socket, packetBuffer, NET_HEADERSIZE, readaddr);

                if (sequence != sock.receiveSequence)
                {
                    receivedDuplicateCount++;
                    continue;
                }

                sock.receiveSequence++;

                length -= NET_HEADERSIZE;

                if ((flags & NETFLAG_EOM) != 0)
                {
                    SZ_Clear(net_message);
                    SZ_Write(net_message, sock.receiveMessage.AsSpan(0, sock.receiveMessageLength));
                    SZ_Write(net_message, packetBuffer.AsSpan(NET_HEADERSIZE, (int)length));
                    sock.receiveMessageLength = 0;

                    ret = 1;
                    break;
                }

                Array.Copy(packetBuffer, NET_HEADERSIZE, sock.receiveMessage, sock.receiveMessageLength, length);
                sock.receiveMessageLength += (int)length;
                continue;
            }
        }

        if (sock.sendNext)
        {
            SendMessageNext(sock);
        }

        return ret;
    }

    // net_dgrm.c: PrintStats
    public void PrintStats(QSocket s)
    {
        Con_Printf("canSend = %4u   \n", s.canSend ? 1 : 0);
        Con_Printf("sendSeq = %4u   ", s.sendSequence);
        Con_Printf("recvSeq = %4u   \n", s.receiveSequence);
        Con_Printf("\n");
    }

    // net_dgrm.c: NET_Stats_f
    public void NET_Stats_f()
    {
        QSocket? s;

        if (Cmd_Argc() == 1)
        {
            Con_Printf("unreliable messages sent   = %i\n", unreliableMessagesSent);
            Con_Printf("unreliable messages recv   = %i\n", unreliableMessagesReceived);
            Con_Printf("reliable messages sent     = %i\n", messagesSent);
            Con_Printf("reliable messages received = %i\n", messagesReceived);
            Con_Printf("packetsSent                = %i\n", packetsSent);
            Con_Printf("packetsReSent              = %i\n", packetsReSent);
            Con_Printf("packetsReceived            = %i\n", packetsReceived);
            Con_Printf("receivedDuplicateCount     = %i\n", receivedDuplicateCount);
            Con_Printf("shortPacketCount           = %i\n", shortPacketCount);
            Con_Printf("droppedDatagrams           = %i\n", droppedDatagrams);
        }
        else if (Q_strcmp(Cmd_Argv(1), "*") == 0)
        {
            for (s = net_activeSockets; s != null; s = s.next)
            {
                PrintStats(s);
            }

            for (s = net_freeSockets; s != null; s = s.next)
            {
                PrintStats(s);
            }
        }
        else
        {
            for (s = net_activeSockets; s != null; s = s.next)
            {
                if (Q_strcasecmp(Cmd_Argv(1), s.address) == 0)
                {
                    break;
                }
            }

            if (s == null)
            {
                for (s = net_freeSockets; s != null; s = s.next)
                {
                    if (Q_strcasecmp(Cmd_Argv(1), s.address) == 0)
                    {
                        break;
                    }
                }
            }

            if (s == null)
            {
                return;
            }

            PrintStats(s);
        }
    }

    private bool testInProgress;
    private int testPollCount;
    private int testDriver;
    private int testSocket;
    private PollProcedure testPollProcedure = null!;

    // net_dgrm.c: Test_Poll
    private void Test_Poll()
    {
        QSockAddr clientaddr = default;
        int control;
        int len;
        string name;
        string address;
        int colors;
        int frags;
        int connectTime;

        net_landriverlevel = testDriver;

        while (true)
        {
            len = dfunc.Read(testSocket, net_message.data, net_message.maxsize, ref clientaddr);
            if ((uint)len < sizeof(int))
            {
                break;
            }

            net_message.cursize = len;

            MSG_BeginReading();
            control = ControlHeader();
            MSG_ReadLong();
            if (control == -1)
            {
                break;
            }

            if ((control & ~NETFLAG_LENGTH_MASK) != NETFLAG_CTL)
            {
                break;
            }

            if ((control & NETFLAG_LENGTH_MASK) != len)
            {
                break;
            }

            if (MSG_ReadByte() != CCREP_PLAYER_INFO)
            {
                throw Sys_Error("Unexpected repsonse to Player Info request\n");
            }

            MSG_ReadByte(); // playerNumber
            name = MSG_ReadString();
            colors = MSG_ReadLong();
            frags = MSG_ReadLong();
            connectTime = MSG_ReadLong();
            address = MSG_ReadString();

            Con_Printf("%s\n  frags:%3i  colors:%u %u  time:%u\n  %s\n", name, frags, colors >> 4, colors & 0x0f, connectTime / 60, address);
        }

        testPollCount--;
        if (testPollCount != 0)
        {
            SchedulePollProcedure(testPollProcedure, 0.1);
        }
        else
        {
            dfunc.CloseSocket(testSocket);
            testInProgress = false;
        }
    }

    // net_dgrm.c: Test_f
    private void Test_f()
    {
        string host;
        int n;
        int max = MAX_SCOREBOARD;
        QSockAddr sendaddr = default;

        if (testInProgress)
        {
            return;
        }

        host = Cmd_Argv(1);

        if (hostCacheCount != 0)
        {
            for (n = 0; n < hostCacheCount; n++)
            {
                if (Q_strcasecmp(host, hostcache[n].name) == 0)
                {
                    if (hostcache[n].driver != myDriverLevel)
                    {
                        continue;
                    }

                    net_landriverlevel = hostcache[n].ldriver;
                    max = hostcache[n].maxusers;
                    sendaddr = hostcache[n].addr;
                    break;
                }
            }

            if (n < hostCacheCount)
            {
                goto JustDoIt;
            }
        }

        for (net_landriverlevel = 0; net_landriverlevel < net_numlandrivers; net_landriverlevel++)
        {
            if (!net_landrivers[net_landriverlevel].initialized)
            {
                continue;
            }

            // see if we can resolve the host name
            if (dfunc.GetAddrFromName(host, ref sendaddr) != -1)
            {
                break;
            }
        }

        if (net_landriverlevel == net_numlandrivers)
        {
            return;
        }

    JustDoIt:
        testSocket = dfunc.OpenSocket(0);
        if (testSocket == -1)
        {
            return;
        }

        testInProgress = true;
        testPollCount = 20;
        testDriver = net_landriverlevel;

        for (n = 0; n < max; n++)
        {
            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREQ_PLAYER_INFO);
            MSG_WriteByte(net_message, n);
            SetControlHeader();
            dfunc.Write(testSocket, net_message.data, net_message.cursize, sendaddr);
        }

        SZ_Clear(net_message);
        SchedulePollProcedure(testPollProcedure, 0.1);
    }

    private bool test2InProgress;
    private int test2Driver;
    private int test2Socket;
    private PollProcedure test2PollProcedure = null!;

    // net_dgrm.c: Test2_Poll
    private void Test2_Poll()
    {
        QSockAddr clientaddr = default;
        int control;
        int len;
        string name;
        string value;

        net_landriverlevel = test2Driver;

        len = dfunc.Read(test2Socket, net_message.data, net_message.maxsize, ref clientaddr);
        if ((uint)len < sizeof(int))
        {
            goto Reschedule;
        }

        net_message.cursize = len;

        MSG_BeginReading();
        control = ControlHeader();
        MSG_ReadLong();
        if (control == -1)
        {
            goto Error;
        }

        if ((control & ~NETFLAG_LENGTH_MASK) != NETFLAG_CTL)
        {
            goto Error;
        }

        if ((control & NETFLAG_LENGTH_MASK) != len)
        {
            goto Error;
        }

        if (MSG_ReadByte() != CCREP_RULE_INFO)
        {
            goto Error;
        }

        name = MSG_ReadString();
        if (name.Length == 0)
        {
            goto Done;
        }

        value = MSG_ReadString();

        Con_Printf("%-16.16s  %-16.16s\n", name, value);

        SZ_Clear(net_message);

        // save space for the header, filled in later
        MSG_WriteLong(net_message, 0);
        MSG_WriteByte(net_message, CCREP_RULE_INFO);
        MSG_WriteString(net_message, name);
        SetControlHeader();
        dfunc.Write(test2Socket, net_message.data, net_message.cursize, clientaddr);
        SZ_Clear(net_message);

    Reschedule:
        SchedulePollProcedure(test2PollProcedure, 0.05);
        return;

    Error:
        Con_Printf("Unexpected repsonse to Rule Info request\n");
    Done:
        dfunc.CloseSocket(test2Socket);
        test2InProgress = false;
    }

    // net_dgrm.c: Test2_f
    private void Test2_f()
    {
        string host;
        int n;
        QSockAddr sendaddr = default;

        if (test2InProgress)
        {
            return;
        }

        host = Cmd_Argv(1);

        if (hostCacheCount != 0)
        {
            for (n = 0; n < hostCacheCount; n++)
            {
                if (Q_strcasecmp(host, hostcache[n].name) == 0)
                {
                    if (hostcache[n].driver != myDriverLevel)
                    {
                        continue;
                    }

                    net_landriverlevel = hostcache[n].ldriver;
                    sendaddr = hostcache[n].addr;
                    break;
                }
            }

            if (n < hostCacheCount)
            {
                goto JustDoIt;
            }
        }

        for (net_landriverlevel = 0; net_landriverlevel < net_numlandrivers; net_landriverlevel++)
        {
            if (!net_landrivers[net_landriverlevel].initialized)
            {
                continue;
            }

            // see if we can resolve the host name
            if (dfunc.GetAddrFromName(host, ref sendaddr) != -1)
            {
                break;
            }
        }

        if (net_landriverlevel == net_numlandrivers)
        {
            return;
        }

    JustDoIt:
        test2Socket = dfunc.OpenSocket(0);
        if (test2Socket == -1)
        {
            return;
        }

        test2InProgress = true;
        test2Driver = net_landriverlevel;

        SZ_Clear(net_message);

        // save space for the header, filled in later
        MSG_WriteLong(net_message, 0);
        MSG_WriteByte(net_message, CCREQ_RULE_INFO);
        MSG_WriteString(net_message, "");
        SetControlHeader();
        dfunc.Write(test2Socket, net_message.data, net_message.cursize, sendaddr);
        SZ_Clear(net_message);
        SchedulePollProcedure(test2PollProcedure, 0.05);
    }

    // net_dgrm.c: Datagram_Init
    public int Datagram_Init()
    {
        int i;
        int csock;

        myDriverLevel = net_driverlevel;
        Cmd_AddCommand("net_stats", NET_Stats_f);

        if (COM_CheckParm("-nolan") != 0)
        {
            return -1;
        }

        for (i = 0; i < net_numlandrivers; i++)
        {
            csock = net_landrivers[i].Init();
            if (csock == -1)
            {
                continue;
            }

            net_landrivers[i].initialized = true;
            net_landrivers[i].controlSock = csock;
        }

        Cmd_AddCommand("ban", NET_Ban_f);
        Cmd_AddCommand("test", Test_f);
        Cmd_AddCommand("test2", Test2_f);

        return 0;
    }

    // net_dgrm.c: Datagram_Shutdown
    public void Datagram_Shutdown()
    {
        int i;

        // shutdown the lan drivers
        for (i = 0; i < net_numlandrivers; i++)
        {
            if (net_landrivers[i].initialized)
            {
                net_landrivers[i].Shutdown();
                net_landrivers[i].initialized = false;
            }
        }
    }

    // net_dgrm.c: Datagram_Close
    public void Datagram_Close(QSocket sock) => sfunc(sock).CloseSocket(sock.socket);

    // net_dgrm.c: Datagram_Listen
    public void Datagram_Listen(bool state)
    {
        int i;

        for (i = 0; i < net_numlandrivers; i++)
        {
            if (net_landrivers[i].initialized)
            {
                net_landrivers[i].Listen(state);
            }
        }
    }

    // net_dgrm.c: _Datagram_CheckNewConnections
    private QSocket? _Datagram_CheckNewConnections()
    {
        QSockAddr clientaddr = default;
        QSockAddr newaddr = default;
        int newsock;
        int acceptsock;
        QSocket? sock;
        QSocket? s;
        int len;
        int command;
        int control;
        int ret;

        acceptsock = dfunc.CheckNewConnections();
        if (acceptsock == -1)
        {
            return null;
        }

        SZ_Clear(net_message);

        len = dfunc.Read(acceptsock, net_message.data, net_message.maxsize, ref clientaddr);
        if ((uint)len < sizeof(int))
        {
            return null;
        }

        net_message.cursize = len;

        MSG_BeginReading();
        control = ControlHeader();
        MSG_ReadLong();
        if (control == -1)
        {
            return null;
        }

        if ((control & ~NETFLAG_LENGTH_MASK) != NETFLAG_CTL)
        {
            return null;
        }

        if ((control & NETFLAG_LENGTH_MASK) != len)
        {
            return null;
        }

        command = MSG_ReadByte();
        if (command == CCREQ_SERVER_INFO)
        {
            if (Q_strcmp(MSG_ReadString(), "QUAKE") != 0)
            {
                return null;
            }

            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREP_SERVER_INFO);
            dfunc.GetSocketAddr(acceptsock, ref newaddr);
            MSG_WriteString(net_message, dfunc.AddrToString(newaddr));
            MSG_WriteString(net_message, hostname.@string);
            MSG_WriteString(net_message, sv.name);
            MSG_WriteByte(net_message, net_activeconnections);
            MSG_WriteByte(net_message, svs.maxclients);
            MSG_WriteByte(net_message, NET_PROTOCOL_VERSION);
            SetControlHeader();
            dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
            SZ_Clear(net_message);
            return null;
        }

        if (command == CCREQ_PLAYER_INFO)
        {
            int playerNumber;
            int activeNumber;
            int clientNumber;
            ServerClient client = svs.clients[0];

            playerNumber = MSG_ReadByte();
            activeNumber = -1;
            for (clientNumber = 0; clientNumber < svs.maxclients; clientNumber++)
            {
                client = svs.clients[clientNumber];
                if (client.active)
                {
                    activeNumber++;
                    if (activeNumber == playerNumber)
                    {
                        break;
                    }
                }
            }

            if (clientNumber == svs.maxclients)
            {
                return null;
            }

            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREP_PLAYER_INFO);
            MSG_WriteByte(net_message, playerNumber);
            MSG_WriteString(net_message, client.name);
            MSG_WriteLong(net_message, client.colors);
            MSG_WriteLong(net_message, (int)client.edict!.v.frags);
            MSG_WriteLong(net_message, (int)(net_time - client.netconnection!.connecttime));
            MSG_WriteString(net_message, client.netconnection.address);
            SetControlHeader();
            dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
            SZ_Clear(net_message);

            return null;
        }

        if (command == CCREQ_RULE_INFO)
        {
            string prevCvarName;
            Cvar? var;

            // find the search start location
            prevCvarName = MSG_ReadString();
            if (prevCvarName.Length != 0)
            {
                var = Cvar_FindVar(prevCvarName);
                if (var == null)
                {
                    return null;
                }

                var = var.next;
            }
            else
            {
                var = cvar_vars;
            }

            // search for the next server cvar
            while (var != null)
            {
                if (var.server)
                {
                    break;
                }

                var = var.next;
            }

            // send the response
            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREP_RULE_INFO);
            if (var != null)
            {
                MSG_WriteString(net_message, var.name);
                MSG_WriteString(net_message, var.@string);
            }

            SetControlHeader();
            dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
            SZ_Clear(net_message);

            return null;
        }

        if (command != CCREQ_CONNECT)
        {
            return null;
        }

        if (Q_strcmp(MSG_ReadString(), "QUAKE") != 0)
        {
            return null;
        }

        if (MSG_ReadByte() != NET_PROTOCOL_VERSION)
        {
            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREP_REJECT);
            MSG_WriteString(net_message, "Incompatible version.\n");
            SetControlHeader();
            dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
            SZ_Clear(net_message);
            return null;
        }

        // check for a ban (AF_INET)
        if (clientaddr.sa_family == 2)
        {
            uint testAddr;
            testAddr = BinaryPrimitives.ReadUInt32LittleEndian(clientaddr.sa_data[2..6]);
            if ((testAddr & banMask) == banAddr)
            {
                SZ_Clear(net_message);

                // save space for the header, filled in later
                MSG_WriteLong(net_message, 0);
                MSG_WriteByte(net_message, CCREP_REJECT);
                MSG_WriteString(net_message, "You have been banned.\n");
                SetControlHeader();
                dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
                SZ_Clear(net_message);
                return null;
            }
        }

        // see if this guy is already connected
        for (s = net_activeSockets; s != null; s = s.next)
        {
            if (s.driver != net_driverlevel)
            {
                continue;
            }

            ret = dfunc.AddrCompare(clientaddr, s.addr);
            if (ret >= 0)
            {
                // is this a duplicate connection reqeust?
                if (ret == 0 && net_time - s.connecttime < 2.0)
                {
                    // yes, so send a duplicate reply
                    SZ_Clear(net_message);

                    // save space for the header, filled in later
                    MSG_WriteLong(net_message, 0);
                    MSG_WriteByte(net_message, CCREP_ACCEPT);
                    dfunc.GetSocketAddr(s.socket, ref newaddr);
                    MSG_WriteLong(net_message, dfunc.GetSocketPort(newaddr));
                    SetControlHeader();
                    dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
                    SZ_Clear(net_message);
                    return null;
                }

                // it's somebody coming back in from a crash/disconnect
                // so close the old qsocket and let their retry get them back in
                NET_Close(s);
                return null;
            }
        }

        // allocate a QSocket
        sock = NET_NewQSocket();
        if (sock == null)
        {
            // no room; try to let him know
            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREP_REJECT);
            MSG_WriteString(net_message, "Server is full.\n");
            SetControlHeader();
            dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
            SZ_Clear(net_message);
            return null;
        }

        // allocate a network socket
        newsock = dfunc.OpenSocket(0);
        if (newsock == -1)
        {
            NET_FreeQSocket(sock);
            return null;
        }

        // connect to the client
        if (dfunc.Connect(newsock, clientaddr) == -1)
        {
            dfunc.CloseSocket(newsock);
            NET_FreeQSocket(sock);
            return null;
        }

        // everything is allocated, just fill in the details
        sock.socket = newsock;
        sock.landriver = net_landriverlevel;
        sock.addr = clientaddr;
        sock.address = dfunc.AddrToString(clientaddr);

        // send him back the info about the server connection he has been allocated
        SZ_Clear(net_message);

        // save space for the header, filled in later
        MSG_WriteLong(net_message, 0);
        MSG_WriteByte(net_message, CCREP_ACCEPT);
        dfunc.GetSocketAddr(newsock, ref newaddr);
        MSG_WriteLong(net_message, dfunc.GetSocketPort(newaddr));
        SetControlHeader();
        dfunc.Write(acceptsock, net_message.data, net_message.cursize, clientaddr);
        SZ_Clear(net_message);

        return sock;
    }

    // net_dgrm.c: Datagram_CheckNewConnections
    public QSocket? Datagram_CheckNewConnections()
    {
        QSocket? ret = null;

        for (net_landriverlevel = 0; net_landriverlevel < net_numlandrivers; net_landriverlevel++)
        {
            if (net_landrivers[net_landriverlevel].initialized)
            {
                if ((ret = _Datagram_CheckNewConnections()) != null)
                {
                    break;
                }
            }
        }

        return ret;
    }

    // net_dgrm.c: _Datagram_SearchForHosts
    private void _Datagram_SearchForHosts(bool xmit)
    {
        int ret;
        int n;
        int i;
        QSockAddr readaddr = default;
        QSockAddr myaddr = default;
        int control;

        dfunc.GetSocketAddr(dfunc.controlSock, ref myaddr);
        if (xmit)
        {
            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREQ_SERVER_INFO);
            MSG_WriteString(net_message, "QUAKE");
            MSG_WriteByte(net_message, NET_PROTOCOL_VERSION);
            SetControlHeader();
            dfunc.Broadcast(dfunc.controlSock, net_message.data, net_message.cursize);
            SZ_Clear(net_message);
        }

        while ((ret = dfunc.Read(dfunc.controlSock, net_message.data, net_message.maxsize, ref readaddr)) > 0)
        {
            if ((uint)ret < sizeof(int))
            {
                continue;
            }

            net_message.cursize = ret;

            // don't answer our own query
            if (dfunc.AddrCompare(readaddr, myaddr) >= 0)
            {
                continue;
            }

            // is the cache full?
            if (hostCacheCount == HOSTCACHESIZE)
            {
                continue;
            }

            MSG_BeginReading();
            control = ControlHeader();
            MSG_ReadLong();
            if (control == -1)
            {
                continue;
            }

            if ((control & ~NETFLAG_LENGTH_MASK) != NETFLAG_CTL)
            {
                continue;
            }

            if ((control & NETFLAG_LENGTH_MASK) != ret)
            {
                continue;
            }

            if (MSG_ReadByte() != CCREP_SERVER_INFO)
            {
                continue;
            }

            dfunc.GetAddrFromName(MSG_ReadString(), ref readaddr);

            // search the cache for this server
            for (n = 0; n < hostCacheCount; n++)
            {
                if (dfunc.AddrCompare(readaddr, hostcache[n].addr) == 0)
                {
                    break;
                }
            }

            // is it already there?
            if (n < hostCacheCount)
            {
                continue;
            }

            // add it
            hostCacheCount++;
            hostcache[n].name = MSG_ReadString();
            hostcache[n].map = MSG_ReadString();
            hostcache[n].users = MSG_ReadByte();
            hostcache[n].maxusers = MSG_ReadByte();
            if (MSG_ReadByte() != NET_PROTOCOL_VERSION)
            {
                hostcache[n].cname = hostcache[n].name.Length > 14 ? hostcache[n].name[..14] : hostcache[n].name;
                hostcache[n].name = "*" + hostcache[n].cname;
            }

            hostcache[n].addr = readaddr;
            hostcache[n].driver = net_driverlevel;
            hostcache[n].ldriver = net_landriverlevel;
            hostcache[n].cname = dfunc.AddrToString(readaddr);

            // check for a name conflict
            for (i = 0; i < hostCacheCount; i++)
            {
                if (i == n)
                {
                    continue;
                }

                if (Q_strcasecmp(hostcache[n].name, hostcache[i].name) == 0)
                {
                    string name = hostcache[n].name;
                    i = Q_strlen(name);
                    if (i < 15 && Ch(name, i - 1) > '8')
                    {
                        hostcache[n].name = name + "0";
                    }
                    else
                    {
                        hostcache[n].name = name[..(i - 1)] + (char)(byte)(name[i - 1] + 1) + name[i..];
                    }

                    i = -1;
                }
            }
        }
    }

    // net_dgrm.c: Datagram_SearchForHosts
    public void Datagram_SearchForHosts(bool xmit)
    {
        for (net_landriverlevel = 0; net_landriverlevel < net_numlandrivers; net_landriverlevel++)
        {
            if (hostCacheCount == HOSTCACHESIZE)
            {
                break;
            }

            if (net_landrivers[net_landriverlevel].initialized)
            {
                _Datagram_SearchForHosts(xmit);
            }
        }
    }

    // net_dgrm.c: _Datagram_Connect
    private QSocket? _Datagram_Connect(string? host)
    {
        QSockAddr sendaddr = default;
        QSockAddr readaddr = default;
        QSocket? sock;
        int newsock;
        int ret = 0;
        int reps;
        double start_time;
        int control;
        string reason;

        // see if we can resolve the host name
        if (host == null || dfunc.GetAddrFromName(host, ref sendaddr) == -1)
        {
            return null;
        }

        newsock = dfunc.OpenSocket(0);
        if (newsock == -1)
        {
            return null;
        }

        sock = NET_NewQSocket();
        if (sock == null)
        {
            goto ErrorReturn2;
        }

        sock.socket = newsock;
        sock.landriver = net_landriverlevel;

        // connect to the host
        if (dfunc.Connect(newsock, sendaddr) == -1)
        {
            goto ErrorReturn;
        }

        // send the connection request
        Con_Printf("trying...\n");
        SCR_UpdateScreen();
        start_time = net_time;

        for (reps = 0; reps < 3; reps++)
        {
            SZ_Clear(net_message);

            // save space for the header, filled in later
            MSG_WriteLong(net_message, 0);
            MSG_WriteByte(net_message, CCREQ_CONNECT);
            MSG_WriteString(net_message, "QUAKE");
            MSG_WriteByte(net_message, NET_PROTOCOL_VERSION);
            SetControlHeader();
            dfunc.Write(newsock, net_message.data, net_message.cursize, sendaddr);
            SZ_Clear(net_message);
            do
            {
                ret = dfunc.Read(newsock, net_message.data, net_message.maxsize, ref readaddr);

                // if we got something, validate it
                if (ret > 0)
                {
                    // is it from the right place?
                    if (sfunc(sock).AddrCompare(readaddr, sendaddr) != 0)
                    {
                        ret = 0;
                        continue;
                    }

                    if ((uint)ret < sizeof(int))
                    {
                        ret = 0;
                        continue;
                    }

                    net_message.cursize = ret;
                    MSG_BeginReading();

                    control = ControlHeader();
                    MSG_ReadLong();
                    if (control == -1)
                    {
                        ret = 0;
                        continue;
                    }

                    if ((control & ~NETFLAG_LENGTH_MASK) != NETFLAG_CTL)
                    {
                        ret = 0;
                        continue;
                    }

                    if ((control & NETFLAG_LENGTH_MASK) != ret)
                    {
                        ret = 0;
                        continue;
                    }
                }
            }
            while (ret == 0 && SetNetTime() - start_time < 2.5);

            if (ret != 0)
            {
                break;
            }

            Con_Printf("still trying...\n");
            SCR_UpdateScreen();
            start_time = SetNetTime();
        }

        if (ret == 0)
        {
            reason = "No Response";
            Con_Printf("%s\n", reason);
            m_return_reason = reason;
            goto ErrorReturn;
        }

        if (ret == -1)
        {
            reason = "Network Error";
            Con_Printf("%s\n", reason);
            m_return_reason = reason;
            goto ErrorReturn;
        }

        ret = MSG_ReadByte();
        if (ret == CCREP_REJECT)
        {
            reason = MSG_ReadString();
            Con_Printf(reason);
            m_return_reason = reason.Length > 31 ? reason[..31] : reason;
            goto ErrorReturn;
        }

        if (ret == CCREP_ACCEPT)
        {
            sock.addr = sendaddr;
            dfunc.SetSocketPort(ref sock.addr, MSG_ReadLong());
        }
        else
        {
            reason = "Bad Response";
            Con_Printf("%s\n", reason);
            m_return_reason = reason;
            goto ErrorReturn;
        }

        dfunc.GetNameFromAddr(sendaddr, out sock.address);

        Con_Printf("Connection accepted\n");
        sock.lastMessageTime = SetNetTime();

        // switch the connection to the specified address
        if (dfunc.Connect(newsock, sock.addr) == -1)
        {
            reason = "Connect to Game failed";
            Con_Printf("%s\n", reason);
            m_return_reason = reason;
            goto ErrorReturn;
        }

        m_return_onerror = false;
        return sock;

    ErrorReturn:
        NET_FreeQSocket(sock);
    ErrorReturn2:
        dfunc.CloseSocket(newsock);
        if (m_return_onerror)
        {
            key_dest = keydest_t.key_menu;
            m_state = m_return_state;
            m_return_onerror = false;
        }

        return null;
    }

    // net_dgrm.c: Datagram_Connect
    public QSocket? Datagram_Connect(string? host)
    {
        QSocket? ret = null;

        for (net_landriverlevel = 0; net_landriverlevel < net_numlandrivers; net_landriverlevel++)
        {
            if (net_landrivers[net_landriverlevel].initialized)
            {
                if ((ret = _Datagram_Connect(host)) != null)
                {
                    break;
                }
            }
        }

        return ret;
    }
}
