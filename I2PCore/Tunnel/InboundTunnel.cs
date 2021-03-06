﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using I2PCore.Tunnel.I2NP.Messages;
using I2PCore.Data;
using I2PCore.Utils;
using I2PCore.Router;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using I2PCore.Tunnel.I2NP.Data;
using I2PCore.Transport;
using System.Threading;

namespace I2PCore.Tunnel
{
    public class InboundTunnel: Tunnel
    {
        I2PIdentHash RemoteGateway;
        public override I2PIdentHash Destination { get { return RemoteGateway; } }

        internal I2PTunnelId GatewayTunnelId;

        internal bool Fake0HopTunnel;
        internal TunnelInfo TunnelSetup;

        public readonly uint TunnelBuildReplyMessageId = BufUtils.RandomUint();
        public readonly int OutTunnelHops;

        static object DeliveryStatusReceivedLock = new object();
        public static event Action<DeliveryStatusMessage> DeliveryStatusReceived;

        object GarlicMessageReceivedLock = new object();
        public event Action<GarlicMessage> GarlicMessageReceived;

        public InboundTunnel( TunnelConfig config, int outtunnelhops ): base( config )
        {
            if ( config != null )
            {
                Fake0HopTunnel = false;
                TunnelSetup = config.Info;
                OutTunnelHops = outtunnelhops;

                var gw = TunnelSetup.Hops[0];
                RemoteGateway = gw.Peer.IdentHash;
                GatewayTunnelId = gw.TunnelId;

                ReceiveTunnelId = TunnelSetup.Hops.Last().TunnelId;

                DebugUtils.LogDebug( "InboundTunnel: Tunnel " + Destination.Id32Short + " created." );
            }
            else
            {
                Fake0HopTunnel = true;

                var hops = new List<HopInfo>();
                hops.Add( new HopInfo( RouterContext.Inst.MyRouterIdentity, new I2PTunnelId() ) );
                TunnelSetup = new TunnelInfo( hops );

                Config = new TunnelConfig(
                    TunnelConfig.TunnelDirection.Inbound,
                    TunnelConfig.TunnelRole.Endpoint,
                    TunnelConfig.TunnelPool.Exploratory,
                    TunnelSetup );

                ReceiveTunnelId = TunnelSetup.Hops.Last().TunnelId;
                RemoteGateway = RouterContext.Inst.MyRouterIdentity.IdentHash;
                GatewayTunnelId = ReceiveTunnelId;

                DebugUtils.LogDebug( "InboundTunnel " + TunnelDebugTrace + ": 0-hop tunnel " + Destination.Id32Short + " created." );
            }
        }

        public override IEnumerable<I2PRouterIdentity> TunnelMembers 
        {
            get
            {
                if ( TunnelSetup == null ) return null; 
                return TunnelSetup.Hops.Select( h => (I2PRouterIdentity)h.Peer );
            }
        }

        public override int TunnelEstablishmentTimeoutSeconds 
        { 
            get 
            {
                if ( Fake0HopTunnel ) return 100;
                /*
                if ( Config.Pool == TunnelConfig.TunnelPool.Exploratory ) return ( TunnelSetup.Hops.Count + 1 ) * 3;
                return ( TunnelSetup.Hops.Count + 1 ) * 3; 
               */
                return ( OutTunnelHops + TunnelMemberHops - 1 ) * MeassuredTunnelBuildTimePerHopSeconds; 
            } 
        }

        public override int LifetimeSeconds 
        { 
            get 
            {
                if ( !Fake0HopTunnel && Config.Pool == TunnelConfig.TunnelPool.Exploratory )
                    return TunnelLifetimeSeconds - TunnelRecreationMarginSeconds;
                return TunnelLifetimeSeconds; 
            } 
        }

        public override void Send( TunnelMessage msg )
        {
            throw new NotImplementedException();
        }

        public override void SendRaw( I2NPMessage msg )
        {
            throw new NotImplementedException();
        }

