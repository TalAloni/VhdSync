using DiskAccessLibrary;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VhdSync
{
    class Program
    {
        private const FileOptions FILE_FLAG_NO_BUFFERING = (FileOptions)0x20000000;
        private const FileOptions FILE_FLAG_OVERLAPPED = (FileOptions)0x40000000;

        private static readonly int s_syncBlockSize = 1024 * 1024;
        private static readonly int s_maxDegressOfParallelism = 128;

        private static long s_numberOfBlocksSynced = 0;
        private static long s_numberOfBlocksToCopy = 0;
        private static long s_numberOfSyncBlocks;
        private static long s_streamLength;

        private static Stopwatch s_stopwatch;

        static void Main(string[] args)
        {
            if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "-force"))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("VhdSync <SourceFilePath> <TargetFilePath> [-force]");
                return;
            }

            ThreadPool.SetMinThreads(s_maxDegressOfParallelism, s_maxDegressOfParallelism);
            string source = args[0];
            string target = args[1];
            bool force = args.Length > 2;
            uint flags = unchecked((uint)(FILE_FLAG_NO_BUFFERING | FileOptions.WriteThrough | FILE_FLAG_OVERLAPPED));

            if (force)
            {
                // Calling AdjustTokenPrivileges and then immediately calling SetFileValidData will sometimes result in ERROR_PRIVILEGE_NOT_HELD.
                // We can work around the issue by obtaining the privilege before obtaining the handle.
                bool hasManageVolumePrivilege = SecurityUtils.ObtainManageVolumePrivilege();
                if (!hasManageVolumePrivilege)
                {
                    Console.WriteLine("Error: Failed to obtain SeManageVolumePrivilege");
                    return;
                }
            }
            SafeFileHandle sourceFileHandle = HandleUtils.GetFileHandle(source, FileAccess.Read, ShareMode.Read, flags);
            SafeFileHandle targetFileHandle = HandleUtils.GetFileHandle(target, FileAccess.ReadWrite, ShareMode.Read, flags);
            if (sourceFileHandle.IsInvalid)
            {
                Console.WriteLine("Error: Source file handle is invalid");
                return;
            }

            if (targetFileHandle.IsInvalid)
            {
                Console.WriteLine("Error: Target file handle is invalid");
                return;
            }

            FileStreamEx sourceStream = new FileStreamEx(sourceFileHandle, FileAccess.Read);
            FileStreamEx targetStream = new FileStreamEx(targetFileHandle, FileAccess.ReadWrite);
            List<long> blocksToSync = null;
            Thread workerThread = new Thread(() =>
            {
                blocksToSync = Syncrhonize(sourceStream, targetStream, force);
            });

            workerThread.Start();
            Console.WriteLine($"Comparing source and target...");
            while (workerThread.ThreadState != System.Threading.ThreadState.Stopped)
            {
                Thread.Sleep(1000);
                Console.WriteLine($"{s_numberOfBlocksSynced.ToString("###,###,##0")} MB read, {s_numberOfBlocksToCopy.ToString("###,###,##0")} MB to copy");
                if (s_numberOfBlocksSynced > 100)
                {
                    long numberOfBlocksSynced = s_numberOfBlocksSynced;
                    long elapsedMilliseconds = s_stopwatch.ElapsedMilliseconds;
                    long remainingMilliseconds = (long)((double)elapsedMilliseconds / numberOfBlocksSynced * (s_numberOfSyncBlocks - numberOfBlocksSynced));
                    TimeSpan eta = TimeSpan.FromMilliseconds(remainingMilliseconds);
                    Console.WriteLine($"ETA: {eta.Days} days {eta.Hours.ToString("00")}:{eta.Minutes.ToString("00")}:{eta.Seconds.ToString("00")}");
                    Console.SetCursorPosition(0, Console.CursorTop - 1);
                }
                Console.SetCursorPosition(0, Console.CursorTop - 1);
            }

            if (blocksToSync != null)
            {
                Console.WriteLine($"Beginning block-Level file sync, {blocksToSync.Count.ToString("###,###,##0")} MB to copy");
                workerThread = new Thread(() =>
                {
                    CopyDifferentBlocks(sourceStream, targetStream, blocksToSync);
                });
                workerThread.Start();
                while (workerThread.ThreadState != System.Threading.ThreadState.Stopped)
                {
                    Thread.Sleep(1000);
                    Console.WriteLine($"{s_numberOfBlocksToCopy.ToString("###,###,##0")} MB remaining to copy");
                    Console.SetCursorPosition(0, Console.CursorTop - 1);
                }

                Console.WriteLine("block-Level file sync completed");
            }
        }

        private static List<long> Syncrhonize(FileStreamEx sourceStream, FileStreamEx targetStream, bool force)
        {
            s_streamLength = sourceStream.Length;
            if (targetStream.Length != s_streamLength)
            {
                if (!force)
                {
                    Console.WriteLine("Error: Source file length does not match target file length");
                    return null;
                }
                else
                {
                    Console.WriteLine($"Setting target file size to {s_streamLength} bytes");
                    try
                    {
                        targetStream.SetLength(s_streamLength);
                        targetStream.SetValidLength(s_streamLength);
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Error: failed to set target file length");
                        return null;
                    }
                }
            }

            List<long> blocksToSync = new List<long>();

            s_numberOfSyncBlocks = (long)Math.Ceiling((double)s_streamLength / s_syncBlockSize);
            s_stopwatch = Stopwatch.StartNew();
            
            Parallel.For(0, s_numberOfSyncBlocks, new ParallelOptions { MaxDegreeOfParallelism = s_maxDegressOfParallelism }, (syncBlockIndex) =>
            {
                int syncBlockSize;
                if (syncBlockIndex == s_numberOfSyncBlocks - 1) // Last sync block
                {
                    syncBlockSize = (int)(s_streamLength % s_syncBlockSize);
                }
                else
                {
                    syncBlockSize = s_syncBlockSize;
                }
                byte[] sourceBuffer = new byte[syncBlockSize];
                byte[] targetBuffer = new byte[syncBlockSize];
                long position = syncBlockIndex * s_syncBlockSize;
                sourceStream.ReadOverlapped(sourceBuffer, 0, sourceBuffer.Length, position);
                targetStream.ReadOverlapped(targetBuffer, 0, targetBuffer.Length, position);

                if (!ByteUtils.AreByteArraysEqual(sourceBuffer, targetBuffer))
                {
                    Interlocked.Increment(ref s_numberOfBlocksToCopy);
                    lock (blocksToSync)
                    {
                        blocksToSync.Add(syncBlockIndex);
                    }
                }

                Interlocked.Increment(ref s_numberOfBlocksSynced);
            });

            return blocksToSync;
        }

        private static void CopyDifferentBlocks(FileStreamEx sourceStream, FileStreamEx targetStream, List<long> blocksToSync)
        {
            Parallel.For(0, blocksToSync.Count, new ParallelOptions { MaxDegreeOfParallelism = s_maxDegressOfParallelism }, (index) =>
            {
                long syncBlockIndex = blocksToSync[index];
                int syncBlockSize;
                if (syncBlockIndex == s_numberOfSyncBlocks - 1) // Last sync block
                {
                    syncBlockSize = (int)(s_streamLength % s_syncBlockSize);
                }
                else
                {
                    syncBlockSize = s_syncBlockSize;
                }

                long offset = syncBlockIndex * s_syncBlockSize;
                byte[] sourceBuffer = new byte[syncBlockSize];
                sourceStream.ReadOverlapped(sourceBuffer, 0, sourceBuffer.Length, offset);
                targetStream.WriteOverlapped(sourceBuffer, 0, sourceBuffer.Length, offset);
                Interlocked.Decrement(ref s_numberOfBlocksToCopy);
            });
        }
    }
}
