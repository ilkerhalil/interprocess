﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Cloudtoid.Interprocess.Semaphore.Posix;

namespace Cloudtoid.Interprocess.Semaphore.MacOS
{
    [SuppressMessage("StyleCop.CSharp.NamingRules", "SA1310:Field names should not contain underscore", Justification = "Matching the exact names in Linux/MacOS")]
    [SuppressMessage("StyleCop.CSharp.NamingRules", "SA1300:Element should begin with upper-case letter", Justification = "Matching the exact names in Linux/MacOS")]
    [SuppressMessage("StyleCop.CSharp.LayoutRules", "SA1513:Closing brace should be followed by blank line", Justification = "There is a bug in the rule!")]
    internal static class Interop
    {
        private const string Lib = "libSystem.dylib";
        private const int SEM_VALUE_MAX = 32767;
        private const int O_CREAT = 0x0200;  // create the semaphore if it does not exist

        private const int ENOENT = 2;        // The named semaphore does not exist.
        private const int EINTR = 4;         // Semaphore operation was interrupted by a signal.
        private const int EDEADLK = 11;      // A deadlock was detected.
        private const int ENOMEM = 12;       // Out of memory
        private const int EACCES = 13;       // Semaphore exists, but the caller does not have permission to open it.
        private const int EEXIST = 17;       // O_CREAT and O_EXCL were specified and the semaphore exists.
        private const int EINVAL = 22;       // Invalid semaphore or operation on a semaphore
        private const int ENFILE = 23;       // Too many semaphores or file descriptors are open on the system.
        private const int EMFILE = 24;       // The process has already reached its limit for semaphores or file descriptors in use.
        private const int EAGAIN = 35;       // The semaphore is already locked.
        private const int ENAMETOOLONG = 63; // The specified semaphore name is too long
        private const int EOVERFLOW = 84;    // The maximum allowable value for a semaphore would be exceeded.

        private static readonly IntPtr SemFailed = new(-1);

        private static unsafe int errno => Marshal.GetLastWin32Error();

#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        [DllImport(Lib, SetLastError = true)]
#if NETSTANDARD2_0
        private static extern IntPtr sem_open([MarshalAs(UnmanagedType.LPStr)] string name, int oflag, uint mode, uint value);
#else
        private static extern IntPtr sem_open([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int oflag, uint mode, uint value);
#endif

#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        [DllImport(Lib, SetLastError = true)]
        private static extern int sem_post(IntPtr handle);

        [DllImport(Lib, SetLastError = true)]
        private static extern int sem_wait(IntPtr handle);

        [DllImport(Lib, SetLastError = true)]
        private static extern int sem_trywait(IntPtr handle);

#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        [DllImport(Lib, SetLastError = true)]
#if NETSTANDARD2_0
        private static extern int sem_unlink([MarshalAs(UnmanagedType.LPStr)] string name);
#else
        private static extern int sem_unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
#endif

#if NET5_0_OR_GREATER
        [SuppressGCTransition]
#endif
        [DllImport(Lib, SetLastError = true)]
        private static extern int sem_close(IntPtr handle);

        internal static IntPtr CreateOrOpenSemaphore(string name, uint initialCount)
        {
            var handle = sem_open(name, O_CREAT, (uint)PosixFilePermissions.ACCESSPERMS, initialCount);
            if (handle != SemFailed)
                return handle;

            throw errno switch
            {
                EINVAL => new ArgumentException($"The initial count cannot be greater than {SEM_VALUE_MAX}.", nameof(initialCount)),
                ENAMETOOLONG => new ArgumentException($"The specified semaphore name is too long.", nameof(name)),
                EACCES => new PosixSempahoreUnauthorizedAccessException(),
                EEXIST => new PosixSempahoreExistsException(),
                EINTR => new OperationCanceledException(),
                ENFILE => new PosixSempahoreException("Too many semaphores or file descriptors are open on the system."),
                EMFILE => new PosixSempahoreException("Too many semaphores or file descriptors are open by this process."),
                ENOMEM => new InsufficientMemoryException(),
                _ => new PosixSempahoreException(errno),
            };
        }

        internal static void Release(IntPtr handle)
        {
            if (sem_post(handle) == 0)
                return;

            throw errno switch
            {
                EINVAL => new InvalidPosixSempahoreException(),
                EOVERFLOW => new SemaphoreFullException(),
                _ => new PosixSempahoreException(errno),
            };
        }

        internal static bool Wait(IntPtr handle, int millisecondsTimeout)
        {
            if (millisecondsTimeout == Timeout.Infinite)
            {
                Wait(handle);
            }
            else
            {
                var start = DateTime.Now;
                while (!TryWait(handle))
                {
                    if ((DateTime.Now - start).Milliseconds > millisecondsTimeout)
                        return false;

                    Thread.Yield();
                }
            }

            return true;
        }

        private static void Wait(IntPtr handle)
        {
            if (sem_wait(handle) == 0)
                return;

            throw errno switch
            {
                EINVAL => new InvalidPosixSempahoreException(),
                EDEADLK => new PosixSempahoreException($"A deadlock was detected attempting to wait on a semaphore."),
                EINTR => new OperationCanceledException(),
                _ => new PosixSempahoreException(errno),
            };
        }

        private static bool TryWait(IntPtr handle)
        {
            if (sem_trywait(handle) == 0)
                return true;

            return errno switch
            {
                EAGAIN => false,
                EINVAL => throw new InvalidPosixSempahoreException(),
                EDEADLK => throw new PosixSempahoreException($"A deadlock was detected attempting to wait on a semaphore."),
                EINTR => throw new OperationCanceledException(),
                _ => throw new PosixSempahoreException(errno),
            };
        }

        internal static void Close(IntPtr handle)
        {
            if (sem_close(handle) == 0)
                return;

            throw errno switch
            {
                EINVAL => new InvalidPosixSempahoreException(),
                _ => new PosixSempahoreException(errno),
            };
        }

        internal static void Unlink(string name)
        {
            if (sem_unlink(name) == 0)
                return;

            throw errno switch
            {
                ENAMETOOLONG => new ArgumentException($"The specified semaphore name is too long.", nameof(name)),
                EACCES => new PosixSempahoreUnauthorizedAccessException(),
                ENOENT => new PosixSempahoreNotExistsException(),
                _ => new PosixSempahoreException(errno),
            };
        }
    }
}