using System.Buffers.Binary;
using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// cl_demo.c: DEMO CODE
//
// When a demo is playing back, all NET_SendMessages are skipped, and NET_GetMessages are read from the demo file.
//
// Whenever cl.time gets past the last received message, another message is read from the demo file.
public sealed partial class QuakeEngine
{
    // cl_demo.c: CL_StopPlayback
    // Called when a demo file runs out, or the user starts a game
    public void CL_StopPlayback()
    {
        if (!cls.demoplayback)
        {
            return;
        }

        cls.demofile!.Dispose();
        cls.demoplayback = false;
        cls.demofile = null;
        cls.state = cactive_t.ca_disconnected;

        if (cls.timedemo)
        {
            CL_FinishTimeDemo();
        }
    }

    // cl_demo.c: CL_WriteDemoMessage
    // Dumps the current net message, prefixed by the length and view angles
    public void CL_WriteDemoMessage()
    {
        int i;
        Span<byte> b = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(b, net_message.cursize);
        cls.demofile!.Write(b);
        for (i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b, cl.viewangles[i]);
            cls.demofile.Write(b);
        }

        cls.demofile.Write(net_message.data, 0, net_message.cursize);
        cls.demofile.Flush();
    }

    // cl_demo.c: CL_GetMessage
    // Handles recording and playback of demos, on top of NET_ code
    public int CL_GetMessage()
    {
        int r, i;
        Span<byte> b = stackalloc byte[4];

        if (cls.demoplayback)
        {
            // decide if it is time to grab the next message
            if (cls.signon == SIGNONS)
            {
                // allways grab until fully connected
                if (cls.timedemo)
                {
                    if (host_framecount == cls.td_lastframe)
                    {
                        return 0; // allready read this frame's message
                    }

                    cls.td_lastframe = host_framecount;

                    // if this is the second frame, grab the real td_starttime so the bogus time on the first frame
                    // doesn't count
                    if (host_framecount == cls.td_startframe + 1)
                    {
                        cls.td_starttime = (float)realtime;
                    }
                }
                else if (/* cl.time > 0 && */ cl.time <= cl.mtime[0])
                {
                    return 0; // don't need another message yet
                }
            }

            // get the next message (fread: what it does not read stays as it was)
            if (cls.demofile!.ReadAtLeast(b, 4, throwOnEndOfStream: false) == 4)
            {
                net_message.cursize = BinaryPrimitives.ReadInt32LittleEndian(b);
            }

            cl.mviewangles[1] = cl.mviewangles[0];
            for (i = 0; i < 3; i++)
            {
                if (cls.demofile.ReadAtLeast(b, 4, throwOnEndOfStream: false) == 4)
                {
                    cl.mviewangles[0][i] = BinaryPrimitives.ReadSingleLittleEndian(b);
                }
            }

            if (net_message.cursize > MAX_MSGLEN)
            {
                throw Sys_Error("Demo message > MAX_MSGLEN");
            }

            r = net_message.cursize >= 0
                && cls.demofile.ReadAtLeast(net_message.data.AsSpan(0, net_message.cursize), net_message.cursize, throwOnEndOfStream: false) == net_message.cursize
                ? 1 : 0;
            if (r != 1)
            {
                CL_StopPlayback();
                return 0;
            }

            return 1;
        }

        while (true)
        {
            r = NET_GetMessage(cls.netcon);

            if (r != 1 && r != 2)
            {
                return r;
            }

            // discard nop keepalive message
            if (net_message.cursize == 1 && net_message.data[0] == svc_nop)
            {
                Con_Printf("<-- server to client keepalive\n");
            }
            else
            {
                break;
            }
        }

        if (cls.demorecording)
        {
            CL_WriteDemoMessage();
        }

        return r;
    }

    // cl_demo.c: CL_Stop_f
    // stop recording a demo
    public void CL_Stop_f()
    {
        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        if (!cls.demorecording)
        {
            Con_Printf("Not recording a demo.\n");
            return;
        }

        // write a disconnect message to the demo file
        SZ_Clear(net_message);
        MSG_WriteByte(net_message, svc_disconnect);
        CL_WriteDemoMessage();

        // finish up
        cls.demofile!.Dispose();
        cls.demofile = null;
        cls.demorecording = false;
        Con_Printf("Completed demo\n");
    }

    // cl_demo.c: CL_Record_f
    // record <demoname> <map> [cd track]
    public void CL_Record_f()
    {
        int c;
        string name;
        int track;

        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        c = Cmd_Argc();
        if (c != 2 && c != 3 && c != 4)
        {
            Con_Printf("record <demoname> [<map> [cd track]]\n");
            return;
        }

        if (Cmd_Argv(1).Contains("..", StringComparison.Ordinal))
        {
            Con_Printf("Relative pathnames are not allowed.\n");
            return;
        }

        if (c == 2 && cls.state == cactive_t.ca_connected)
        {
            Con_Printf("Can not record - already connected to server\nClient demo recording must be started before connecting\n");
            return;
        }

        // write the forced cd track number, or -1
        if (c == 4)
        {
            track = QString.atoi(Cmd_Argv(3));
            Con_Printf("Forcing CD track to %i\n", cls.forcetrack);
        }
        else
        {
            track = -1;
        }

        // the name C prints (the file goes where the game writes, design.md §9)
        string file = Com.DefaultExtension(Cmd_Argv(1), ".dem");
        name = Com.DefaultExtension(CFormat.Sprintf("%s/%s", com_gamedir, Cmd_Argv(1)), ".dem");

        // start the map up
        if (c > 2)
        {
            Cmd_ExecuteString(va("map %s", Cmd_Argv(2)), cmd_source_t.src_command);
        }

        // open the demo file
        Con_Printf("recording to %s.\n", name);
        try
        {
            cls.demofile = new FileStream(fileSystem.WritePath(file), FileMode.Create, FileAccess.Write);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Con_Printf("ERROR: couldn't open.\n");
            return;
        }

        cls.forcetrack = track;
        cls.demofile.Write(Encoding.Latin1.GetBytes(CFormat.Sprintf("%i\n", cls.forcetrack)));

        cls.demorecording = true;
    }

    // cl_demo.c: CL_PlayDemo_f
    // play [demoname]
    public void CL_PlayDemo_f()
    {
        string name;
        int c;
        bool neg = false;

        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        if (Cmd_Argc() != 2)
        {
            Con_Printf("play <demoname> : plays a demo\n");
            return;
        }

        // disconnect from server
        CL_Disconnect();

        // open the demo file
        name = Com.DefaultExtension(Cmd_Argv(1), ".dem");

        Con_Printf("Playing demo from %s.\n", name);
        COM_FOpenFile(name, out cls.demofile);
        if (cls.demofile == null)
        {
            Con_Printf("ERROR: couldn't open.\n");
            cls.demonum = -1; // stop demo loop
            return;
        }

        cls.demoplayback = true;
        cls.state = cactive_t.ca_connected;
        cls.forcetrack = 0;

        // (at the end of the file C's getc gives EOF for ever: the loop would not end)
        while ((c = cls.demofile.ReadByte()) != '\n' && c != -1)
        {
            if (c == '-')
            {
                neg = true;
            }
            else
            {
                cls.forcetrack = (cls.forcetrack * 10) + (c - '0');
            }
        }

        if (neg)
        {
            cls.forcetrack = -cls.forcetrack;
        }

        // ZOID, fscanf is evil
        // fscanf (cls.demofile, "%i\n", &cls.forcetrack);
    }

    // cl_demo.c: CL_FinishTimeDemo
    public void CL_FinishTimeDemo()
    {
        int frames;
        float time;

        cls.timedemo = false;

        // the first frame didn't count
        frames = host_framecount - cls.td_startframe - 1;
        time = (float)(realtime - cls.td_starttime);
        if (time == 0)
        {
            time = 1;
        }

        Con_Printf("%i frames %5.1f seconds %5.1f fps\n", frames, time, frames / time);
    }

    // cl_demo.c: CL_TimeDemo_f
    // timedemo [demoname]
    public void CL_TimeDemo_f()
    {
        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        if (Cmd_Argc() != 2)
        {
            Con_Printf("timedemo <demoname> : gets demo speeds\n");
            return;
        }

        CL_PlayDemo_f();

        // cls.td_starttime will be grabbed at the second frame of the demo, so all the loading time doesn't get
        // counted
        cls.timedemo = true;
        cls.td_startframe = host_framecount;
        cls.td_lastframe = -1; // get a new message this frame
    }
}
