/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005-2006  University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;
using System.Collections;
using System.Diagnostics;

namespace Brunet {
  public class NodeRankComparer : System.Collections.IComparer {
    public int Compare(object x, object y) {
      if( x == y ) {
        //This is trivial, but we need to deal with it:
        return 0;
      }
      NodeRankInformation x1 = (NodeRankInformation) x;
      NodeRankInformation y1 = (NodeRankInformation) y;
      if (x1.Equals(y1) && x1.Count == y1.Count) {
        /*
         * Since each Address is in our list at most once,
         * this is an Error, so lets print it out and hope
         * someone sees it.
         */
        Console.Error.WriteLine("NodeRankComparer.Comparer: Equality: {0} == {1}", x1, y1);
	return 0;
      }
      if (x1.Count <= y1.Count) {
	return 1;
      }
      if (x1.Count > y1.Count) {
	return -1;
      }
      return -1;
    }
  }
  public class NodeRankInformation { 
    //address of the node
    private Address _addr;
    //rank - score is a better name though
    private int _count;

     
    
    //when was the last retry made
    private DateTime _last_retry = DateTime.MinValue;

    //constructor
    public NodeRankInformation(Address addr) {
      _addr = addr;
      _count = 0;
    }
    public int Count {
      get {
	return _count;
      }
      set {
	_count = value;
      }
    }
    public DateTime LastRetryInstant {
      get {
        return _last_retry;
      }
      set {
       _last_retry = value; 
      }
    }
    public Address Addr {
      get {
	return _addr;
      }
    }

    override public bool Equals(Object other ) {
      NodeRankInformation other1 = (NodeRankInformation) other;
      if (_addr.Equals(other1.Addr)) {
	//Console.WriteLine("equal ranks");
	return true;
      }
      //Console.WriteLine("ranks not equal");
      return false;
    }
    override public int GetHashCode() {
      return _addr.GetHashCode();
    }
    override public string ToString() {
      return _addr.ToString() + ": " + _count + ": " + _last_retry;
    }
  }

  /** The following is what we call a ChotaConnectionOverlord.
   *  This provides high-performance routing by setting up direct
   *  structured connections between pairs of highly communicating nodes.
   *  Chota - in Hindi means small. 
   */
  public class ChotaConnectionOverlord : ConnectionOverlord, IAHPacketHandler {
    //the node we are attached to
    protected Node _node;

    //used for locking
    protected object _sync;
    //our random number generator
    protected Random _rand;

    //if the overlord is active
    protected bool _active;
    
    //minimum score before we start forming chota connections
    private static readonly int MIN_SCORE_THRESHOLD = 5;

    //the maximum number of Chota connections we plan to support
    private static readonly int max_chota = 200;
    
    //maximum number of entries in the node_rank table
    private static readonly int node_rank_capacity = max_chota;

    //retry interval for Chota connections
    //private static readonly double _retry_delay = 5.0;

    
    //hashtable of destinations. for each destination we maintain 
    //how frequently we communicate with it. Just like the LRU in virtual
    // memory context - Arijit Ganguly. 
    protected ArrayList node_rank_list;
    /*
     * Allows us to quickly look up the node rank for a destination
     */
    protected Hashtable _dest_to_node_rank;

    //maintains if bidirectional connectivity and also active linkers and connectors
    protected Hashtable _chota_connection_state;

    //ip packet handler to mark bidirectional connectivity
    protected ChotaConnectionIPPacketHandler _ip_handler;
    
    //node rank comparer
    protected NodeRankComparer _cmp;
    
