using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ET
{


    public sealed class RouterService : AService
    {
        // RouterService创建的时间
        public long StartTime;

        // 当前时间 - RouterService创建的时间, 线程安全
        public uint TimeNow
        {
            get
            {
                return (uint)(TimeHelper.ClientNow() - this.StartTime);
            }
        }

        private Socket socket;


        #region 回调方法
        static RouterService()
        {
            //Kcp.KcpSetLog(KcpLog);
            //Kcp.KcpSetoutput(KcpOutput);
        }

        private static readonly byte[] logBuffer = new byte[1024];


        #endregion


        #region 主线程

        public RouterService(ThreadSynchronizationContext threadSynchronizationContext, IPEndPoint ipEndPoint, ServiceType serviceType)
        {
            this.ServiceType = serviceType;
            this.ThreadSynchronizationContext = threadSynchronizationContext;
            this.StartTime = TimeHelper.ClientNow();
            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                this.socket.SendBufferSize = Kcp.OneM * 64;
                this.socket.ReceiveBufferSize = Kcp.OneM * 64;
            }

            this.socket.Bind(ipEndPoint);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const uint IOC_IN = 0x80000000;
                const uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                this.socket.IOControl((int)SIO_UDP_CONNRESET, new[] { Convert.ToByte(false) }, null);
            }
        }

        public RouterService(ThreadSynchronizationContext threadSynchronizationContext, ServiceType serviceType)
        {
            this.ServiceType = serviceType;
            this.ThreadSynchronizationContext = threadSynchronizationContext;
            this.StartTime = TimeHelper.ClientNow();
            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // 作为客户端不需要修改发送跟接收缓冲区大小
            this.socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const uint IOC_IN = 0x80000000;
                const uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                this.socket.IOControl((int)SIO_UDP_CONNRESET, new[] { Convert.ToByte(false) }, null);
            }
        }


        public void ChangeAddress(long id, IPEndPoint address)
        {
#if NET_THREAD
            this.ThreadSynchronizationContext.Post(() =>
                    {
#endif
            RouterChannel kChannel = this.Get(id);
            if (kChannel == null)
            {
                return;
            }

            Log.Info($"channel change address: {id} {address}");
            kChannel.RemoteAddress = address;
#if NET_THREAD
                    }
        );
#endif
        }

        #endregion

        #region 网络线程
        private readonly Dictionary<long, RouterChannel> idChannels = new Dictionary<long, RouterChannel>();
        private readonly Dictionary<long, RouterChannel> localConnChannels = new Dictionary<long, RouterChannel>();
        private readonly Dictionary<long, RouterChannel> waitConnectChannels = new Dictionary<long, RouterChannel>();
        private readonly List<long> waitRemoveChannels = new List<long>();

        private readonly byte[] cache = new byte[8192];
        private EndPoint ipEndPoint = new IPEndPoint(IPAddress.Any, 0);

        // 网络线程
        private readonly Random random = new Random(Guid.NewGuid().GetHashCode());

        // 下帧要更新的channel
        private readonly HashSet<long> updateChannels = new HashSet<long>();

        // 下次时间更新的channel
        private readonly MultiMap<long, long> timeId = new MultiMap<long, long>();

        private readonly List<long> timeOutTime = new List<long>();

        // 记录最小时间，不用每次都去MultiMap取第一个值
        private long minTime;


        public override bool IsDispose()
        {
            return this.socket == null;
        }

        public override void Dispose()
        {
            foreach (long channelId in this.idChannels.Keys.ToArray())
            {
                this.Remove(channelId);
            }

            this.socket.Close();
            this.socket = null;
        }

        private IPEndPoint CloneAddress()
        {
            IPEndPoint ip = (IPEndPoint)this.ipEndPoint;
            return new IPEndPoint(ip.Address, ip.Port);
        }

        private void Recv()
        {
            if (this.socket == null)
            {
                return;
            }

            while (socket != null && this.socket.Available > 0)
            {
                int messageLength = this.socket.ReceiveFrom(this.cache, ref this.ipEndPoint);

                // 长度小于1，不是正常的消息
                if (messageLength < 1)
                {
                    continue;
                }

                // accept
                byte flag = this.cache[0];

                // conn从100开始，如果为1，2，3则是特殊包
                uint remoteConn = 0;
                uint localConn = 0;
                ulong localRmoteConn = 0;
                try
                {
                    RouterChannel kChannel = null;
                    switch (flag)
                    {
#if NOT_CLIENT
                        case KcpProtocalType.SYN: // accept
                            {
                                // 长度!=5，不是SYN消息
                                if (messageLength < 9)
                                {
                                    break;
                                }

                                string realAddress = null;
                                remoteConn = BitConverter.ToUInt32(this.cache, 1);
                                if (messageLength > 9)
                                {
                                    realAddress = this.cache.ToStr(9, messageLength - 9);
                                }

                                remoteConn = BitConverter.ToUInt32(this.cache, 1);
                                //localConn = BitConverter.ToUInt32(this.cache, 5);

                                this.waitConnectChannels.TryGetValue(remoteConn, out kChannel);
                                if (kChannel == null)
                                {
                                    long id = this.CreateAcceptChannelId(remoteConn);
                                    if (this.idChannels.ContainsKey(id))
                                    {
                                        break;
                                    }
                                    kChannel = new RouterChannel(id, remoteConn, this.socket, realAddress, this.CloneAddress(), this);
                                    kChannel.RouterConnect();
                                    this.idChannels.Add(kChannel.Id, kChannel);
                                    this.waitConnectChannels.Add(remoteConn, kChannel); // 连接上了或者超时后会删除
                                    this.localConnChannels.Add(kChannel.LocalConn, kChannel);

                                }

                                break;
                            }
                        case KcpProtocalType.RouterReconnect:
                            // 长度!=5，不是SYN消息
                            if (messageLength < 9)
                            {
                                break;
                            }

                            string RerealAddress = null;
                            remoteConn = BitConverter.ToUInt32(this.cache, 1);
                            if (messageLength > 9)
                            {
                                RerealAddress = this.cache.ToStr(9, messageLength - 9);
                            }

                            remoteConn = BitConverter.ToUInt32(this.cache, 1);
                            //localConn = BitConverter.ToUInt32(this.cache, 5);
                            this.waitConnectChannels.TryGetValue(remoteConn, out kChannel);
                            if (kChannel == null)
                            {
                                //localConn = CreateRandomLocalConn(this.random);
                                //已存在同样的localConn，则不处理，等待下次sync
                                if (this.localConnChannels.ContainsKey(remoteConn))
                                {
                                    break;
                                }
                                long id = this.CreateAcceptChannelId(remoteConn);
                                if (this.idChannels.ContainsKey(id))
                                {
                                    break;
                                }
                                kChannel = new RouterChannel(id, remoteConn, this.socket, RerealAddress, this.CloneAddress(), this);
                                this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, NetworkHelper.ToIPEndPoint(kChannel.RealAddress));
                                this.idChannels.Add(kChannel.Id, kChannel);
                                this.waitConnectChannels.Add(remoteConn, kChannel); // 连接上了或者超时后会删除
                                this.localConnChannels.Add(kChannel.LocalConn, kChannel);

                            }
                            break;
#endif
                        case KcpProtocalType.ACK: // connect返回
                            // 长度!=9，不是connect消息
                            if (messageLength != 9)
                            {
                                break;
                            }
                            remoteConn = BitConverter.ToUInt32(this.cache, 1);
                            localConn = BitConverter.ToUInt32(this.cache, 5);
                            kChannel = this.GetByLocalConn(localConn);

                            //代表gate连接成功
                            if (kChannel != null)
                            {
                                kChannel.RemoteConn = remoteConn;
                                kChannel.HandleConnnect();
                                //返回给客户端
                                this.socket.SendTo(this.cache, 0, 9, SocketFlags.None, kChannel.RemoteAddress);
                            }

                            break;
                        case KcpProtocalType.FIN: // 断开
                            // 长度!=13，不是DisConnect消息
                            if (messageLength != 13)
                            {
                                break;
                            }

                            remoteConn = BitConverter.ToUInt32(this.cache, 1);
                            localConn = BitConverter.ToUInt32(this.cache, 5);
                            int error = BitConverter.ToInt32(this.cache, 9);
                            localRmoteConn = ((ulong)remoteConn << 32) | localConn;
                            //直接取出的说明是发往客户端的
                            if (RouterChannel.LocalRemoteConnId.TryGetValue(localRmoteConn, out kChannel))
                            {
                                this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, kChannel.RemoteAddress);
                                kChannel.Dispose();
                                break;
                            }
                            localRmoteConn = ((ulong)localConn << 32) | remoteConn;
                            //如果这样取到说明实际应该发往真正地址
                            if (RouterChannel.LocalRemoteConnId.TryGetValue(localRmoteConn, out kChannel))
                            {
                                this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, NetworkHelper.ToIPEndPoint(kChannel.RealAddress));
                                kChannel.Dispose();
                                break;
                            }
                            
                            break;
                        case KcpProtocalType.MSG: // 消息要寻找.首先找到localRmoteConn 找到了说明是服务端发往客户端的,否则反过来尝试寻找
                            // 长度<9，不是Msg消息
                            if (messageLength < 9)
                            {
                                break;
                            }
                            // 处理chanel
                            remoteConn = BitConverter.ToUInt32(this.cache, 1);
                            localConn = BitConverter.ToUInt32(this.cache, 5);
                            
                            localRmoteConn = ((ulong)remoteConn << 32) | localConn;
                            //直接取出的说明是发往客户端的
                            if (RouterChannel.LocalRemoteConnId.TryGetValue(localRmoteConn, out kChannel))
                            {
                                this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, kChannel.RemoteAddress);
                                break;
                            }
                            localRmoteConn=((ulong)localConn << 32) | remoteConn;
                            //如果这样取到说明实际应该发往真正地址
                            if (RouterChannel.LocalRemoteConnId.TryGetValue(localRmoteConn, out kChannel))
                            {
                                this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, NetworkHelper.ToIPEndPoint(kChannel.RealAddress));
                                break;
                            }
                            else
                            {
                                Log.Debug("KcpProtocalType.MSG double miss");
                            }
                            //kChannel = this.GetByLocalConn(localConn);
                            //if (kChannel != null)
                            //{
                            //    // 校验remoteConn，防止第三方攻击
                            //    if (kChannel.RemoteConn != remoteConn)
                            //    {
                            //        break;
                            //    }
                            //    this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, kChannel.RemoteAddress);
                            //    break;
                            //    // 通知对方断开
                            //    //this.Disconnect(localConn, remoteConn, ErrorCode.ERR_KcpNotFoundChannel, (IPEndPoint)this.ipEndPoint, 1);
                            //}
                            ////可能是客户端发给服务器

                            //kChannel = this.GetByLocalConn(remoteConn);
                            //if (kChannel==null)
                            //{
                            //    //两个conn检测都失败
                            //    Log.Debug("KcpProtocalType.MSG double miss");
                            //    break;
                            //}
                            //// 校验remoteConn，防止第三方攻击
                            //if (kChannel.RemoteConn!=localConn)
                            //{
                            //    break;
                            //}
                            //this.socket.SendTo(this.cache, 0, messageLength, SocketFlags.None, NetworkHelper.ToIPEndPoint(kChannel.RealAddress));
                            break;
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"RouterService error: {flag} {remoteConn} {localConn}\n{e}");
                }
            }
        }

        public RouterChannel Get(long id)
        {
            RouterChannel channel;
            this.idChannels.TryGetValue(id, out channel);
            return channel;
        }

        public RouterChannel GetByLocalConn(uint localConn)
        {
            RouterChannel channel;
            this.localConnChannels.TryGetValue(localConn, out channel);
            return channel;
        }

        public override void Remove(long id)
        {
            if (!this.idChannels.TryGetValue(id, out RouterChannel kChannel))
            {
                return;
            }
            Log.Info($"RouterService remove channel: {id} {kChannel.LocalConn} {kChannel.RemoteConn}");
            this.idChannels.Remove(id);
            this.localConnChannels.Remove(kChannel.LocalConn);
            if (this.waitConnectChannels.TryGetValue(kChannel.RemoteConn, out RouterChannel waitChannel))
            {
                if (waitChannel.LocalConn == kChannel.LocalConn)
                {
                    this.waitConnectChannels.Remove(kChannel.RemoteConn);
                }
            }
            kChannel.Dispose();
        }

        private void Disconnect(uint localConn, uint remoteConn, int error, IPEndPoint address, int times)
        {
            try
            {
                if (this.socket == null)
                {
                    return;
                }

                byte[] buffer = this.cache;
                buffer.WriteTo(0, KcpProtocalType.FIN);
                buffer.WriteTo(1, localConn);
                buffer.WriteTo(5, remoteConn);
                buffer.WriteTo(9, (uint)error);
                for (int i = 0; i < times; ++i)
                {
                    this.socket.SendTo(buffer, 0, 13, SocketFlags.None, address);
                }
            }
            catch (Exception e)
            {
                Log.Error($"Disconnect error {localConn} {remoteConn} {error} {address} {e}");
            }

            Log.Info($"channel send fin: {localConn} {remoteConn} {address} {error}");
        }

        protected override void Send(long channelId, long actorId, MemoryStream stream)
        {
            //RouterChannel channel = this.Get(channelId);
            //if (channel == null)
            //{
            //    return;
            //}
            //channel.Send(actorId, stream);
        }

        // 服务端需要看channel的update时间是否已到
        public void AddToUpdateNextTime(long time, long id)
        {
            if (time == 0)
            {
                this.updateChannels.Add(id);
                return;
            }
            if (time < this.minTime)
            {
                this.minTime = time;
            }
            this.timeId.Add(time, id);
        }

        public override void Update()
        {
            this.Recv();

            this.TimerOut();

            foreach (long id in updateChannels)
            {
                RouterChannel kChannel = this.Get(id);
                if (kChannel == null)
                {
                    continue;
                }

                if (kChannel.Id == 0)
                {
                    continue;
                }

                kChannel.Update();
            }

            this.updateChannels.Clear();

            this.RemoveConnectTimeoutChannels();
        }

        private void RemoveConnectTimeoutChannels()
        {
            this.waitRemoveChannels.Clear();

            foreach (long channelId in this.waitConnectChannels.Keys)
            {
                this.waitConnectChannels.TryGetValue(channelId, out RouterChannel kChannel);
                if (kChannel == null)
                {
                    Log.Error($"RemoveConnectTimeoutChannels not found kchannel: {channelId}");
                    continue;
                }

                // 连接上了要马上删除
                if (kChannel.IsConnected)
                {
                    this.waitRemoveChannels.Add(channelId);
                }

                // 10秒连接超时
                if (this.TimeNow > kChannel.CreateTime + 10 * 1000)
                {
                    this.waitRemoveChannels.Add(channelId);
                }
            }

            foreach (long channelId in this.waitRemoveChannels)
            {
                this.waitConnectChannels.Remove(channelId);
            }
        }

        // 计算到期需要update的channel
        private void TimerOut()
        {
            if (this.timeId.Count == 0)
            {
                return;
            }

            uint timeNow = this.TimeNow;

            if (timeNow < this.minTime)
            {
                return;
            }

            this.timeOutTime.Clear();

            foreach (KeyValuePair<long, List<long>> kv in this.timeId)
            {
                long k = kv.Key;
                if (k > timeNow)
                {
                    minTime = k;
                    break;
                }

                this.timeOutTime.Add(k);
            }

            foreach (long k in this.timeOutTime)
            {
                foreach (long v in this.timeId[k])
                {
                    this.updateChannels.Add(v);
                }

                this.timeId.Remove(k);
            }
        }

        protected override void Get(long id, IPEndPoint address)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}