using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.OS.Network.Application;
using AElf.OS.Network.Infrastructure;
using AElf.OS.Network.Types;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace AElf.OS.Network.Grpc
{
    public class GrpcPeer : IPeer
    {
        private const int MaxMetricsPerMethod = 100;
        private const int BlockRequestTimeout = 300;
        private const int BlocksRequestTimeout = 500;
        private const int GetNodesTimeout = 500;

        private const int FinalizeConnectTimeout = 500;
        private const int UpdateHandshakeTimeout = 400;
        
        private enum MetricNames
        {
            Announce,
            GetBlocks,
            GetBlock,
            PreLibAnnounce,
            PreLibConfirm
        };
        
        private readonly Channel _channel;
        private readonly PeerService.PeerServiceClient _client;
        
        /// <summary>
        /// Property that describes a valid state. Valid here means that the peer is ready to be used for communications.
        /// </summary>
        public bool IsReady
        {
            get { return (_channel.State == ChannelState.Idle || _channel.State == ChannelState.Ready) && IsConnected; }
        }
        
        public long LastKnownLibHeight { get; private set; }

        public bool IsBest { get; set; }
        public bool IsConnected { get; set; }
        public Hash CurrentBlockHash { get; private set; }
        public long CurrentBlockHeight { get; private set; }

        public IReadOnlyDictionary<long, AcceptedBlockInfo> RecentBlockHeightAndHashMappings { get; }
        private readonly ConcurrentDictionary<long, AcceptedBlockInfo> _recentBlockHeightAndHashMappings;
        
        public string IpAddress { get; }

        public PeerInfo Info { get; }

        public IReadOnlyDictionary<long, PreLibBlockInfo> PreLibBlockHeightAndHashMappings { get; }
        private readonly ConcurrentDictionary<long, PreLibBlockInfo> _preLibBlockHeightAndHashMappings;
       
        public bool CanStreamTransactions { get; private set; } = false;
        public bool CanStreamAnnounces { get; private set; } = false;
        
        public bool CanStreamPreLibAnnounces { get; private set; } = false;
        public bool CanStreamPreLibConfirmAnnounces { get; private set; } = false;
        
        public bool CanStreamBlocks { get; private set; }
        
        public IReadOnlyDictionary<string, ConcurrentQueue<RequestMetric>> RecentRequestsRoundtripTimes { get; }
        private readonly ConcurrentDictionary<string, ConcurrentQueue<RequestMetric>> _recentRequestsRoundtripTimes;
        
        private AsyncClientStreamingCall<Transaction, VoidReply> _transactionStreamCall;
        private AsyncClientStreamingCall<BlockAnnouncement, VoidReply> _announcementStreamCall;
        private AsyncClientStreamingCall<PreLibAnnouncement, VoidReply> _preLibAnnounceStreamCall;
        private AsyncClientStreamingCall<PreLibConfirmAnnouncement, VoidReply> _preLibConfirmAnnounceStreamCall; 

        public GrpcPeer(Channel channel, PeerService.PeerServiceClient client, string ipAddress, PeerInfo peerInfo)
        {
            _channel = channel;
            _client = client;

            IpAddress = ipAddress;
            Info = peerInfo;

            _recentBlockHeightAndHashMappings = new ConcurrentDictionary<long, AcceptedBlockInfo>();
            RecentBlockHeightAndHashMappings = new ReadOnlyDictionary<long, AcceptedBlockInfo>(_recentBlockHeightAndHashMappings);
            
            _preLibBlockHeightAndHashMappings = new ConcurrentDictionary<long, PreLibBlockInfo>();
            PreLibBlockHeightAndHashMappings = new ReadOnlyDictionary<long, PreLibBlockInfo>(_preLibBlockHeightAndHashMappings);
            
            _recentRequestsRoundtripTimes = new ConcurrentDictionary<string, ConcurrentQueue<RequestMetric>>();
            RecentRequestsRoundtripTimes =
                new ReadOnlyDictionary<string, ConcurrentQueue<RequestMetric>>(_recentRequestsRoundtripTimes);

            _recentRequestsRoundtripTimes.TryAdd(nameof(MetricNames.Announce), new ConcurrentQueue<RequestMetric>());
            _recentRequestsRoundtripTimes.TryAdd(nameof(MetricNames.GetBlock), new ConcurrentQueue<RequestMetric>());
            _recentRequestsRoundtripTimes.TryAdd(nameof(MetricNames.GetBlocks), new ConcurrentQueue<RequestMetric>());
            _recentRequestsRoundtripTimes.TryAdd(nameof(MetricNames.PreLibAnnounce),
                new ConcurrentQueue<RequestMetric>());
            _recentRequestsRoundtripTimes.TryAdd(nameof(MetricNames.PreLibConfirm),
                new ConcurrentQueue<RequestMetric>());
        }
        
        public Dictionary<string, List<RequestMetric>> GetRequestMetrics()
        {
            Dictionary<string, List<RequestMetric>> metrics = new Dictionary<string, List<RequestMetric>>();

            foreach (var roundtripTime in _recentRequestsRoundtripTimes.ToArray())
            {
                var metricsToAdd = new List<RequestMetric>();
                
                metrics.Add(roundtripTime.Key, metricsToAdd);
                foreach (var requestMetric in roundtripTime.Value)
                {
                    metricsToAdd.Add(requestMetric);
                }
            }

            return metrics;
        }
        
        public async Task UpdateHandshakeAsync()
        {
            GrpcRequest request = new GrpcRequest
            {
                ErrorMessage = "Error while updating handshake."
            };
            
            Metadata data = new Metadata
            {
                {GrpcConstants.TimeoutMetadataKey, UpdateHandshakeTimeout.ToString()}
            };
            
            var handshake = await RequestAsync(_client, c => c.UpdateHandshakeAsync(new UpdateHandshakeRequest(), data), request);
             
            if (handshake != null)
                LastKnownLibHeight = handshake.LibBlockHeight;
        }

        public Task<NodeList> GetNodesAsync(int count = NetworkConstants.DefaultDiscoveryMaxNodesToRequest)
        {
            GrpcRequest request = new GrpcRequest
            {
                ErrorMessage = $"Request nodes failed."
            };
            
            Metadata data = new Metadata
            {
                {GrpcConstants.TimeoutMetadataKey, GetNodesTimeout.ToString()}
            };
            
            return RequestAsync(_client, c => c.GetNodesAsync(new NodesRequest { MaxCount = count }, data), request);
        }
        
        public async Task<FinalizeConnectReply> FinalizeConnectAsync(Handshake handshake)
        {
            GrpcRequest request = new GrpcRequest { ErrorMessage = $"Error while finalizing request to {this}." };
            Metadata data = new Metadata { {GrpcConstants.TimeoutMetadataKey, FinalizeConnectTimeout.ToString()} };

            var finalizeConnectReply = await RequestAsync(_client, c => c.FinalizeConnectAsync(handshake, data), request);
            
            IsConnected = finalizeConnectReply.Success;
            
            return finalizeConnectReply;
        }

        public async Task<BlockWithTransactions> GetBlockByHashAsync(Hash hash)
        {
            var blockRequest = new BlockRequest {Hash = hash};

            GrpcRequest request = new GrpcRequest
            {
                ErrorMessage = $"Block request for {hash} failed.",
                MetricName = nameof(MetricNames.GetBlock),
                MetricInfo = $"Block request for {hash}"
            };

            Metadata data = new Metadata { {GrpcConstants.TimeoutMetadataKey, BlockRequestTimeout.ToString()} };

            var blockReply 
                = await RequestAsync(_client, c => c.RequestBlockAsync(blockRequest, data), request);

            return blockReply?.Block;
        }

        public async Task<List<BlockWithTransactions>> GetBlocksAsync(Hash firstHash, int count)
        {
            var blockRequest = new BlocksRequest {PreviousBlockHash = firstHash, Count = count};
            var blockInfo = $"{{ first: {firstHash}, count: {count} }}";
            
            GrpcRequest request = new GrpcRequest
            {
                ErrorMessage = $"Get blocks for {blockInfo} failed.",
                MetricName = nameof(MetricNames.GetBlocks),
                MetricInfo = $"Get blocks for {blockInfo}"
            };

            Metadata data = new Metadata { {GrpcConstants.TimeoutMetadataKey, BlocksRequestTimeout.ToString()} };

            var list = await RequestAsync(_client, c => c.RequestBlocksAsync(blockRequest, data), request);

            if (list == null)
                return new List<BlockWithTransactions>();

            return list.Blocks.ToList();
        }

        #region Streaming

        /// <summary>
        /// Send a announcement to the peer using the stream call.
        /// Note: this method is not thread safe.
        /// </summary>
        public async Task SendAnnouncementAsync(BlockAnnouncement header)
        {
            if (!IsConnected)
                return;
            
            if (_announcementStreamCall == null)
                _announcementStreamCall = _client.AnnouncementBroadcastStream();
            
            try
            {
                await _announcementStreamCall.RequestStream.WriteAsync(header);
            }
            catch (RpcException e)
            {
                _announcementStreamCall.Dispose();
                _announcementStreamCall = null;
                
                HandleFailure(e, $"Error during announcement broadcast: {header.BlockHash}.");
            }
            catch (Exception e)
            {
                throw new NetworkException($"Failed stream to {this}: ", e);
            }
        }
        
        public async Task SendPreLibAnnounceAsync(PreLibAnnouncement preLibAnnouncement)
        {
            if (!IsConnected)
                return;
            
            if (_preLibAnnounceStreamCall == null)
                _preLibAnnounceStreamCall = _client.PreLibAnnounceStream();
            
            try
            {
                await _preLibAnnounceStreamCall.RequestStream.WriteAsync(preLibAnnouncement);
            }
            catch (RpcException e)
            {
                _preLibAnnounceStreamCall.Dispose();
                _preLibAnnounceStreamCall = null;
                
                HandleFailure(e, $"Error during pre lib broadcast: {preLibAnnouncement.BlockHash}.");
            }
            catch (Exception e)
            {
                throw new NetworkException($"Failed stream to {this}: ", e);
            }
        }

        public async Task SendPreLibConfirmAnnounceAsync(PreLibConfirmAnnouncement preLibConfirmAnnouncement)
        { 
            if (!IsConnected)
                return;
            
            if (_preLibConfirmAnnounceStreamCall == null)
                _preLibConfirmAnnounceStreamCall = _client.PreLibConfirmAnnounceStream();
            
            try
            {
                await _preLibConfirmAnnounceStreamCall.RequestStream.WriteAsync(preLibConfirmAnnouncement);
            }
            catch (RpcException e)
            {
                _preLibConfirmAnnounceStreamCall.Dispose();
                _preLibConfirmAnnounceStreamCall = null;
                
                HandleFailure(e, $"Error during pre lib confirm broadcast: {preLibConfirmAnnouncement.BlockHash}.");
            }
            catch (Exception e)
            {
                throw new NetworkException($"Failed stream to {this}: ", e);
            }
        }
        
        
        /// <summary>
        /// Send a transaction to the peer using the stream call.
        /// Note: this method is not thread safe.
        /// </summary>
        public async Task SendTransactionAsync(Transaction transaction)
        {
            if (!IsConnected)
                return;
                
            if (_transactionStreamCall == null)
                _transactionStreamCall = _client.TransactionBroadcastStream();

            try
            {
                await _transactionStreamCall.RequestStream.WriteAsync(transaction);
            }
            catch (RpcException e)
            {
                _transactionStreamCall.Dispose();
                _transactionStreamCall = null;
                
                HandleFailure(e, $"Error during transaction broadcast: {transaction.GetHash()}.");
            }
            catch (Exception e)
            {
                throw new NetworkException($"Failed stream to {this}: ", e);
            }
        }

        #endregion
        
        private async Task<TResp> RequestAsync<TResp>(PeerService.PeerServiceClient client,
            Func<PeerService.PeerServiceClient, AsyncUnaryCall<TResp>> func, GrpcRequest requestParams)
        {
            var metricsName = requestParams.MetricName;
            bool timeRequest = !string.IsNullOrEmpty(metricsName);
            var requestStartTime = TimestampHelper.GetUtcNow();
            
            Stopwatch requestTimer = null;
            
            if (timeRequest)
                requestTimer = Stopwatch.StartNew();
                
            try
            {
                var response = await func(client);

                if (timeRequest)
                {
                    requestTimer.Stop();
                    RecordMetric(requestParams, requestStartTime, requestTimer.ElapsedMilliseconds);
                }
                
                return response;
            }
            catch (AggregateException e)
            {
                HandleFailure(e.Flatten(), requestParams.ErrorMessage);
            }
            finally
            {
                if (timeRequest)
                {
                    requestTimer.Stop();
                    RecordMetric(requestParams, requestStartTime, requestTimer.ElapsedMilliseconds);
                }
            }

            return default(TResp);
        }

        private void RecordMetric(GrpcRequest grpcRequest, Timestamp requestStartTime, long elapsedMilliseconds)
        {
            var metrics = _recentRequestsRoundtripTimes[grpcRequest.MetricName];
                    
            while (metrics.Count >= MaxMetricsPerMethod)
                metrics.TryDequeue(out _);
                    
            metrics.Enqueue(new RequestMetric
            {
                Info = grpcRequest.MetricInfo,
                RequestTime = requestStartTime,
                MethodName = grpcRequest.MetricName,
                RoundTripTime = elapsedMilliseconds
            });
        }

        /// <summary>
        /// This method handles the case where the peer is potentially down. If the Rpc call
        /// put the channel in TransientFailure or Connecting, we give the connection a certain time to recover.
        /// </summary>
        private void HandleFailure(Exception exception, string errorMessage)
        {
            // If channel has been shutdown (unrecoverable state) remove it.
            string message = $"Failed request to {this}: {errorMessage}";
            NetworkExceptionType type = NetworkExceptionType.Rpc;
            
            if (_channel.State == ChannelState.Shutdown)
            {
                message = $"Peer is shutdown - {this}: {errorMessage}";
                type = NetworkExceptionType.Unrecoverable;
            }
            else if (_channel.State == ChannelState.TransientFailure || _channel.State == ChannelState.Connecting)
            {
                message = $"Failed request to {this}: {errorMessage}";
                type = NetworkExceptionType.PeerUnstable;
            }
            else if (exception.InnerException is RpcException rpcEx && rpcEx.StatusCode == StatusCode.Cancelled)
            {
                message = $"Failed request to {this}: {errorMessage}";
                type = NetworkExceptionType.Unrecoverable;
            }
            
            throw new NetworkException(message, exception, type);
        }

        public async Task<bool> TryRecoverAsync()
        {
            await _channel.TryWaitForStateChangedAsync(_channel.State,
                DateTime.UtcNow.AddSeconds(NetworkConstants.DefaultPeerDialTimeoutInMilliSeconds));

            // Either we connected again or the state change wait timed out.
            if (_channel.State == ChannelState.TransientFailure || _channel.State == ChannelState.Connecting)
                return false;

            return true;
        }
        
        public void ProcessReceivedAnnouncement(BlockAnnouncement blockAnnouncement)
        {
            CurrentBlockHeight = blockAnnouncement.BlockHeight;
            CurrentBlockHash = blockAnnouncement.BlockHash;
            if (_recentBlockHeightAndHashMappings.TryGetValue(CurrentBlockHeight, out var blockInfo))
            {
                if (blockAnnouncement.HasFork || blockInfo.BlockHash != CurrentBlockHash)
                {
                    blockInfo.HasFork = true;
                }
            }
            else
            {
                blockInfo = new AcceptedBlockInfo
                {
                    BlockHash = CurrentBlockHash,
                    HasFork = false
                };
            }
            
            _recentBlockHeightAndHashMappings[CurrentBlockHeight] = blockInfo;
            while (_recentBlockHeightAndHashMappings.Count > 20)
            {
                _recentBlockHeightAndHashMappings.TryRemove(_recentBlockHeightAndHashMappings.Keys.Min(), out _);
            }
        }

        public void ProcessReceivedPreLibAnnounce(PreLibAnnouncement preLibAnnouncement)
        {
            var blockHeight = preLibAnnouncement.BlockHeight;
            var blockHash = preLibAnnouncement.BlockHash;
            var preLibCount = preLibAnnouncement.PreLibCount;
            if (_preLibBlockHeightAndHashMappings.TryGetValue(blockHeight, out var preLibBlockInfo))
            {
                if (preLibBlockInfo.BlockHash != blockHash)
                    return;
                if(preLibCount > preLibBlockInfo.PreLibCount)
                    preLibBlockInfo.PreLibCount = preLibCount;
            }
            else
            {
                preLibBlockInfo = new PreLibBlockInfo
                {
                    BlockHash = blockHash,
                    PreLibCount = preLibCount
                };
            }

            _preLibBlockHeightAndHashMappings[blockHeight] = preLibBlockInfo;
            while (_preLibBlockHeightAndHashMappings.Count > 20)
            {
                _preLibBlockHeightAndHashMappings.TryRemove(_preLibBlockHeightAndHashMappings.Keys.Min(), out _);
            }
        }

        public bool HasBlock(long blockHeight, Hash blockHash)
        {
            return _recentBlockHeightAndHashMappings.TryGetValue(blockHeight, out var blockInfo) &&
                   blockInfo.BlockHash == blockHash && !blockInfo.HasFork;
        }

        public bool HasPreLib(long blockHeight, Hash blockHash)
        {
            return _preLibBlockHeightAndHashMappings.TryGetValue(blockHeight, out var preLibBlockInfo) &&
                preLibBlockInfo.BlockHash == blockHash;
        }
        
        public async Task DisconnectAsync(bool gracefulDisconnect)
        {
            IsConnected = false;
            
            // send disconnect message if the peer is still connected and the connection
            // is stable.
            if (gracefulDisconnect && IsReady)
            {
                GrpcRequest request = new GrpcRequest { ErrorMessage = "Error while sending disconnect." };
                
                try
                {
                    await RequestAsync(_client, c => c.DisconnectAsync(new DisconnectReason {Why = DisconnectReason.Types.Reason.Shutdown}), request);
                }
                catch (NetworkException)
                {
                    // swallow the exception, we don't care because we're disconnecting.
                }
            }
            
            try
            {
                await _channel.ShutdownAsync();
            }
            catch (InvalidOperationException)
            {
                // if channel already shutdown
            }
        }

        public override string ToString()
        {
            return $"{{ listening-port: {IpAddress}, key: {Info.Pubkey.Substring(0, 45)}... }}";
        }
    }
}