/* winsock_oracle.c: the oracle's Winsock and its virtual LAN (design.md §11.5). */
#ifndef WINSOCK_ORACLE_H
#define WINSOCK_ORACLE_H

// sys_oracle.c: the virtual clock's steps so far (frame 1 runs at step 1; a frame that waits takes more)
unsigned int Oracle_Steps (void);

// the machine (-oraclehost) and the other hosts' datagrams (-tracedatagrams), before Host_Init
void Oracle_WinsockInit (void);

// the trace's U line: the hash of what was sent and received since the last call, and how many
unsigned long long Oracle_Datagrams (int *count);

#endif