    /*
     * We don't want to risk mistyping these strings.
     */
    static protected readonly string struc_chota = "structured.chota";

#if ARI_CHOTA_DEBUG
    protected int debug_counter = 0;
#endif
    
    
    public ChotaConnectionOverlord(Node n)
    {
      _node = n;
      _cmp = new NodeRankComparer();
      _sync = new object();
      _rand = new Random();
      _chota_connection_state = new Hashtable();
      _ip_handler = new ChotaConnectionIPPacketHandler();
      node_rank_list = new ArrayList();
      _dest_to_node_rank = new Hashtable();

      lock( _sync ) {
	_node.ConnectionTable.ConnectionEvent +=
          new EventHandler(this.ConnectHandler); 

	// we assess trimming/growing situation on every heart beat
        _node.HeartBeatEvent += new EventHandler(this.CheckState);
        _node.SubscribeToSends(AHPacket.Protocol.IP, this);
	//subscribe the ip_handler to IP packets
	_node.Subscribe(AHPacket.Protocol.IP, this);
      }
#if ARI_EXP_DEBUG
      Console.WriteLine("ChotaConnectionOverlord starting : {0}", DateTime.Now);
#endif
      
    }
    /**
     * On every activation, the ChotaConnectionOverlord trims any connections
     * that are unused, and also creates any new connections of needed
     * 
     */
    override public void Activate() {
      if (!IsActive) {
#if ARI_CHOTA_DEBUG || ARI_EXP_DEBUG
	Console.WriteLine("ChotaConnectionOverlord is inactive");
#endif
	return;
      }
      //it is now that we do things with connections
      ConnectionTable tab = _node.ConnectionTable;

      NodeRankInformation to_add = null;
      Connection to_trim = null;

      lock(tab.SyncRoot) {//lock the connection table
	lock(_sync) { //lock the score table
	  int structured_count = tab.Count(ConnectionType.Structured);
	  //we assume that we are well-connected before ChotaConnections are needed. 
	  if( structured_count < 2 ) {
#if ARI_CHOTA_DEBUG
	    Console.WriteLine("Not sufficient structured connections to bootstrap Chotas.");
#endif
	    //if we do not have sufficient structured connections
	    //we do not;
	    return;
	  }
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Finding a connection to trim... ");
#endif
	  //find out the lowest score guy to trim
          SortTable();
	  for (int i = node_rank_list.Count - 1; i >= max_chota && i > 0; i--)  
	  {
	    NodeRankInformation node_rank = (NodeRankInformation) node_rank_list[i];
	    bool trim = false;
	    foreach(Connection c in tab.GetConnections(struc_chota)) {
	      if (node_rank.Addr.Equals(c.Address)) {
		to_trim = c;
		trim = true;
		break;
	      }
	      if (trim) {
		break;
	      }
	    }
	  }
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Finding connections to open... ");
#endif
	  //find out the highest score  guy who need connections
	  for (int i = 0; i < node_rank_list.Count && i < max_chota; i++) 
	  {
	    //we are traversing the list in descending order of 
	    bool add = true;
	    NodeRankInformation node_rank = (NodeRankInformation) node_rank_list[i];
#if ARI_CHOTA_DEBUG
	    Console.WriteLine("Testing: {0}", node_rank);
#endif
	    if (node_rank.Count < MIN_SCORE_THRESHOLD ) {
#if ARI_CHOTA_DEBUG
	      Console.WriteLine("To poor score for a connection....");
#endif
	      //too low score to create a connection
	      continue;
	    }
	    //TimeSpan elapsed = DateTime.Now - node_rank.LastRetryInstant;
	    //if (elapsed.TotalSeconds < _retry_delay) {
	    //Console.WriteLine("To early for retry, Now = {0} and {1} < {2}", 
	    //			DateTime.Now, elapsed.TotalSeconds, _retry_delay);
	      //wait for some time before sending a connection request again
	      //continue;
	    //}
	    //check if there is an active connector/linker or lacking bidirectional 
	    //connectivity

	    ChotaConnectionState state = null;
	    if (_chota_connection_state.ContainsKey(node_rank.Addr)) {
	      state = (ChotaConnectionState) _chota_connection_state[node_rank.Addr];
	    } else {
#if ARI_CHOTA_DEBUG
	      Console.WriteLine("Creating a new chota connection state."); 
#endif	      
	      state = new ChotaConnectionState(node_rank.Addr);
	      _chota_connection_state[node_rank.Addr] = state;
	    }
	    if (!state.CanConnect)
	    {
#if ARI_CHOTA_DEBUG
	      Console.WriteLine("No point connecting. Active connector or no recorded bidirectionality."); 
#endif
	      continue;
	    }

#if ARI_CHOTA_DEBUG
	    Console.WriteLine("{0} looks good chota connection.", node_rank);
#endif
	    //make sure that this guy doesn't have any Structured or Leaf Connection already
	    foreach(Connection c in tab.GetConnections(ConnectionType.Structured)) {
	      if (node_rank.Addr.Equals(c.Address)) {
#if ARI_CHOTA_DEBUG
		Console.WriteLine("{0} already has a structured connection - {1}. ", node_rank, c.ConType);
#endif
		add = false;
		break;
	      }
	    }
	    foreach(Connection c in tab.GetConnections(ConnectionType.Leaf)) {
	      if (node_rank.Addr.Equals(c.Address)) {
#if ARI_CHOTA_DEBUG
		Console.WriteLine("{0} already has a leaf connection. ", node_rank);
#endif
		add = false;
		break;
	      }
	    }
	    if (add) {
	      to_add = node_rank;
	      break;
	    }
	  }
	}
	
	//connection to add
	if (to_add != null) {
	  //the first connection would have the highest score
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Trying to form a chota connection to addr: {0}", to_add.Addr);
#endif
	  to_add.LastRetryInstant = DateTime.Now;
	  ConnectTo(to_add.Addr, 1024, struc_chota);
	} else {
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("No new connection to add... ");
#endif
	}
	//now pick some guy who can be trimmed off 
	if (to_trim != null) {
	  //lets pick the guy who possibly has the lowest score
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Trimming chota connection with addr: {0}", to_trim.Address);
#endif
	  _node.GracefullyClose(to_trim.Edge);
	} else {
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("No connection to trim... ");
#endif
	}
      }
    }
    
