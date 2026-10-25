/* pbcore for iOS: exported from core-go/apple (go build -buildmode c-archive). */
#ifndef PBCORE_H
#define PBCORE_H

#include <stdint.h>

/* level: 0 debug, 1 info, 2 warning, 3 error, 9 status line (JSON: {"status":"up"|"down"|"error"|"stats",...}) */
typedef void (*pbLoggerFn)(int level, const char *msg);

void pbSetLogger(uintptr_t fn);
char *pbVersion(void);
int pbStart(const char *requestJSON, int tunFd, const char *bindInterface);
void pbStop(void);
int pbRunning(void);
int pbReload(const char *rulesJSON);
char *pbCheckProxy(const char *proxyURL, const char *lang);

#endif
