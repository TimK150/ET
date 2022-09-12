using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace ET
{
    public class WChannel : AChannel
    {
        public HttpListenerWebSocketContext WebSocketContext { get; }

        private readonly WService Service;

        private readonly WebSocket webSocket;

        private readonly Queue<byte[]> queue = new Queue<byte[]>();

        private bool isSending;

        private bool isConnected;

        private readonly MemoryStream recvStream;

        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        private readonly byte[] sendCache = new byte[Packet.OpcodeLength + Packet.ActorIdLength];

        private readonly CircularBuffer sendBuffer = new CircularBuffer();

        public WChannel(long id, HttpListenerWebSocketContext webSocketContext, WService service)
        {
            this.Id = id;
            this.Service = service;
            this.ChannelType = ChannelType.Accept;
            this.WebSocketContext = webSocketContext;
            this.webSocket = webSocketContext.WebSocket;
            this.recvStream = new MemoryStream(ushort.MaxValue);

            isConnected = true;

            this.Service.ThreadSynchronizationContext.PostNext(() =>
            {
                this.StartRecv().Coroutine();
                this.StartSend().Coroutine();
            });
        }

        public WChannel(long id, WebSocket webSocket, string connectUrl, WService service)
        {
            this.Id = id;
            this.Service = service;
            this.ChannelType = ChannelType.Connect;
            this.webSocket = webSocket;
            this.recvStream = new MemoryStream(ushort.MaxValue);

            isConnected = false;

            this.Service.ThreadSynchronizationContext.PostNext(() => this.ConnectAsync(connectUrl).Coroutine());
        }

        public override void Dispose()
        {
            if (this.IsDisposed)
            {
                return;
            }

            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();
            this.cancellationTokenSource = null;

            this.webSocket.Dispose();
        }

        public async ETTask ConnectAsync(string url)
        {
            try
            {
                await ((ClientWebSocket)this.webSocket).ConnectAsync(new Uri(url), cancellationTokenSource.Token);
                isConnected = true;

                this.StartRecv().Coroutine();
                this.StartSend().Coroutine();
            }
            catch (Exception e)
            {
                Log.Error(e);
                this.OnError(ErrorCore.ERR_WebsocketConnectError);
            }
        }

        public void Send(MemoryStream stream)
        {
            byte[] bytes = new byte[stream.Length - stream.Position];
            Array.Copy(stream.GetBuffer(), stream.Position, bytes, 0, bytes.Length);

            //stream.Seek(Packet.ActorIdLength, SeekOrigin.Begin); // 外网不需要actorId

            //ushort messageSize = (ushort)(stream.Length - stream.Position);
            //this.sendCache.WriteTo(0, messageSize);
            //this.sendBuffer.Write(this.sendCache, 0, PacketParser.OuterPacketSizeLength);
            //this.sendBuffer.Write(stream.GetBuffer(), (int)stream.Position, (int)(stream.Length - stream.Position));

            //byte[] bytes = new byte[sendBuffer.Length];

            ////Array.Copy(stream.GetBuffer(), stream.Position, bytes, PacketParser.OuterPacketSizeLength, bytes.Length - PacketParser.OuterPacketSizeLength);
            //Array.Copy(sendBuffer.Last, sendBuffer.Position, bytes, 0, bytes.Length);

            this.queue.Enqueue(bytes);

            if (this.isConnected)
            {
                this.StartSend().Coroutine();
            }
        }

        public async ETTask StartSend()
        {
            if (this.IsDisposed)
            {
                return;
            }

            try
            {
                if (this.isSending)
                {
                    return;
                }

                this.isSending = true;

                while (true)
                {
                    if (this.queue.Count == 0)
                    {
                        this.isSending = false;
                        return;
                    }

                    byte[] bytes = this.queue.Dequeue();
                    try
                    {
                        //await this.webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length), WebSocketMessageType.Binary, true, cancellationTokenSource.Token);

                        // ms
                        int c = 0;
                        foreach (var b in bytes)
                        {
                            if (b != 0)
                            {
                                c++;
                            }
                        }
                        byte[] bs = new byte[c];
                        for (int i = 8; i < bytes.Length; i++)
                        {
                            bs[i - 8] = bytes[i];
                        }

                        await this.webSocket.SendAsync(new ArraySegment<byte>(bs, 0, bs.Length), WebSocketMessageType.Binary, true, cancellationTokenSource.Token);
                        System.Threading.Thread.Sleep(1000);
                        if (this.IsDisposed)
                        {
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                        this.OnError(ErrorCore.ERR_WebsocketSendError);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private byte[] cache = new byte[ushort.MaxValue];

        public async ETTask StartRecv()
        {
            if (this.IsDisposed)
            {
                return;
            }

            try
            {
                while (true)
                {
                    //#if NOT_UNITY
                    //                    ValueWebSocketReceiveResult receiveResult;
                    //#else
                    WebSocketReceiveResult receiveResult;
                    //#endif
                    int receiveCount = 0;
                    do
                    {
                        //#if NOT_UNITY
                        //                      receiveResult = await this.webSocket.ReceiveAsync(
                        //                            new Memory<byte>(cache, receiveCount, this.cache.Length - receiveCount),
                        //                            cancellationTokenSource.Token);
                        //#else
                        receiveResult = await this.webSocket.ReceiveAsync(
                            new ArraySegment<byte>(this.cache, receiveCount, this.cache.Length - receiveCount),
                            cancellationTokenSource.Token);
                        //#endif
                        if (this.IsDisposed)
                        {
                            return;
                        }

                        receiveCount += receiveResult.Count;
                    }
                    while (!receiveResult.EndOfMessage);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        this.OnError(ErrorCore.ERR_WebsocketPeerReset);
                        return;
                    }

                    if (receiveResult.Count > ushort.MaxValue)
                    {
                        await this.webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"message too big: {receiveCount}",
                            cancellationTokenSource.Token);
                        this.OnError(ErrorCore.ERR_WebsocketMessageTooBig);
                        return;
                    }

                    this.recvStream.SetLength(receiveCount);
                    this.recvStream.Seek(2, SeekOrigin.Begin);
                    Array.Copy(this.cache, 0, this.recvStream.GetBuffer(), 0, receiveCount);
                    Console.WriteLine($"recvStream.Length:{recvStream.Length}");
                    this.OnRead(this.recvStream);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
                this.OnError(ErrorCore.ERR_WebsocketRecvError);
            }
        }

        private void OnRead(MemoryStream memoryStream)
        {
            try
            {
                long channelId = this.Id;
                this.Service.OnRead(channelId, memoryStream);
            }
            catch (Exception e)
            {
                Log.Error($"{this.RemoteAddress} {memoryStream.Length} {e}");
                // 出现任何消息解析异常都要断开Session，防止客户端伪造消息
                this.OnError(ErrorCore.ERR_PacketParserError);
            }
        }

        private void OnError(int error)
        {
            Log.Debug($"WChannel error: {error} {this.RemoteAddress}");

            long channelId = this.Id;

            this.Service.Remove(channelId);

            this.Service.OnError(channelId, error);
        }
    }
}