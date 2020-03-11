﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientWebSocket.Pipeline;
using ClientWebSocket.Pipeline.EventArguments;
using EnumsNET;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Socket.Io.Client.Core.EventArguments;
using Socket.Io.Client.Core.Model;
using Socket.Io.Client.Core.Parse;
using Socket.Io.Client.Core.Processor;
using Socket.Io.Client.Core.Extensions;

namespace Socket.Io.Client.Core
{
    public partial class SocketIoClient : ISocketIoClient
    {
        private readonly ILogger<SocketIoClient> _logger;
        private readonly PipelineWebSocket _socket;
        private readonly IDictionary<PacketType, IPacketProcessor> _packetProcessors;
        private readonly IDictionary<string, List<object>> _events;
        private readonly IDictionary<int, object> _callbacks;

        private int _packetId = -1;
        private string _namespace = "/";
        private TimeSpan _pingInterval = TimeSpan.FromSeconds(5);
        private CancellationTokenSource _cts;

        public SocketIoClient(SocketIoOptions options = null, ILogger<SocketIoClient> logger = null)
        {
            State = ReadyState.Closed;
            Options = options ?? new SocketIoOptions();
            _logger = logger ?? NullLogger<SocketIoClient>.Instance;
            _packetProcessors = new Dictionary<PacketType, IPacketProcessor>
            {
                { PacketType.Open, new OpenPacketProcessor(this, Options.JsonSerializerOptions, _logger) },
                { PacketType.Pong, new PongPacketProcessor(this, _logger) },
                { PacketType.Error, new ErrorPacketProcessor(this, _logger) },
                { PacketType.Message, new MessagePacketProcessor(this, _logger) },
            };
            _callbacks = new Dictionary<int, object>();
            _events = new Dictionary<string, List<object>>();
            _cts = new CancellationTokenSource();
            _socket = new PipelineWebSocket();
            _socket.OnMessage += OnSocketMessageAsync;
        }

        private bool HasDefaultNamespace => string.IsNullOrEmpty(_namespace) || _namespace == SocketIo.DefaultNamespace;

        private SocketIoOptions Options { get; }

        public ReadyState State { get; private set; }

        #region ISocketIoClient Implementation

        public async Task OpenAsync(Uri uri)
        {
            InitializeSocketIoEvents(_events);
            SubscribeToEvents();
            State = ReadyState.Opening;

            _namespace = uri.LocalPath;
            var socketIoUri = uri.HttpToSocketIoWs(path: !HasDefaultNamespace ? _namespace : null);
            await _socket.StartAsync(socketIoUri);
        }

        public async Task CloseAsync()
        {
            if (State == ReadyState.Closing || State == ReadyState.Closed)
                throw new InvalidOperationException($"Socket is in state: {State} and cannot be closed.");

            try
            {
                State = ReadyState.Closing;
                await SendAsync(PacketType.Message, PacketSubType.Disconnect, null);
                await this.EmitAsync(SocketIoEvent.Close);
                await _socket.StopAsync();
            }
            finally
            {
                await Cleanup();
                State = ReadyState.Closed;
            }
        }

        ValueTask ISocketIoClient.SendAsync(Packet packet)
        {
            var data = PacketParser.Encode(packet, Options.Encoding);
            return _socket.SendAsync(data);
        }

        #endregion

        private ValueTask OnSocketMessageAsync(object sender, IMemoryOwner<byte> data)
        {
            try
            {
                if (!PacketParser.TryDecode(data.Memory, Options.Encoding, out var packet))
                {
                    _logger.LogError($"Could not decode packet from data: {Options.Encoding.GetString(data.Memory.Span)}");
                    return default;
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace($"Processing packet: {packet}");

                if (!_packetProcessors.TryGetValue(packet.Type, out var processor))
                {
                    _logger.LogWarning($"Unsupported packet type: {packet.Type}. Data: {packet}");
                }
                else
                {
                    return processor.ProcessAsync(packet);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing socket data");
                this.EmitAsync(SocketIoEvent.Error, new SocketErrorEventArgs(ex));
            }

            return default;
        }

        private void StartPingPong()
        {
            Task.Run(async () =>
            {
                while (State == ReadyState.Open && !_cts.IsCancellationRequested)
                {
                    try
                    {
                        await SendAndValidatePingAsync();
                        _cts.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error while performing ping/pong.");
                        await this.EmitAsync(SocketIoEvent.Error, new ErrorEventArgs(ex));
                    }
                    finally
                    {
                        await Task.Delay(_pingInterval, _cts.Token);
                    }
                }
            });
        }

        private async ValueTask SendAndValidatePingAsync()
        {
            var data = string.Empty;

            //ping packet does not have subtype
            await SendAsync(PacketType.Ping, null, data);
            OnOnce(SocketIoEvent.Pong, (Func<PongEventArgs, ValueTask>)OnPong);

            ValueTask OnPong(PongEventArgs args)
            {
                if (data != args.Pong.Data)
                {
                    _logger.LogError($"Server responded with incorrect pong packet. Ping data: [{data}] Pong packet: [{args.Pong}]");
                    return this.EmitAsync(SocketIoEvent.ProbeError, new ErrorEventArgs("Server responded with incorrect pong."));
                }

                return this.EmitAsync(SocketIoEvent.ProbeSuccess);
            }
        }

        private ValueTask Cleanup()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _callbacks.Clear();
            _events.Clear();
            return default;
        }

        #region Helpers

        private void SubscribeToEvents()
        {
            On(SocketIoEvent.Connect, OnConnect);
            On(SocketIoEvent.Handshake, (Func<HandshakeData, ValueTask>)OnHandshake);
            On(SocketIoEvent.Open, OnOpen);
        }

        private int GetNextPacketId() => Interlocked.Increment(ref _packetId);

        private void InitializeSocketIoEvents(IDictionary<string, List<object>> events)
        {
            Enums.GetValues<SocketIoEvent>().Select(e => events[SocketIo.Event.Name[e]] = new List<object>());
        }

        private ValueTask SendAsync(Packet packet) => ((ISocketIoClient)this).SendAsync(packet);

        private ValueTask SendAsync(PacketType type, PacketSubType? subType, string data, int? id = null)
        {
            return SendAsync(new Packet(type, subType, _namespace, data, id, 0, null));
        }

        private ValueTask SendEmitAsync<TData>(string eventName, TData data = default, int? packetId = null)
        {
            var sb = new StringBuilder()
                     .Append("[\"")
                     .Append(eventName)
                     .Append("\"");

            if (data != null)
            {
                sb.Append(",");
                sb.Append(JsonSerializer.Serialize(data, Options.JsonSerializerOptions));
            }

            sb.Append("]");

            return SendAsync(PacketType.Message, PacketSubType.Event, sb.ToString(), packetId);
        }

        private ValueTask OnHandshake(HandshakeData arg)
        {
            _pingInterval = TimeSpan.FromMilliseconds(arg.PingInterval);
            return default;
        }

        private ValueTask OnConnect()
        {
            State = ReadyState.Open;
            StartPingPong();
            return default;
        }

        private async ValueTask OnOpen()
        {
            if (!HasDefaultNamespace)
            {
                _logger.LogDebug($"Sending connect to namespace: {_namespace}");
                await SendAsync(PacketType.Message, PacketSubType.Connect, null);
            }
        }

        #endregion
    }
}