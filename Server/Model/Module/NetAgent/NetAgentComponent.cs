using System.Collections.Generic;

namespace ET
{
	public class NetAgentComponent : Entity
	{
		public RouterService Service;

		public IMessageDispatcher MessageDispatcher { get; set; }
	}
}