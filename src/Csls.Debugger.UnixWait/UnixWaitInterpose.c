#include <sys/types.h>
#include <sys/wait.h>

/* Only the Mach-O binding record is native; all wait ownership remains in C#. */
extern pid_t csls_waitpid_interposed(pid_t process_id, int *status, int options);
extern pid_t csls_waitpid_interposed_nocancel(pid_t process_id, int *status, int options);
extern pid_t csls_waitpid_nocancel(pid_t process_id, int *status, int options) __asm__("_waitpid$NOCANCEL");

/* dyld redirects waitpid consumers; the C# owner uses the distinct wait4 API. */
__attribute__((used, section("__DATA,__interpose")))
static const struct
{
    pid_t (*replacement)(pid_t, int *, int);
    pid_t (*original)(pid_t, int *, int);
} s_waitpid_interpositions[] =
{
    { csls_waitpid_interposed, waitpid },
    { csls_waitpid_interposed_nocancel, csls_waitpid_nocancel }
};