  override public bool NeedConnection 
    {
      get {
	return true;
      } 
    }
    public override bool IsActive 
    {
      get {
	return _active;
      }
      set {
	_active = value;
      }
    }
    /**
     * When we get ConnectToMessage responses the connector tells us.
     */
    override public void HandleCtmResponse(Connector c, AHPacket resp_p,
                                           ConnectToMessage ctm_resp)
    {
      /**
       * Time to start linking:
       */
      Linker l = new Linker(_node, ctm_resp.Target.Address,
                            ctm_resp.Target.Transports,
                            ctm_resp.ConnectionType);
      _node.TaskQueue.Enqueue( l );
    }
    /**
     * Here is how we handle Send subscriptions
     */
    public void HandleAHPacket(object node, AHPacket p, Edge from) {
      if( from == null ) {
        /*
         * This is a Send, or a packet that came from us
         */
        UpdateTable(p);
      }
      else {
        //This is an incoming packet
        ReceivePacketHandler(p);
      }
    }
    /**
     * Everytime we the node sends a packet out this method is invoked. 
     * Since multiple invocations may exist, take care of synchronization. 
     */
    public void UpdateTable(AHPacket p) {
    /*
     * We know the following conditions are never true because
     * we are only subscribed to IP packets, and the Node will
     * not send null packets
      
      if (p == null) {
	return;
      }
      if (!p.PayloadType.Equals(AHPacket.Protocol.IP)) {
	return;
      }
      */
#if ARI_CHOTA_DEBUG
      Console.WriteLine("Receiving an IP-packet send event...");
      Console.WriteLine("IP packet: update table");
#endif
      /*
       * We don't need to keep a perfectly accurate count.
       * As an optimization, we could just sample:
       */
      if( _rand.Next(4) != 0 ) {
        return;
      }
      lock(_sync) {
        /*
         * We have to lock here to make the following an atomic
         * operation, otherwise we could leave this table inconsistent
         */
        NodeRankInformation node_rank =
          (NodeRankInformation)_dest_to_node_rank[p.Destination];
        if( node_rank == null ) {
          //This is a new guy:
	  node_rank = new NodeRankInformation(p.Destination);
          node_rank_list.Add( node_rank );
          _dest_to_node_rank[p.Destination] = node_rank;
        }
        //Since we only update once every fourth time, go ahead
        //and bump the count by 4 each time, so the count represents
        //the expected number of packets we have sent.
        node_rank.Count = node_rank.Count + 4;
        //There, we have updated the node_rank
      }
    }
    /**
     * We only need to do this before we take action based on the
     * table, in the mean time, it can get disordered
     */
    protected void SortTable() {
      lock( _sync ) {
        //Keep the table sorted according to _cmp
        node_rank_list.Sort( _cmp );
	if (node_rank_list.Count > node_rank_capacity) {
          //we are exceeding capacity
          //trim the list
          int rmv_idx = node_rank_list.Count - 1;
          NodeRankInformation nr = (NodeRankInformation)node_rank_list[ rmv_idx ];
	  node_rank_list.RemoveAt(rmv_idx);    
          _dest_to_node_rank.Remove( nr.Addr );
	}
      }
    }
    /**
     * On every heartbeat this method is invoked.
     * We decide which edge to trim and which one to add
     */ 
    public void CheckState(object node, EventArgs eargs) {
#if ARI_CHOTA_DEBUG
      Console.WriteLine("Receiving a heart beat event...");
      debug_counter++;
#endif
      //in this case we decrement the rank
      //update information in the connection table.
      lock(_sync) {
        SortTable();
        foreach(NodeRankInformation node_rank in node_rank_list) {
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Pre-decrement -> Heartbeat: {0}", node_rank);
#endif
	  int count = node_rank.Count;
	  if (count > 0) {
	    node_rank.Count = count - 1;
	  }
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Post-decrement -> Heartbeat: {0}", node_rank);
#endif
	  //should also forget connectivity issues once we fall below MIN_SCORE_THRESHOLD
	  if (node_rank.Count < MIN_SCORE_THRESHOLD) {
	    if (_chota_connection_state.ContainsKey(node_rank.Addr)) {
	      ChotaConnectionState state = 
		(ChotaConnectionState) _chota_connection_state[node_rank.Addr];
	      state.Received = false;
#if ARI_CHOTA_DEBUG
	      Console.WriteLine("ChotaConnectionState -  Reverting to unidirectional: {0}", node_rank.Addr);
#endif
	    }
	  }
	}
      }

#if ARI_CHOTA_DEBUG
      //periodically print out ChotaConnectionState as we know
      if (debug_counter >= 5) {
	lock(_sync) {
	  IDictionaryEnumerator ide = _chota_connection_state.GetEnumerator();
	  while(ide.MoveNext()) {
	    ChotaConnectionState state = (ChotaConnectionState) ide.Value;
	    Address addr_key = (Address) ide.Key;
	    if (state.Connector != null) {
	      Console.WriteLine("ChotaConnectionState: {0} => Active Connector; Connectivity: {1}"
				, addr_key, state.Received);
	    } else {
	      Console.WriteLine("ChotaConnectionState: {0} => No connector; Connectivity: {1}"
				, addr_key, state.Received);
	    }
	  }
	}
	debug_counter = 0;
      }
#endif

#if ARI_CHOTA_DEBUG
      Console.WriteLine("Calling activate... ");
#endif
      //everything fine now take a look at connections
      //let us see which connections are to trim
      Activate();
    }

