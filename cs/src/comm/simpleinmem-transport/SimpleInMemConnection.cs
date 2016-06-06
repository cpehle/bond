// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.Comm.SimpleInMem
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Bond.Comm.Layers;
    using Bond.Comm.Service;
    using Bond.Comm.SimpleInMem.Processor;

    public enum ConnectionType
    {
        Client,
        Server
    }

    [Flags]
    public enum CnxState
    {
        Created = 0x01,
        Connected = 0x02,
        SendProtocolError = 0x04,
        Disconnecting = 0x08,
        Disconnected = 0x10,
    }

    public class SimpleInMemConnection : Connection, IRequestResponseConnection, IEventConnection
    {
        private readonly Guid connectionId;
        private readonly ConnectionType connectionType;
        private readonly SimpleInMemListener parentListener;
        private readonly ServiceHost serviceHost;
        private InMemFrameQueue pairedReadQueue = null;
        private readonly InMemFrameQueue writeQueue;
        private long prevConversationId;
        private CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
        private CnxState state;
        private object stateLock = new object();

        public ConnectionMetrics ConnectionMetrics { get; } = new ConnectionMetrics();

        internal SimpleInMemConnection(SimpleInMemListener parentListener,
                                       ConnectionType connectionType, ServiceHost serviceHost)
        {
            if (parentListener == null) throw new ArgumentNullException(nameof(parentListener));
            if (serviceHost == null) throw new ArgumentNullException(nameof(serviceHost));

            this.connectionType = connectionType;
            this.parentListener = parentListener;
            this.serviceHost = serviceHost;
            writeQueue = new InMemFrameQueue();
            connectionId = Guid.NewGuid();

            // start at -1 or 0 so the first conversation ID is 1 or 2.
            prevConversationId = (connectionType == ConnectionType.Client) ? -1 : 0;
            state = CnxState.Created;
            ConnectionMetrics.connection_id = connectionId.ToString();
        }

        public CnxState State
        {
            get
            {
                return state;
            }
        }

        public ConnectionType ConnectionType
        {
            get
            {
                return connectionType;
            }
        }

        public Guid Id
        {
            get
            {
                return connectionId;
            }
        }

        public bool IsConnected
        {
            get
            {
                return ((state & CnxState.Connected) != 0);
            }
        }

        public bool IsPaired
        {
            get
            {
                return pairedReadQueue != null;
            }
        }

        internal InMemFrameQueue WriteQueue
        {
            get
            {
                return writeQueue;
            }
        }

        internal InMemFrameQueue ReadQueue
        {
            get
            {
                return pairedReadQueue;
            }
        }

        public override string ToString()
        {
            return $"{nameof(SimpleInMemConnection)}({connectionId})";
        }

        public override Task StopAsync()
        {
            bool connected = false;

            lock (stateLock)
            {
                if (connected = ((state & CnxState.Connected) != 0))
                {
                    state = CnxState.Disconnecting;
                    cancelTokenSource.Cancel();
                }
            }

            if (connected)
            {
                OnDisconnect();
                writeQueue.Clear();
                pairedReadQueue = null;

                lock (stateLock)
                {
                    state = CnxState.Disconnected;
                }
            }

            return TaskExt.CompletedTask;
        }

        internal static ConnectionPair Pair(SimpleInMemConnection client, SimpleInMemConnection server)
        {
            ConnectionPair pair = new ConnectionPair(client, server);
            
            server.pairedReadQueue = client.writeQueue;
            client.pairedReadQueue = server.writeQueue;

            server.Start();
            client.Start();
            return pair;
        }

        public async Task<IMessage<TResponse>> RequestResponseAsync<TRequest, TResponse>(string methodName, IMessage<TRequest> message, CancellationToken ct)
        {
            EnsureCorrectState(CnxState.Connected);
            IMessage response = await SendRequestAsync(methodName, message);
            return response.Convert<TResponse>();
        }

        public Task FireEventAsync<TPayload>(string methodName, IMessage<TPayload> message)
        {
            EnsureCorrectState(CnxState.Connected);
            SendEventAsync(methodName, message);
            return TaskExt.CompletedTask;
        }

        private void Start()
        {
            if (!IsPaired)
            {
                var message = $"{ToString()} - Connection cannot be started without pairing first.";
                throw new InvalidOperationException(message);
            }

            lock (stateLock)
            {
                EnsureCorrectState(CnxState.Created);
                var batchProcessor = new BatchProcessor(this, serviceHost);
                Task.Run(() => batchProcessor.ProcessAsync(cancelTokenSource.Token));
                state = CnxState.Connected;
            }
        }

        private Task<IMessage> SendRequestAsync(string methodName, IMessage request)
        {
            var conversationId = AllocateNextConversationId();

            var sendContext = new SimpleInMemSendContext(this);
            IBonded layerData;
            Error layerError = LayerStackUtils.ProcessOnSend(parentListener.ServiceHost.ParentTransport.LayerStack,
                                                             MessageType.Request, sendContext, out layerData);

            if (layerError != null)
            {
                Log.Error("{0}.{1}: Sending request {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                            this, nameof(SendRequestAsync), conversationId, methodName, layerError.error_code, layerError.message);
                return Task.FromResult<IMessage>(Message.FromError(layerError));
            }

            var payload = Util.NewPayLoad(conversationId, PayloadType.Request, layerData, request, new TaskCompletionSource<IMessage>());
            payload.headers.method_name = methodName;
            writeQueue.Enqueue(payload);

            return payload.outstandingRequest.Task;
        }

        private void SendEventAsync(string methodName, IMessage message)
        {
            var conversationId = AllocateNextConversationId();

            var sendContext = new SimpleInMemSendContext(this);
            IBonded layerData;
            Error layerError = LayerStackUtils.ProcessOnSend(parentListener.ServiceHost.ParentTransport.LayerStack,
                                                             MessageType.Event, sendContext, out layerData);

            if (layerError != null)
            {
                Log.Error("{0}.{1}: Sending event {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                            this, nameof(SendEventAsync), conversationId, methodName, layerError.error_code, layerError.message);
                return;
            }

            var payload = Util.NewPayLoad(conversationId, PayloadType.Event, layerData, message, null);
            payload.headers.method_name = methodName;
            writeQueue.Enqueue(payload);
        }

        private ulong AllocateNextConversationId()
        {
            // Interlocked.Add() handles overflow by wrapping, not throwing.
            var newConversationId = Interlocked.Add(ref prevConversationId, 2);
            if (newConversationId < 0)
            {
                throw new SimpleInMemProtocolErrorException("Exhausted conversation IDs");
            }
            return unchecked((ulong)newConversationId);
        }

        private void OnDisconnect()
        {
            Log.Debug("{0}.{1}: Disconnecting.", this, nameof(OnDisconnect));
            var args = new DisconnectedEventArgs(this, null);
            if (connectionType == ConnectionType.Client)
            {
                parentListener.InvokeOnDisconnected(args);
            }
        }

        private void EnsureCorrectState(CnxState allowedStates, [CallerMemberName] string methodName = "<unknown>")
        {
            if ((state & allowedStates) == 0)
            {
                var message = $"Connection (${this}) is not in the correct state for the requested operation (${methodName}). Current state: ${state} Allowed states: ${allowedStates}";
                throw new InvalidOperationException(message);
            }
        }
    }

    /// <summary>
    /// Maintains a pair of Client-Server Connection instances.
    /// <see cref="Id">Id</see> uniquely identifies a connected pair.
    /// </summary>
    internal class ConnectionPair
    {
        private readonly Guid id = Guid.NewGuid();
        private readonly SimpleInMemConnection server;
        private readonly SimpleInMemConnection client;

        internal ConnectionPair(SimpleInMemConnection client, SimpleInMemConnection server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (server.ConnectionType != ConnectionType.Server) throw new ArgumentException($"{server.ToString()} - Invalid Server connection type: {server.ConnectionType}");
            if (client.ConnectionType != ConnectionType.Client) throw new ArgumentException($"{client.ToString()} - Invalid Client connection type: {client.ConnectionType}");

            if (server.IsPaired)
            {
                throw new ArgumentException($"{server.ToString()} - already paired connection.");
            }

            if (client.IsPaired)
            {
                throw new ArgumentException($"{client.ToString()} - already paired connection.");
            }

            this.server = server;
            this.client = client;
        }

        internal Guid Id { get { return id; } }

        internal SimpleInMemConnection Server { get { return server; } }

        internal SimpleInMemConnection Client { get { return client; } }
    }
}