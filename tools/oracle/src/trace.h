/* trace.c: the oracle's traces (tools/oracle/README.md). */
#ifndef TRACE_H
#define TRACE_H

void Trace_Init (void);
void Trace_Frame (void);
void Trace_Events (void);
void Trace_Palette (unsigned char *palette);
void Trace_Error (char *text);
void Trace_Close (void);
void Trace_Message (struct qsocket_s *sock, sizebuf_t *data, int reliable, int result);

#endif
