using System.Collections.Generic;

namespace ET

{

    [ComponentOf(typeof(Scene))]

    [ChildType(typeof(Session))]

    public class NetWSComponent : Entity, IAwake<int>, IAwake<IEnumerable<string>, int>, IDestroy

    {

        public AService Service;

        public int SessionStreamDispatcherType { get; set; }

    }

}