using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AnimeJaNai.Launcher;

/// The few libc calls the Linux launcher needs: execve, flock, isatty, kill(pid, 0).
[SupportedOSPlatform("linux")]
static unsafe partial class Posix
{
    const int O_RDWR = 0x2, O_CREAT = 0x40, O_CLOEXEC = 0x80000;
    const int LOCK_EX = 2, LOCK_NB = 4;
    const int EINTR = 4, EPERM = 1;
    const int SIGPIPE = 13, SIG_SETMASK = 2;

    /// Replaces this process with `exe`. Returns only on failure (throws).
    public static void Exec(string exe, IReadOnlyList<string> args, IDictionary<string, string> env)
    {
        var allocated = new List<nint>();
        nint Utf8(string s)
        {
            nint p = Marshal.StringToCoTaskMemUTF8(s);
            allocated.Add(p);
            return p;
        }
        try
        {
            var argv = new nint[args.Count + 2];
            argv[0] = Utf8(exe);
            for (int i = 0; i < args.Count; i++)
            {
                argv[i + 1] = Utf8(args[i]);
            }
            var envp = new nint[env.Count + 1];
            int n = 0;
            foreach (var (k, v) in env)
            {
                envp[n++] = Utf8($"{k}={v}");
            }

            Console.Out.Flush();
            Console.Error.Flush();
            // The .NET runtime ignores SIGPIPE, and an ignored signal stays ignored across exec.
            // Give mpv the default disposition and an empty signal mask, as a shell would.
            signal(SIGPIPE, 0 /* SIG_DFL */);
            byte* empty = stackalloc byte[128]; // sizeof(sigset_t) in glibc
            new Span<byte>(empty, 128).Clear();
            pthread_sigmask(SIG_SETMASK, empty, null);

            fixed (nint* a = argv)
            fixed (nint* e = envp)
            {
                execve(argv[0], a, e);
            }
            int err = Marshal.GetLastPInvokeError();
            throw new IOException($"could not start {exe}: {Marshal.GetPInvokeErrorMessage(err)}");
        }
        finally
        {
            foreach (var p in allocated)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    /// An exclusive flock on `path`, or null when it isn't free within the timeout. The descriptor
    /// is close-on-exec, so an execve releases the lock (no process is left holding it).
    public static LockFile? Lock(string path, int timeoutMs)
    {
        int fd = open(path, O_RDWR | O_CREAT | O_CLOEXEC, 0x180 /* 0600 */);
        if (fd < 0)
        {
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (flock(fd, LOCK_EX | LOCK_NB) == 0)
            {
                return new LockFile(fd);
            }
            if (Marshal.GetLastPInvokeError() != EINTR && sw.ElapsedMilliseconds >= timeoutMs)
            {
                close(fd);
                return null;
            }
            Thread.Sleep(10);
        }
    }

    public sealed class LockFile(int fd) : IDisposable
    {
        int fd = fd;

        public void Dispose()
        {
            if (fd >= 0)
            {
                close(fd); // closing the only descriptor drops the flock
                fd = -1;
            }
        }
    }

    public static bool IsTerminal(int fd) => isatty(fd) == 1;

    /// Whether a process with this pid exists (it may belong to someone else).
    public static bool Alive(int pid) => pid > 0 && (kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == EPERM);

    [LibraryImport("libc", SetLastError = true)] private static partial int execve(nint path, nint* argv, nint* envp);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int open(string path, int flags, int mode);
    [LibraryImport("libc", SetLastError = true)] private static partial int flock(int fd, int operation);
    [LibraryImport("libc", SetLastError = true)] private static partial int close(int fd);
    [LibraryImport("libc")] private static partial int isatty(int fd);
    [LibraryImport("libc", SetLastError = true)] private static partial int kill(int pid, int sig);
    [LibraryImport("libc")] private static partial nint signal(int sig, nint handler);
    [LibraryImport("libc")] private static partial int pthread_sigmask(int how, byte* set, byte* old);
}