        PeriodicAction FragBufferReport = new PeriodicAction( TickSpan.Seconds( 60 ) );

        public override bool Exectue()
        {
            if ( Terminated || RemoteGateway == null ) return false;

            FragBufferReport.Do( delegate()
            {
                var fbsize = Reassembler.BufferedFragmentCount;
                DebugUtils.Log( "InboundTunnel " + TunnelDebugTrace + ": " + Destination.Id32Short + " Fragment buffer size: " + fbsize.ToString() );
                if ( fbsize > 2000 ) throw new Exception( "BufferedFragmentCount > 2000 !" ); // Trying to fill my memory?
            } );

            return HandleReceiveQueue() && HandleSendQueue();
        }

        private bool HandleReceiveQueue()
        {
            II2NPHeader msg = null;
            List<TunnelDataMessage> tdmsgs = null;

            lock ( ReceiveQueue )
            {
                if ( ReceiveQueue.Count == 0 ) return true;

                if ( ReceiveQueue.Any( mq => mq.MessageType == I2NPMessage.MessageTypes.TunnelData ) )
                {
                    var removelist = ReceiveQueue.Where( mq => mq.MessageType == I2NPMessage.MessageTypes.TunnelData );
                    tdmsgs = removelist.Select( mq => (TunnelDataMessage)mq.Message ).ToList();
                    foreach ( var one in removelist.ToArray() ) ReceiveQueue.Remove( one );
                }
                else
                {
                    msg = ReceiveQueue.Last.Value;
                    ReceiveQueue.RemoveLast();
                }
            }

            if ( tdmsgs != null )
            {
                HandleTunnelData( tdmsgs );
                return true;
            }

            DebugUtils.LogDebug( "InboundTunnel " + TunnelDebugTrace + " HandleReceiveQueue: " + msg.MessageType.ToString() );

            switch ( msg.MessageType )
            {
                case I2NPMessage.MessageTypes.TunnelData:
                    throw new NotImplementedException( "Should not happen " + TunnelDebugTrace );

                case I2NPMessage.MessageTypes.TunnelBuildReply:
                    ThreadPool.QueueUserWorkItem( cb =>
                    {
                        TunnelProvider.Inst.HandleTunnelBuildReply( (II2NPHeader16)msg, (TunnelBuildReplyMessage)msg.Message );
                    } );
                    return true;

                case I2NPMessage.MessageTypes.VariableTunnelBuildReply:
                    ThreadPool.QueueUserWorkItem( cb =>
                    {
                        TunnelProvider.Inst.HandleVariableTunnelBuildReply( (II2NPHeader16)msg, (VariableTunnelBuildReplyMessage)msg.Message );
                    } );
                    return true;

                case I2NPMessage.MessageTypes.DeliveryStatus:
#if LOG_ALL_TUNNEL_TRANSFER
                    DebugUtils.LogDebug( "InboundTunnel " + TunnelDebugTrace + ": DeliveryStatus: " + msg.Message.ToString() );
#endif
                    
                    ThreadPool.QueueUserWorkItem( cb => {
                        lock ( DeliveryStatusReceivedLock ) if ( DeliveryStatusReceived != null ) DeliveryStatusReceived( (DeliveryStatusMessage)msg.Message );
                    } );
                    break;

                case I2NPMessage.MessageTypes.DatabaseStore:
                    var ds = (DatabaseStoreMessage)msg.Message;
                    ThreadPool.QueueUserWorkItem( cb =>
                    {
                        TunnelProvider.HandleDatabaseStore( ds );
                    } );
                    break;

                case I2NPMessage.MessageTypes.Garlic:
#if LOG_ALL_TUNNEL_TRANSFER
                    DebugUtils.Log( "InboundTunnel " + TunnelDebugTrace + ": Garlic: " + msg.Message.ToString() );
#endif

                    ThreadPool.QueueUserWorkItem( cb =>
                    {
                        lock ( GarlicMessageReceivedLock ) if ( GarlicMessageReceived != null ) GarlicMessageReceived( (GarlicMessage)msg.Message );
                    } );
                    break;

                default:
                    DebugUtils.LogWarning( "InboundTunnel " + TunnelDebugTrace + " HandleReceiveQueue: Dropped " + msg.ToString() );
                    break;
            }

            return true;
        }

