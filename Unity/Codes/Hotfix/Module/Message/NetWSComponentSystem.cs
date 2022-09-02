using System;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Net;

using System.Text;

using System.Threading.Tasks;

namespace ET

{

    [FriendClass(typeof(NetThreadComponent))]

    [ObjectSystem]

    public class NetWSComponentAwakeSystem : AwakeSystem<NetWSComponent, int>

    {

        public override void Awake(NetWSComponent self, int sessionStreamDispatcherType)

        {

            self.SessionStreamDispatcherType = sessionStreamDispatcherType;

            self.Service = new WService(NetThreadComponent.Instance.ThreadSynchronizationContext);

            self.Service.ErrorCallback += (channelId, error) => self.OnError(channelId, error);

            self.Service.ReadCallback += (channelId, Memory) => self.OnRead(channelId, Memory);

            NetThreadComponent.Instance.Add(self.Service);

        }

    }

    [FriendClass(typeof(NetThreadComponent))]

    [ObjectSystem]

    public class NetWSComponentAwake1System : AwakeSystem<NetWSComponent, IEnumerable<string>, int>

    {
        public int i { get; set; }

        public override void Awake(NetWSComponent self, IEnumerable<string> prefixs, int sessionStreamDispatcherType)

        {
            var prefixList = prefixs.ToList();
            Console.WriteLine($"[WS] {this.GetType().ToString()}_address: {prefixList[i]}");
            i = i++;

            self.SessionStreamDispatcherType = sessionStreamDispatcherType;

            self.Service = new WService(NetThreadComponent.Instance.ThreadSynchronizationContext, prefixs);

            self.Service.ErrorCallback += (channelId, error) => self.OnError(channelId, error);

            self.Service.ReadCallback += (channelId, Memory) => self.OnRead(channelId, Memory);

            self.Service.AcceptCallback += (channelId, IPAddress) => self.OnAccept(channelId, IPAddress);

            NetThreadComponent.Instance.Add(self.Service);

        }

    }

    [ObjectSystem]

    public class NetWSComponentDestroySystem : DestroySystem<NetWSComponent>

    {

        public override void Destroy(NetWSComponent self)

        {

            NetThreadComponent.Instance.Remove(self.Service);

            self.Service.Destroy();

        }

    }

    [FriendClass(typeof(NetWSComponent))]

    public static class NetWSComponentSystem

    {

        public static void OnRead(this NetWSComponent self, long channelId, MemoryStream memoryStream)

        {

            Session session = self.GetChild<Session>(channelId);

            if (session == null)

            {

                return; 

            }

            session.LastRecvTime = TimeHelper.ClientNow();

            SessionStreamDispatcher.Instance.Dispatch(self.SessionStreamDispatcherType, session, memoryStream);

        }

        public static void OnError(this NetWSComponent self, long channelId, int error)

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

        public static void OnAccept(this NetWSComponent self, long channelId, IPEndPoint ipEndPoint)

        {

            Session session = self.AddChildWithId<Session, AService>(channelId, self.Service);

            session.RemoteAddress = ipEndPoint;

            // 挂上这个组件，5秒就会删除session，所以客户端验证完成要删除这个组件。该组件的作用就是防止外挂一直连接不发消息也不进行权限验证

            session.AddComponent<SessionAcceptTimeoutComponent>();

            // 客户端连接，2秒检查一次recv消息，10秒没有消息则断开

            session.AddComponent<SessionIdleCheckerComponent, int>(NetThreadComponent.checkInteral);

        }

        public static Session Get(this NetWSComponent self, long id)

        {

            Session session = self.GetChild<Session>(id);

            return session;

        }

        public static Session Create(this NetWSComponent self, IPEndPoint realIPEndPoint)

        {

            long channelId = RandomHelper.RandInt64();

            Session session = self.AddChildWithId<Session, AService>(channelId, self.Service);

            session.RemoteAddress = realIPEndPoint;

            session.AddComponent<SessionIdleCheckerComponent, int>(NetThreadComponent.checkInteral);

            self.Service.GetOrCreate(session.Id, realIPEndPoint);

            return session;

        }

    }

}