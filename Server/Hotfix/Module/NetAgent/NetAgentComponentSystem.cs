
using System;
using System.IO;
using System.Net;

namespace ET
{

	[ObjectSystem]
	public class NetAgentComponentAwake1System : AwakeSystem<NetAgentComponent, IPEndPoint>
	{
		public override void Awake(NetAgentComponent self, IPEndPoint address)
		{
            self.Service = new RouterService(NetThreadComponent.Instance.ThreadSynchronizationContext, address, ServiceType.Outer);
            self.Service.ErrorCallback += self.OnError;
            self.Service.ReadCallback += self.OnRead;
            self.Service.AcceptCallback += self.OnAccept;

            NetThreadComponent.Instance.Add(self.Service);
		}
	}
	
	[ObjectSystem]
	public class NetAgentComponentLoadSystem : LoadSystem<NetAgentComponent>
	{
		public override void Load(NetAgentComponent self)
		{
		}
	}
	
    public static class NetAgentComponentSystem
    {
        public static void OnRead(this NetAgentComponent self, long channelId, MemoryStream memoryStream)
        {
            
        }

        public static void OnError(this NetAgentComponent self, long channelId, int error)
        {
            Session session = self.GetChild<Session>(channelId);
            if (session == null)
            {
                return;
            }

            session.Error = error;
            session.Dispose();
        }

        // 这个channelId是由CreateAcceptChannelId生成的
        //同时ipEndPoint是真正要连接的gate的地址
        public static void OnAccept(this NetAgentComponent self, long channelId, IPEndPoint ipEndPoint)
        {
            Session session = EntityFactory.CreateWithParentAndId<Session, AService>(self, channelId, self.Service);
            session.RemoteAddress = ipEndPoint;

            // 客户端连接，2秒检查一次recv消息，10秒没有消息则断开
            session.AddComponent<SessionIdleCheckerComponent, int>(NetThreadComponent.checkInteral);
        }

        public static Session Get(this NetAgentComponent self, long id)
        {
            Session session = self.GetChild<Session>(id);
            return session;
        }


    }
}