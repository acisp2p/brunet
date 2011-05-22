/*
This program is part of BruNet, a library for the creation of efficient overlay
networks.
Copyright (C) 2008 David Wolinsky <davidiw@ufl.edu>, University of Florida

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using Brunet;
using Brunet.Util;
using System;
using System.Collections;

#if BRUNET_NUNIT
using NUnit.Framework;
using System.Security.Cryptography;
#endif

namespace Brunet.Messaging.Mock {
  /// <summary>This class provides a MockDataHandler object that keeps a hash
  /// table containing all the data received and an event to notify when an
  /// HandleData has been called.</summary>
  public class MockDataHandler: IDataHandler {
    Hashtable _ht;
    ArrayList _order;
    MemBlock _last_received;
    public int Count { get { return _order.Count; } }
    public MemBlock LastReceived { get { return _last_received; } }
    public event EventHandler HandleDataCallback;

    public MockDataHandler() {
      _ht = new Hashtable();
      _order = new ArrayList();
    }

    public void HandleData(MemBlock payload, ISender return_path, object state) {
      _last_received = payload;
      _ht[payload] = return_path;
      _order.Add(payload);
      if(HandleDataCallback != null) {
        HandleDataCallback(payload, null);
      }
    }

    public bool Contains(object o) {
      return _ht.Contains(o);
    }

    public ISender Value(object o) {
      return _ht[o] as ISender;
    }

    public int Position(object o) {
      return _order.IndexOf(o);
    }
  }
#if BRUNET_NUNIT
  [TestFixture]
  public class MDHTest {
    int _count = 0;
    protected void Callback(object o, EventArgs ea) {
      _count++;
    }

    [Test]
    public void test() {
      MockDataHandler mdh = new MockDataHandler();
      mdh.HandleDataCallback += Callback;
      ISender sender = new MockSender(null, null, mdh, 0);
      byte[][] b = new byte[10][];
      MemBlock[] mb = new MemBlock[10];
      Random rand = new Random();
      for(int i = 0; i < 10; i++) {
        b[i] = new byte[128];
        rand.NextBytes(b[i]);
        mb[i] = MemBlock.Reference(b[i]);
        sender.Send(mb[i]);
      }

      for(int i = 0; i < 10; i++) {
        Assert.AreEqual(i, mdh.Position(mb[i]), "Position " + i);
        Assert.IsTrue(mdh.Contains(mb[i]), "Contains " + i);
      }

      Assert.AreEqual(_count, 10, "Count");
    }
  }
#endif
}
