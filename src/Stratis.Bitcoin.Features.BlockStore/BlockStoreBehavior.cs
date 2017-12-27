﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.P2P.Peer;
using Stratis.Bitcoin.P2P.Protocol;
using Stratis.Bitcoin.P2P.Protocol.Behaviors;
using Stratis.Bitcoin.P2P.Protocol.Payloads;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Features.BlockStore
{
    public interface IBlockStoreBehavior : INetworkPeerBehavior
    {
        bool CanRespondToGetDataPayload { get; set; }

        bool CanRespondToGetBlocksPayload { get; set; }

        /// <summary>
        /// Sends information about newly discovered blocks to network peers using "headers" or "inv" message.
        /// </summary>
        /// <param name="blocksToAnnounce">List of block headers to announce.</param>
        Task AnnounceBlocksAsync(List<ChainedBlock> blocksToAnnounce);
    }

    public class BlockStoreBehavior : NetworkPeerBehavior, IBlockStoreBehavior
    {
        // TODO: move this to the options
        // Maximum number of headers to announce when relaying blocks with headers message.
        private const int MAX_BLOCKS_TO_ANNOUNCE = 8;

        private readonly ConcurrentChain chain;

        private readonly IBlockRepository blockRepository;

        private readonly IBlockStoreCache blockStoreCache;

        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>Factory for creating loggers.</summary>
        private readonly ILoggerFactory loggerFactory;

        /// <inheritdoc />
        public bool CanRespondToGetBlocksPayload { get; set; }

        /// <inheritdoc />
        public bool CanRespondToGetDataPayload { get; set; }

        // local resources
        public bool PreferHeaders;// public for testing

        private bool preferHeaderAndIDs;

        public BlockStoreBehavior(
            ConcurrentChain chain,
            BlockRepository blockRepository,
            IBlockStoreCache blockStoreCache,
            ILoggerFactory loggerFactory)
            : this(chain, blockRepository as IBlockRepository, blockStoreCache, loggerFactory)
        {
        }

        public BlockStoreBehavior(ConcurrentChain chain, IBlockRepository blockRepository, IBlockStoreCache blockStoreCache, ILoggerFactory loggerFactory)
        {
            Guard.NotNull(chain, nameof(chain));
            Guard.NotNull(blockRepository, nameof(blockRepository));
            Guard.NotNull(blockStoreCache, nameof(blockStoreCache));
            Guard.NotNull(loggerFactory, nameof(loggerFactory));

            this.chain = chain;
            this.blockRepository = blockRepository;
            this.blockStoreCache = blockStoreCache;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.loggerFactory = loggerFactory;

            this.CanRespondToGetBlocksPayload = true;
            this.CanRespondToGetDataPayload = true;

            this.PreferHeaders = false;
            this.preferHeaderAndIDs = false;
        }

        protected override void AttachCore()
        {
            this.logger.LogTrace("()");

            this.AttachedPeer.MessageReceived += this.AttachedNode_MessageReceivedAsync;

            this.logger.LogTrace("(-)");
        }

        protected override void DetachCore()
        {
            this.logger.LogTrace("()");

            this.AttachedPeer.MessageReceived -= this.AttachedNode_MessageReceivedAsync;

            this.logger.LogTrace("(-)");
        }

        private async void AttachedNode_MessageReceivedAsync(NetworkPeer node, IncomingMessage message)
        {
            this.logger.LogTrace("({0}:'{1}',{2}:'{3}')", nameof(node), node?.RemoteSocketEndpoint, nameof(message), message?.Message?.Command);

            try
            {
                await this.ProcessMessageAsync(node, message).ConfigureAwait(false);
            }
            catch (OperationCanceledException opx)
            {
                if (!opx.CancellationToken.IsCancellationRequested)
                    if (this.AttachedPeer?.IsConnected ?? false)
                    {
                        this.logger.LogTrace("(-)[CANCELED_EXCEPTION]");
                        throw;
                    }

                // do nothing
            }
            catch (Exception ex)
            {
                this.logger.LogError("Exception occurred: {0}", ex.ToString());
                throw;
            }

            this.logger.LogTrace("(-)");
        }

        private async Task ProcessMessageAsync(NetworkPeer peer, IncomingMessage message)
        {
            this.logger.LogTrace("({0}:'{1}',{2}:'{3}')", nameof(peer), peer?.RemoteSocketEndpoint, nameof(message), message?.Message?.Command);

            switch (message.Message.Payload)
            {
                case GetDataPayload getDataPayload:
                    if (!this.CanRespondToGetDataPayload)
                    {
                        this.logger.LogTrace("Can't respond to 'getdata'.");
                        break;
                    }

                    await this.ProcessGetDataAsync(peer, getDataPayload).ConfigureAwait(false);
                    break;

                case GetBlocksPayload getBlocksPayload:
                    // TODO: this is not used in core anymore consider deleting it
                    // However, this is required for StratisX to be able to sync from us.

                    if (!this.CanRespondToGetDataPayload)
                    {
                        this.logger.LogTrace("Can't respond to 'getblocks'.");
                        break;
                    }

                    await this.ProcessGetBlocksAsync(peer, getBlocksPayload).ConfigureAwait(false);
                    break;

                case SendCmpctPayload sendCmpctPayload:
                    await this.ProcessSendCmpctPayload(peer, sendCmpctPayload).ConfigureAwait(false);
                    break;

                case SendHeadersPayload sendHeadersPayload:
                    this.PreferHeaders = true;
                    break;
            }

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Processes "getblocks" message received from the peer.
        /// </summary>
        /// <param name="peer">Peer that sent the message.</param>
        /// <param name="getBlocksPayload">Payload of "getblocks" message to process.</param>
        private async Task ProcessGetBlocksAsync(NetworkPeer peer, GetBlocksPayload getBlocksPayload)
        {
            this.logger.LogTrace("({0}:'{1}',{2}:'{3}')", nameof(peer), peer.RemoteSocketEndpoint, nameof(getBlocksPayload), getBlocksPayload);

            ChainedBlock chainedBlock = this.chain.FindFork(getBlocksPayload.BlockLocators);
            if (chainedBlock == null)
            {
                this.logger.LogTrace("(-)[NO_FORK_POINT]");
                return;
            }

            bool sendTip = true;
            ChainedBlock lastAddedChainedBlock = null;
        	var inv = new InvPayload();
        	for (int limit = 0; limit < InvPayload.MaxGetBlocksInventorySize; limit++)
        	{
        		chainedBlock = this.chain.GetBlock(chainedBlock.Height + 1);
                if (chainedBlock.HashBlock == getBlocksPayload.HashStop)
                {
                    this.logger.LogTrace("Hash stop has been reached.");
                    break;
                }

                this.logger.LogTrace("Adding block '{0}' to the inventory.", chainedBlock);
                lastAddedChainedBlock = chainedBlock;
                inv.Inventory.Add(new InventoryVector(InventoryType.MSG_BLOCK, chainedBlock.HashBlock));
                if (this.chain.Tip.HashBlock == chainedBlock.HashBlock)
                {
                    this.logger.LogTrace("Tip of the chain has been reached.");
                    sendTip = false;
                }
        	}

            int count = inv.Inventory.Count;
            if (count > 0)
            {
                ChainHeadersBehavior chainBehavior = peer.Behavior<ChainHeadersBehavior>();
                ChainedBlock peerTip = chainBehavior.PendingTip;

                // New nodes do not use "getblocks" messages and their syncing is based on "getheaders"
                // messages. StratisX nodes do need "getblocks", but then our node tries to sync 
                // with the modern method. So we need to disable it to prevent collisions of those two methods.
                // However, this should be only done if the peer is syncing from us and not vice versa.
                // And when the sync is done, we should enable it again.
                chainBehavior.CanSync = (peerTip != null) && (this.chain.Tip.ChainWork > peerTip.ChainWork);

                this.logger.LogTrace("Setting peer's pending tip to '{0}'.", lastAddedChainedBlock);
                int peersHeight = peerTip != null ? peerTip.Height : 0;
                if (peersHeight < lastAddedChainedBlock.Height) chainBehavior.SetPendingTip(lastAddedChainedBlock);

                this.logger.LogTrace("Sending inventory with {0} block hashes.", count);
                await peer.SendMessageAsync(inv).ConfigureAwait(false);

                if (sendTip)
                {
                    // In order for the peer to send us the next "getblocks" message
                    // to continue the syncing process, we need to send an inventory message
                    // with our chain tip.
                    var invContinue = new InvPayload();
                    invContinue.Inventory.Add(new InventoryVector(InventoryType.MSG_BLOCK, this.chain.Tip.HashBlock));
                    await peer.SendMessageAsync(invContinue).ConfigureAwait(false);
                }
            }
            else this.logger.LogTrace("Nothing to send.");

            this.logger.LogTrace("(-)");
        }

        private Task ProcessSendCmpctPayload(NetworkPeer peer, SendCmpctPayload sendCmpct)
        {
            // TODO: announce using compact blocks
            return Task.CompletedTask;
        }

        private async Task ProcessGetDataAsync(NetworkPeer peer, GetDataPayload getDataPayload)
        {
            this.logger.LogTrace("({0}:'{1}',{2}.{3}.{4}:{5})", nameof(peer), peer?.RemoteSocketEndpoint, nameof(getDataPayload), nameof(getDataPayload.Inventory), nameof(getDataPayload.Inventory.Count), getDataPayload.Inventory.Count);
            Guard.Assert(peer != null);

            // TODO: bring logic from core
            foreach (InventoryVector item in getDataPayload.Inventory.Where(inv => inv.Type.HasFlag(InventoryType.MSG_BLOCK)))
            {
                // TODO: check if we need to add support for "not found"
                Block block = await this.blockStoreCache.GetBlockAsync(item.Hash).ConfigureAwait(false);

                if (block != null)
                {
                    this.logger.LogTrace("Sending block '{0}' to peer '{1}'.", item.Hash, peer?.RemoteSocketEndpoint);

                    //TODO strip block of witness if node does not support
                    await peer.SendMessageAsync(new BlockPayload(block.WithOptions(peer.SupportedTransactionOptions))).ConfigureAwait(false);
                }
            }

            this.logger.LogTrace("(-)");
        }

        private async Task SendAsBlockInventoryAsync(NetworkPeer peer, IEnumerable<uint256> blocks)
        {
            this.logger.LogTrace("({0}:'{1}',{2}.Count:{3})", nameof(peer), peer?.RemoteSocketEndpoint, nameof(blocks), blocks.Count());

            var queue = new Queue<InventoryVector>(blocks.Select(s => new InventoryVector(InventoryType.MSG_BLOCK, s)));
            while (queue.Count > 0)
            {
                var items = queue.TakeAndRemove(ConnectionManager.MaxInventorySize).ToArray();
                if (peer.IsConnected)
                {
                    this.logger.LogTrace("Sending inventory message to peer '{0}'.", peer.RemoteSocketEndpoint);
                    await peer.SendMessageAsync(new InvPayload(items)).ConfigureAwait(false);
                }
            }

            this.logger.LogTrace("(-)");
        }

        /// <inheritdoc />
        public async Task AnnounceBlocksAsync(List<ChainedBlock> blocksToAnnounce)
        {
            Guard.NotNull(blocksToAnnounce, nameof(blocksToAnnounce));
            this.logger.LogTrace("({0}.{1}:{2})", nameof(blocksToAnnounce), nameof(blocksToAnnounce.Count), blocksToAnnounce.Count);

            if (!blocksToAnnounce.Any())
            {
                this.logger.LogTrace("(-)[NO_BLOCKS]");
                return;
            }

            NetworkPeer peer = this.AttachedPeer;
            if (peer == null)
            {
                this.logger.LogTrace("(-)[NO_PEER]");
                return;
            }

            bool revertToInv = ((!this.PreferHeaders &&
                                 (!this.preferHeaderAndIDs || blocksToAnnounce.Count > 1)) ||
                                blocksToAnnounce.Count > MAX_BLOCKS_TO_ANNOUNCE);

            this.logger.LogTrace("Block propagation preferences of the peer '{0}': prefer headers - {1}, prefer headers and IDs - {2}, will{3} revert to 'inv' now.", peer.RemoteSocketEndpoint, this.PreferHeaders, this.preferHeaderAndIDs, revertToInv ? "" : " NOT");

            var headers = new List<BlockHeader>();
            var inventoryBlockToSend = new List<uint256>();

            var chainBehavior = peer.Behavior<ChainHeadersBehavior>();
            ChainedBlock bestIndex = null;
            if (!revertToInv)
            {
                bool foundStartingHeader = false;
                // Try to find first chained block that the peer doesn't have, and then add all chained blocks past that one.

                foreach (ChainedBlock chainedBlock in blocksToAnnounce)
                {
                    bestIndex = chainedBlock;

                    if (!foundStartingHeader)
                    {
                        this.logger.LogTrace("Checking is the peer '{0}' can connect header '{1}'.", peer.RemoteSocketEndpoint, chainedBlock);

                        // Peer doesn't have a block at the height of our block and with the same hash?
                        if (chainBehavior.PendingTip?.FindAncestorOrSelf(chainedBlock) != null)
                        {
                            this.logger.LogTrace("Peer '{0}' already has header '{1}'.", peer.RemoteSocketEndpoint, chainedBlock.Previous);
                            continue;
                        }

                        // Peer doesn't have a block at the height of our block.Previous and with the same hash?
                        if (chainBehavior.PendingTip?.FindAncestorOrSelf(chainedBlock.Previous) == null)
                        {
                            // Peer doesn't have this header or the prior one - nothing will connect, so bail out.
                            this.logger.LogTrace("Neither the header nor its previous header found for peer '{0}', reverting to 'inv'.", peer.RemoteSocketEndpoint);
                            revertToInv = true;
                            break;
                        }

                        this.logger.LogTrace("Peer '{0}' can connect header '{1}'.", peer.RemoteSocketEndpoint, chainedBlock.Previous);
                        foundStartingHeader = true;
                    }

                    // If we reached here then it means that we've found starting header.
                    headers.Add(chainedBlock.Header);
                }
            }

            if (!revertToInv && headers.Any())
            {
                if ((headers.Count == 1) && this.preferHeaderAndIDs)
                {
                    // TODO:
                }
                else if (this.PreferHeaders)
                {
                    if (headers.Count > 1) this.logger.LogDebug("Sending {0} headers, range {1} - {2}, to peer '{3}'.", headers.Count, headers.First(), headers.Last(), peer.RemoteSocketEndpoint);
                    else this.logger.LogDebug("Sending header '{0}' to peer '{1}'.", headers.First(), peer.RemoteSocketEndpoint);

                    chainBehavior.SetPendingTip(bestIndex);
                    await peer.SendMessageAsync(new HeadersPayload(headers.ToArray())).ConfigureAwait(false);
                    this.logger.LogTrace("(-)[SEND_HEADERS_PAYLOAD]");
                    return;
                }
                else
                {
                    revertToInv = true;
                }
            }

            if (revertToInv)
            {
                // If falling back to using an inv, just try to inv the tip.
                // The last entry in 'blocksToAnnounce' was our tip at some point in the past.

                if (blocksToAnnounce.Any())
                {
                    ChainedBlock chainedBlock = blocksToAnnounce.Last();
                    if (chainedBlock != null)
                    {
                        if ((chainBehavior.PendingTip == null) || (chainBehavior.PendingTip.GetAncestor(chainedBlock.Height) == null))
                        {
                            inventoryBlockToSend.Add(chainedBlock.HashBlock);
                            this.logger.LogDebug("Sending inventory hash '{0}' to peer '{1}'.", chainedBlock.HashBlock, peer.RemoteSocketEndpoint);
                        }
                    }
                }
            }

            if (inventoryBlockToSend.Any())
            {
                await this.SendAsBlockInventoryAsync(peer, inventoryBlockToSend).ConfigureAwait(false);
                this.logger.LogTrace("(-)[SEND_INVENTORY]");
                return;
            }

            this.logger.LogTrace("(-)");
        }

        public override object Clone()
        {
            this.logger.LogTrace("()");

            var res = new BlockStoreBehavior(this.chain, this.blockRepository, this.blockStoreCache, this.loggerFactory)
            {
                CanRespondToGetBlocksPayload = this.CanRespondToGetBlocksPayload,
                CanRespondToGetDataPayload = this.CanRespondToGetDataPayload
            };

            this.logger.LogTrace("(-)");
            return res;
        }
    }
}