    /**
     * When a Connector finishes his job, this method is called to
     * clean up the connector but at the same time;
     * we record all unfinished linkers and subscribe to finish events.
     * This ensures we do not make a connection attempt in the meanwhile.
     */
    protected void ConnectorEndHandler(object connector, EventArgs args)
    {
      lock( _sync ) {
	Connector ctr = (Connector)connector;
	//we do not need to lock the connector; since it is already over
	IDictionaryEnumerator ide = _chota_connection_state.GetEnumerator();
#if ARI_CHOTA_DEBUG
	Address addr_key = null;
#endif
	while(ide.MoveNext()) {
	  ChotaConnectionState state = (ChotaConnectionState) ide.Value;
	  if (state.Connector != null && state.Connector.Equals(ctr)) {
#if ARI_CHOTA_DEBUG
	    addr_key = (Address) ide.Key;
	    Console.WriteLine("ConnectorEndHandler: Connector (Chota) ended for target: {0}", 
			      addr_key);
#endif
	    //set the associated connector to null;
	    state.Connector = null;
	    break;
	  }
	}
#if ARI_CHOTA_DEBUG
	if (addr_key == null) {
	  Console.WriteLine("Finshed connector not in our records. We may have trimmed this info before.");
	}
#endif
      }
    }

    /**
     * Everytime we the node receives a packet this method is invoked. 
     * All this does is to update the ChotaConnectionState "bidirectional connectivity"
     * flag.
     */
    public void ReceivePacketHandler(AHPacket p) {
      //update information in chota_connection_state.

#if ARI_CHOTA_DEBUG
      Console.WriteLine("Got an IP packet from src: {0} ", p.Source);
#endif

      //Getting from a Hashtable is threadsafe... no need to lock
      ChotaConnectionState state =
        (ChotaConnectionState) _chota_connection_state[p.Source];
      if ( state != null ) {
	state.Received = true;
      }
    }

