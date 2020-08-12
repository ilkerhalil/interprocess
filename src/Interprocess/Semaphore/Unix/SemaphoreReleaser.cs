﻿using System;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cloudtoid.Interprocess.DomainSocket;
using Microsoft.Extensions.Logging;

namespace Cloudtoid.Interprocess.Semaphore.Unix
{
    internal sealed class SemaphoreReleaser : IInterprocessSemaphoreReleaser
    {
        private static readonly byte[] message = new byte[] { 1 };
        private readonly CancellationTokenSource cancellationSource = new CancellationTokenSource();
        private readonly AutoResetEvent releaseSignal = new AutoResetEvent(false);
        private readonly string filePath;
        private readonly ILogger logger;
        private Socket?[] clients = Array.Empty<Socket>();

        internal SemaphoreReleaser(SharedAssetsIdentifier identifier, ILogger logger)
        {
            this.logger = logger;

            filePath = Util.CreateShortUniqueFileName(
                identifier.Path,
                identifier.Name,
                Constants.Extension);

            if (filePath.Length > 104)
                throw new ArgumentException($"The queue path and queue name together are too long for this OS. File: '{filePath}'");

            StartServer();
        }

        // used for testing
        internal int ClientCount
            => clients.Count(c => c != null);

        public void Dispose()
            => cancellationSource.Cancel();

        public void Release()
        {
            if (clients.Length > 0)
                releaseSignal.Set();
        }

        private void StartServer()
        {
            // using dedicated threads as these are long running and looping operations
            var thread = new Thread(ConnectionAcceptLoop);
            thread.IsBackground = true;
            thread.Start();

            thread = new Thread(async () => await ReleaseLoop());
            thread.IsBackground = true;
            thread.Start();
        }

        private void ConnectionAcceptLoop()
        {
            var cancellation = cancellationSource.Token;
            UnixDomainSocketServer? server = null;

            try
            {
                server = new UnixDomainSocketServer(filePath, logger);
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var client = server.Accept(cancellation);
                        clients = clients.Concat(new[] { client }).Where(c => c != null).ToArray();
                    }
                    catch (SocketException se)
                    {
                        logger.LogError(se, "Accepting a Unix Domain Socket connection failed unexpectedly.");
                        server.Dispose();
                        server = new UnixDomainSocketServer(filePath, logger);
                    }
                }
            }
            catch when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // if there is an error here, we are in a bad state.
                // treat this as a fatal exception and crash the process
                logger.FailFast(
                    "Unix semaphore releaser failed leaving the application in a bad state. " +
                    "The only option is to crash the application.", ex);
            }
            finally
            {
                foreach (var client in clients)
                    client.SafeDispose();

                server?.Dispose();
            }
        }

        private async Task ReleaseLoop()
        {
            const int MaxClientCount = 1000;
            var cancellation = cancellationSource.Token;
            var tasks = new ValueTask[MaxClientCount];

            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (!releaseSignal.WaitOne(10))
                        continue;

                    // take a snapshot as the ref to the array may change
                    var clients = this.clients;

                    var count = Math.Min(clients.Length, MaxClientCount);
                    if (count == 0)
                        continue;

                    for (int i = 0; i < count; i++)
                        tasks[i] = ReleaseAsync(clients, i, cancellation);

                    // do not use Task.WaitAll
                    for (int i = 0; i < count; i++)
                        await tasks[i].ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // if there is an error here, we are in a bad state.
                // treat this as a fatal exception and crash the process
                logger.FailFast(
                    "Unix semaphore releaser failed leaving the application in a bad state. " +
                    "The only option is to crash the application.", ex);
            }
            finally
            {
                releaseSignal.Dispose();
            }
        }

        private async ValueTask ReleaseAsync(
            Socket?[] clients,
            int i,
            CancellationToken cancellation)
        {
            var client = clients[i];
            if (client == null)
                return;

            try
            {
                var bytesSent = await client
                    .SendAsync(message, SocketFlags.None, cancellation)
                    .ConfigureAwait(false);

                Debug.Assert(bytesSent == message.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sending a message to a Unix Domain Socket failed.");

                if (!client.Connected)
                {
                    clients[i] = null;
                    client.SafeDispose();
                }
            }
        }
    }
}
