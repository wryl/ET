using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ET
{

	public class RouterChannel : AChannel
	{
		public RouterService Service;

		// 保存所有的channel
		public static readonly Dictionary<uint, RouterChannel> routerChannels = new Dictionary<uint, RouterChannel>();

		public static readonly ConcurrentDictionary<long, ulong> idLocalRemoteConn = new ConcurrentDictionary<long, ulong>();
		public static readonly ConcurrentDictionary<ulong, RouterChannel> LocalRemoteConnId = new ConcurrentDictionary<ulong, RouterChannel>();


		private Socket socket;

		private uint lastRecvTime;
		public IPEndPoint TargetAddress { get; set; }
		public readonly uint CreateTime;

		public uint LocalConn { get; set; }
		public uint RemoteConn { get; set; }
		public int SceneId { get; set; }
		private readonly byte[] sendCache = new byte[1024 * 1024];

		public bool IsConnected { get; private set; }

		public string RealAddress { get; set; }


		// connect
		public RouterChannel(long id, uint localConn, Socket socket,string realAddress, IPEndPoint remoteEndPoint, RouterService kService)
		{
			this.LocalConn = localConn;
			if (routerChannels.ContainsKey(this.LocalConn))
			{
				throw new Exception($"channel create error: {this.LocalConn} {remoteEndPoint} {this.ChannelType}");
			}

			this.Id = id;
			this.ChannelType = ChannelType.Connect;

			Log.Info($"channel create: {this.Id} {this.LocalConn} {remoteEndPoint} {this.ChannelType}");

			this.Service = kService;
			this.RemoteAddress = remoteEndPoint;
			this.RealAddress = realAddress;
			this.socket = socket;

			routerChannels.Add(this.LocalConn, this);

			this.lastRecvTime = kService.TimeNow;
			this.CreateTime = kService.TimeNow;

			

		}

		// accept
		public RouterChannel(long id, uint localConn, uint remoteConn, Socket socket, IPEndPoint remoteEndPoint, RouterService kService)
		{
			if (routerChannels.ContainsKey(this.LocalConn))
			{
				throw new Exception($"channel create error: {localConn} {remoteEndPoint} {this.ChannelType}");
			}

			this.Id = id;
			this.ChannelType = ChannelType.Accept;

			Log.Info($"channel create: {this.Id} {localConn} {remoteConn} {remoteEndPoint} {this.ChannelType}");

			this.Service = kService;
			this.LocalConn = localConn;
			this.RemoteConn = remoteConn;
			this.RemoteAddress = remoteEndPoint;
			this.socket = socket;

			routerChannels.Add(this.LocalConn, this);

			this.lastRecvTime = kService.TimeNow;
			this.CreateTime = kService.TimeNow;

		}



		#region 网络线程




		public override void Dispose()
		{
			if (this.IsDisposed)
			{
				return;
			}

			uint localConn = this.LocalConn;
			uint remoteConn = this.RemoteConn;
			Log.Info($"channel dispose: {this.Id} {localConn} {remoteConn}");

			routerChannels.Remove(localConn);
			idLocalRemoteConn.TryRemove(this.Id, out ulong nextid);
			LocalRemoteConnId.TryRemove(nextid, out var _);
			long id = this.Id;
			this.Id = 0;
			this.Service.Remove(id);

			try
			{
				//this.Service.Disconnect(localConn, remoteConn, this.Error, this.RemoteAddress, 3);
			}

			catch (Exception e)
			{
				Log.Error(e);
			}

			this.socket = null;
		}

		public void HandleConnnect()
		{
			// 如果连接上了就不用处理了
			if (this.IsConnected)
			{
				return;
			}
			ulong localRmoteConn = ((ulong)this.RemoteConn << 32) | this.LocalConn;
			idLocalRemoteConn.TryAdd(this.Id, localRmoteConn);
			LocalRemoteConnId.TryAdd(localRmoteConn, this);
			Log.Info($"channel connected: {this.Id} {this.LocalConn} {this.RemoteConn} {this.RemoteAddress}");
			this.IsConnected = true;
			this.lastRecvTime = this.Service.TimeNow;

			
		}

		public void RouterConnect()
		{
			try
			{
				uint timeNow = this.Service.TimeNow;

				this.lastRecvTime = timeNow;

				byte[] buffer = sendCache;
				buffer.WriteTo(0, KcpProtocalType.SYN);
				buffer.WriteTo(1, this.LocalConn);
				buffer.WriteTo(5, this.RemoteConn);
				this.socket.SendTo(buffer, 0, 9, SocketFlags.None, NetworkHelper.ToIPEndPoint(this.RealAddress));
				Log.Info($"RouterChannel connect {this.Id} {this.LocalConn} {this.RemoteConn} {this.RealAddress} {this.socket.LocalEndPoint}");
				// 200毫秒后再次update发送connect请求
				this.Service.AddToUpdateNextTime(timeNow + 300, this.Id);
			}
			catch (Exception e)
			{
				Log.Error(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}
		public void Update()
		{
			if (this.IsDisposed)
			{
				return;
			}

			uint timeNow = this.Service.TimeNow;

			// 如果还没连接上，发送连接请求
			if (!this.IsConnected)
			{
				// 20秒没连接上则报错
				if (timeNow - this.CreateTime > 10 * 1000)
				{
					Log.Error($"RouterChannel connect timeout: {this.Id} {this.RemoteConn} {timeNow} {this.CreateTime} {this.ChannelType} {this.RemoteAddress}");
					this.OnError(ErrorCode.ERR_KcpConnectTimeout);
					return;
				}

				switch (ChannelType)
				{
					case ChannelType.Connect:
						this.RouterConnect();
						break;
				}
				return;
			}
		}
		public void OnError(int error)
		{
			long channelId = this.Id;
			this.Service.Remove(channelId);
			this.Service.OnError(channelId, error);
		}

		#endregion
	}
}