    /**
     * This method is called when a new Connection is added
     * to the ConnectionTable; currently just for debugging. 
     */
    protected void ConnectHandler(object contab, EventArgs eargs)
    {
#if ARI_CHOTA_DEBUG
      Connection new_con1 = ((ConnectionEventArgs)eargs).Connection; 
      Console.WriteLine("Forming a connection: {0}", new_con1);
#endif
#if ARI_EXP_DEBUG
      Connection new_con2 = ((ConnectionEventArgs)eargs).Connection; 
      if (new_con2.ConType.Equals(struc_chota)) {
	Console.WriteLine("Forming a chota connection: {0} at :{1}",
	                  new_con2, DateTime.Now);
      }
#endif
    }
    /**
     * This method is called when a we disconnect
     */
    protected void DisconnectHandler(object contab, EventArgs eargs)
    {
      
#if ARI_CHOTA_DEBUG
      Connection new_con1 = ((ConnectionEventArgs)eargs).Connection;
      Console.WriteLine("Disconnect connection: {0}", new_con1);
#endif
#if ARI_EXP_DEBUG
      Connection new_con2 = ((ConnectionEventArgs)eargs).Connection;
      if (new_con2.ConType.Equals(struc_chota)) {
	Console.WriteLine("Disconnect a chota connection: {0} at: {1}",
	                  new_con2, DateTime.Now);
      }
#endif
    }
   
    protected void ConnectTo(Address target,
			     short t_ttl, string contype)
    {
      ConnectionType mt = Connection.StringToMainType(contype);
      /*
       * This is an anonymous delegate which is called before
       * the Connector starts.  If it returns true, the Connector
       * will finish immediately without sending an ConnectToMessage
       */
      Linker l = new Linker(_node, target, null, contype);
      object link_task = l.Task;
      Connector.AbortCheck abort = delegate(Connector c) {
        bool stop = false;
        lock( _node.ConnectionTable.SyncRoot ) {
          stop = _node.ConnectionTable.Contains( mt, target );
          if (!stop ) {
            /*
             * Make a linker to get the task.  We won't use
             * this linker.
             * No need in sending a ConnectToMessage if we
             * already have a linker going.
             */
            stop = _node.TaskQueue.HasTask( link_task );
          }
        }
        return stop;
      };
      if ( abort(null) ) {
#if ARI_CHOTA_DEBUG
	Console.WriteLine("Looks like we are already connected to the target: {0}"
			  , target);
#endif
        return;
      }
      short t_hops = 0;
      ConnectToMessage ctm =
        new ConnectToMessage(contype, _node.GetNodeInfo(6) );
      ctm.Id = _rand.Next(1, Int32.MaxValue);
      ctm.Dir = ConnectionMessage.Direction.Request;

      AHPacket ctm_pack =
        new AHPacket(t_hops, t_ttl, _node.Address, target, AHPacket.AHOptions.Exact,
                     AHPacket.Protocol.Connection, ctm.ToByteArray());

      Connector con = new Connector(_node, ctm_pack, ctm, this);
      lock( _sync ) {
	ChotaConnectionState state = null;
	if (!_chota_connection_state.ContainsKey(target)) {
	  //state = new ChotaConnectionState(target);
	  //_chota_connection_state[target] = state;
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("We can't be asked to connect without ChotaConnectionState (Shouldn't have happened).");
	  return;
#endif
	} else {
	  state = (ChotaConnectionState) _chota_connection_state[target];
	}
	if (!state.CanConnect) 
	{ 
#if ARI_CHOTA_DEBUG
	  Console.WriteLine("Can't connect: Active connector or no recorded bidirectionality (Shouldn't have got here).");
	  return;
#endif	  
	}
	state.Connector = con;
      }
      con.FinishEvent += new EventHandler(this.ConnectorEndHandler);
      
#if ARI_CHOTA_DEBUG
      Console.WriteLine("ChotaConnectionOverlord: Starting a real chota connection attempt to: {0}", target);
#endif

#if ARI_EXP_DEBUG
      Console.WriteLine("ChotaConnectionOverlord: Starting a real chota connection attempt to: {0} at {1}", target, DateTime.Now);
#endif

      //Start work on connecting
      con.AbortIf = abort;
      _node.TaskQueue.Enqueue( con );
    }
  }
}