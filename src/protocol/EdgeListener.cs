/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2005  University of California

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

/*
 * using Brunet.TransportAddress;
 * using Brunet.Edge;
 */

using Brunet;
using System;
using System.Net;
using System.Net.Sockets;
using System.Collections;

namespace Brunet
{

  /**
   * Abstract class which represents the listers for
   * new edges.  When these listeners "hear" and edge,
   * they send the EdgeEvent
   * 
   * The EdgeListener also creates edges for its
   * own TransportAddress type.
   */

  public abstract class EdgeListener
  {

#if PLAB_LOG
    private BrunetLogger _logger;
    public BrunetLogger Logger{
	get{
	  return _logger;
	}
	set
	{
	  _logger = value;          
	}
    }
#endif

    /**
     * @param success if the CreateEdgeTo was successful, this is true
     * @param e the newly created edge, if success is true, else e is null
     * @param x if success is false this may contain an exception
     */
    public delegate void EdgeCreationCallback(bool success, Edge e, Exception x);

    /**
     * A ReadOnly list of TransportAddress objects for
     * this EdgeListener
     */
    public abstract ArrayList LocalTAs
    {
      get;
      }

      /**
       * What type of TransportAddress does this EdgeListener use
       */
      public abstract Brunet.TransportAddress.TAType TAType
      {
        get;
        }

        /**
         * @return true if the Start method has been called
         */
        public abstract bool IsStarted
        {
          get;
          }
          /**
           * @param ta TransportAddress to create an edge to
           * @param ecb the EdgeCreationCallback to call when done
           * @throw EdgeException if we try to call this before calling
           * Start.
           */
          public abstract void CreateEdgeTo(TransportAddress ta, EdgeCreationCallback ecb);

    /**
     * Looks up the local IP addresses and returns a list
     * of transport Address objects which match them.
     * Loopback addresses will be at the end.
     *
     * Both UdpEdgeListener and TcpEdgeListener make use of this
     *
     * @todo it would be better to have a more precise method here
     * than using the DNS.  It may have to be platform specific,
     * and then fall back to the DNS technique.
     */
    static protected ArrayList GetIPTAs(TransportAddress.TAType tat, int port)
    {
      ArrayList tas = new ArrayList();
      try {
        String StrLocalHost =  (Dns.GetHostName());
        IPHostEntry IPEntry = Dns.GetHostByName (StrLocalHost);
        foreach(IPAddress a in IPEntry.AddressList) {
          /**
           * We add Loopback addresses to the back, all others to the front
           * This makes sure non-loopback addresses are listed first.
           */
          if( IPAddress.IsLoopback(a) ) {
            //Put it at the back
            tas.Add( new TransportAddress(tat, new IPEndPoint(a, port) ) );
          }
          else {
            //Put it at the front
            tas.Insert(0, new TransportAddress(tat, new IPEndPoint(a, port) ) );
          }
        }
      }
      catch(SocketException x) {
        //If the hostname is not properly configured, we could wind
	//up here.  Just put the loopback address is:
        tas.Add( new TransportAddress(tat, new IPEndPoint(IPAddress.Loopback, port) ) );
      }
      return tas;
    }

    public event System.EventHandler EdgeEvent;

    //This function sends the New Edge event
    protected void SendEdgeEvent(Edge e)
    {
      if( EdgeEvent != null ) 
        EdgeEvent(e, EventArgs.Empty);
    }

    /**
     * Start listening for edges.  Edges
     * received will be announced with the EdgeEvent
     * 
     * This must be called before CreateEdgeTo.
     */
    public abstract void Start();
    /**
     * Stop listening for edges.
     * The edgelistener may not be garbage collected
     * until this is called
     */
    public abstract void Stop();
  }

}