        TunnelDataFragmentReassembly Reassembler = new TunnelDataFragmentReassembly();

        private void HandleTunnelData( List<TunnelDataMessage> msgs )
        {
            DecryptTunnelMessages( msgs );

            var newmsgs = Reassembler.Process( msgs );
            foreach( var one in newmsgs ) 
            {
                if ( one.GetType() == typeof( TunnelMessageLocal ) )
                {
                    DebugUtils.Log( "InboundTunnel " + TunnelDebugTrace + " TunnelData distributed Local :\r\n" + one.Header.ToString() );
                    MessageReceived( ( (TunnelMessageLocal)one ).Header );
                }
                else
                if ( one.GetType() == typeof( TunnelMessageRouter ) )
                {
                    DebugUtils.Log( "InboundTunnel " + TunnelDebugTrace + " TunnelData distributed Router :\r\n" + one.Header.ToString() );
                    TransportProvider.Send( ( (TunnelMessageRouter)one ).Destination, one.Header.Message );
                }
                else
                if ( one.GetType() == typeof( TunnelMessageTunnel ) )
                {
                    var tone = (TunnelMessageTunnel)one;
                    DebugUtils.Log( "InboundTunnel " + TunnelDebugTrace + " TunnelData distributed Tunnel :\r\n" + one.Header.ToString() );
                    var gwmsg = new TunnelGatewayMessage( tone.Header, tone.Tunnel );
                    TransportProvider.Send( tone.Destination, gwmsg );
                }
                else
                {
                    DebugUtils.LogWarning( "InboundTunnel " + TunnelDebugTrace + " TunnelData without routing rules:\r\n" + one.Header.ToString() );
                }
            }
        }

        private void DecryptTunnelMessages( List<TunnelDataMessage> msgs )
        {
            var cipher = new CbcBlockCipher( new AesEngine() );
            List<TunnelDataMessage> failed = null;

            foreach ( var msg in msgs )
            {
                try
                {
                    for ( int i = TunnelSetup.Hops.Count - 2; i >= 0; --i )
                    {
                        var hop = TunnelSetup.Hops[i];

                        msg.IV.AesEcbDecrypt( hop.IVKey.Key.ToByteArray() );
                        cipher.Decrypt( hop.LayerKey.Key, msg.IV, msg.EncryptedWindow );
                        msg.IV.AesEcbDecrypt( hop.IVKey.Key.ToByteArray() );
                    }

                    // The 0 should be visible now
                    msg.UpdateFirstDeliveryInstructionPosition();
                }
                catch ( Exception ex )
                {
                    DebugUtils.Log( "DecryptTunnelMessages", ex );

                    // Be resiliant to faulty data. Just drop it.
                    if ( failed == null ) failed = new List<TunnelDataMessage>();
                    failed.Add( msg );
                }
            }

            if ( failed != null )
            {
                foreach ( var one in failed ) while ( msgs.Remove( one ) );
            }
        }

        private bool HandleSendQueue()
        {
            lock ( SendQueue )
            {
                if ( SendQueue.Count == 0 ) return true;
            }
            return true;
        }

        public I2NPMessage CreateBuildRequest()
        {
            //TunnelSetup.Hops.Insert( 0, new HopInfo( RouterContext.Inst.MyRouterIdentity ) );

            var vtb = VariableTunnelBuildMessage.BuildInboundTunnel( TunnelSetup );

            //DebugUtils.Log( vtb.ToString() );

            return vtb;
        }

        public override string ToString()
        {
            return base.ToString() + " " + Destination.Id32Short;
        }
    }
}
