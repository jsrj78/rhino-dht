using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.DistributedHashTable.Commands;
using Rhino.DistributedHashTable.Parameters;
using Rhino.DistributedHashTable.Remote;
using Rhino.DistributedHashTable.Util;
using Rhino.Queues;

namespace Rhino.DistributedHashTable.Internal
{
	public class DistributedHashTableNode : IDistributedHashTableNode
	{
		private readonly NodeEndpoint endPoint;
		private readonly IExecuter executer;
		private readonly IDistributedHashTableMaster master;
		private readonly IMessageSerializer messageSerializer;
		private readonly IQueueManager queueManager;
		private readonly IDistributedHashTableNodeReplicationFactory replicationFactory;
		private IList<Segment> ranges = new List<Segment>();
		private IList<Segment> rangesThatWeAreCatchingUpOn = new List<Segment>();

		public DistributedHashTableNode(IDistributedHashTableMaster master,
		                                IExecuter executer,
		                                IMessageSerializer messageSerializer,
		                                NodeEndpoint endPoint,
		                                IQueueManager queueManager,
										IDistributedHashTableNodeReplicationFactory replicationFactory)
		{
			this.master = master;
			this.executer = executer;
			this.messageSerializer = messageSerializer;
			this.endPoint = endPoint;
			this.queueManager = queueManager;
			this.replicationFactory = replicationFactory;
			State = NodeState.NotStarted;
		}

		public NodeState State { get; set; }

		public Topology Topology { get; set; }

		public NodeEndpoint Endpoint
		{
			get { return endPoint; }
		}

		public Guid GetTopologyVersion()
		{
			return Topology.Version;
		}

		public bool IsSegmentOwned(int range)
		{
			return Topology.IsOwnedBy(endPoint, range);
		}

		public void SendToOwner(int range,
		                        IExtendedRequest[] requests)
		{
			var ownerSegment = Topology.GetSegment(range);
			queueManager.Send(ownerSegment.AssignedEndpoint.Async,
			                  new MessagePayload
			                  {
			                  	Data = messageSerializer.Serialize(requests),
			                  });
		}

		public void SendToAllOtherBackups(int range,
		                                  IExtendedRequest[] requests)
		{
			var ownerSegment = Topology.GetSegment(range);
			foreach (var endpoint in ownerSegment.Backups.Append(ownerSegment.AssignedEndpoint).Where(x => x != endPoint))
			{
				queueManager.Send(endpoint.Async,
				                  new MessagePayload
				                  {
				                  	Data = messageSerializer.Serialize(requests),
				                  });
			}
		}

		public void DoneReplicatingSegments(int[] replicatedSegments)
		{
			master.CaughtUp(endPoint, replicatedSegments);
			rangesThatWeAreCatchingUpOn.MoveTo(ranges, x => replicatedSegments.Contains(x.Index));
			State = NodeState.Started;
		}

		public void GivingUpOn(params int[] rangesGivingUpOn)
		{
			master.GaveUp(endPoint, rangesGivingUpOn);
			rangesThatWeAreCatchingUpOn.RemoveAll(x => rangesGivingUpOn.Contains(x.Index));
		}

		public IDistributedHashTableStorage Storage { get; set; }

		public void Start()
		{
			Topology = master.GetTopology();
			var assignedSegments = master.Join(endPoint);
			rangesThatWeAreCatchingUpOn = assignedSegments
				.Where(x => x.AssignedEndpoint != endPoint)
				.ToList();
			foreach (var rangeToReplicate in rangesThatWeAreCatchingUpOn.GroupBy(x => x.AssignedEndpoint))
			{
				executer.RegisterForExecution(
					new OnlineSegmentReplicationCommand(
						rangeToReplicate.Key,
						rangeToReplicate.ToArray(), 
						this,
						replicationFactory.Create(endPoint))
					);
			}
			ranges = assignedSegments.Where(x => x.AssignedEndpoint == endPoint).ToList();
			State =
				ranges.Count > 0
					?
						NodeState.Started
					:
						NodeState.Starting;
		}
	}
}