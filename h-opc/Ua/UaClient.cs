﻿using System;
using System.Collections.Generic;
using System.Linq;
using Hylasoft.Opc.Common;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Hylasoft.Opc.Ua
{
  /// <summary>
  /// Client Implementation for UA
  /// </summary>
  public class UaClient : IClient<UaNode>
  {
    private readonly Uri _serverUrl;
    private Session _session;
    private readonly IDictionary<string, UaNode> _nodesCache = new Dictionary<string, UaNode>();

    // default monitor interval in Milliseconds
    private const int DefaultMonitorInterval = 100;

    // TODO undestand if this has to be parametric
    private const uint AttributeId = 13U;

    /// <summary>
    /// Creates a server object
    /// </summary>
    /// <param name="serverUrl">the url of the server to connect to</param>
    public UaClient(Uri serverUrl)
    {
      _serverUrl = serverUrl;
      Status = OpcStatus.NotConnected;
    }

    #region interface methods

    /// <summary>
    /// Connect the client to the OPC Server
    /// </summary>
    public void Connect()
    {
      if (Status == OpcStatus.Connected)
        return;
      _session = InitializeSession(_serverUrl);
      var node = _session.NodeCache.Find(ObjectIds.ObjectsFolder);
      RootNode = new UaNode(this, string.Empty, node.NodeId.ToString());
      AddNodeToCache(RootNode);
      Status = OpcStatus.Connected;
    }

    /// <summary>
    /// Gets the current status of the OPC Client
    /// </summary>
    public OpcStatus Status { get; private set; }

    /// <summary>
    /// Read a tag
    /// </summary>
    /// <typeparam name="T">The type of tag to read</typeparam>
    /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
    /// E.g: the tag `foo.bar` reads the tag `bar` on the folder `foo`</param>
    /// <returns>The value retrieved from the OPC</returns>
    public T Read<T>(string tag)
    {
      var n = FindNode(tag, RootNode);
      var nodesToRead = new ReadValueIdCollection
      {
        new ReadValueId
        {
          NodeId = n.NodeId,
          AttributeId = AttributeId
        }
      };
      DataValueCollection results;
      DiagnosticInfoCollection diag;
      _session.Read(null, 0, TimestampsToReturn.Neither, nodesToRead, out results, out diag);
      var val = results[0];

      CheckReturnValue(val.StatusCode);
      return (T)val.Value;
    }

    /// <summary>
    /// Write a value on the specified opc tag
    /// </summary>
    /// <typeparam name="T">The type of tag to write on</typeparam>
    /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
    /// E.g: the tag `foo.bar` writes on the tag `bar` on the folder `foo`</param>
    /// <param name="item"></param>
    public void Write<T>(string tag, T item)
    {
      var n = FindNode(tag, RootNode);
      var writeValue = new WriteValue
      {
        NodeId = n.NodeId,
        AttributeId = AttributeId,
        Value = { Value = item }
      };
      var nodesToWrite = new WriteValueCollection { writeValue };

      StatusCodeCollection results;
      DiagnosticInfoCollection diag;
      _session.Write(null, nodesToWrite, out results, out diag);
      CheckReturnValue(results[0]);
    }

    /// <summary>
    /// Monitor the specified tag for changes
    /// </summary>
    /// <typeparam name="T">the type of tag to monitor</typeparam>
    /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
    /// E.g: the tag `foo.bar` monitors the tag `bar` on the folder `foo`</param>
    /// <param name="callback">the callback to execute when the value is changed.
    /// The first parameter is the new value of the node, the second is an `unsubscribe` function to unsubscribe the callback</param>
    public void Monitor<T>(string tag, Action<T, Action> callback)
    {
      var node = FindNode(tag);

      var sub = new Subscription
      {
        PublishingInterval = DefaultMonitorInterval,
        PublishingEnabled = true,
        DisplayName = tag,
        Priority = byte.MaxValue
      };

      var item = new MonitoredItem
      {
        StartNodeId = node.NodeId,
        AttributeId = AttributeId,
        DisplayName = tag,
        SamplingInterval = DefaultMonitorInterval,
      };
      sub.AddItem(item);
      _session.AddSubscription(sub);
      sub.Create();
      sub.ApplyChanges();

      item.Notification += (monitoredItem, args) =>
      {
        var p = (MonitoredItemNotification)args.NotificationValue;
        var t = p.Value.WrappedValue.Value;
        Action unsubscribe = () =>
        {
          sub.RemoveItems(sub.MonitoredItems);
          sub.Delete(true);
          _session.RemoveSubscription(sub);
          sub.Dispose();
        };
        callback((T)t, unsubscribe);
      };
    }

    /// <summary>
    /// Explore a folder on the Opc Server
    /// </summary>
    /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
    /// E.g: the tag `foo.bar` finds the sub nodes of `bar` on the folder `foo`</param>
    /// <returns>The list of sub-nodes</returns>
    public IEnumerable<UaNode> ExploreFolder(string tag)
    {
      var folder = FindNode(tag);
      var nodes = ClientUtils.Browse(_session, folder.NodeId)
        .GroupBy(n => n.NodeId) //this is to select distinct
        .Select(n => n.First())
        .Where(n => n.NodeClass == NodeClass.Variable || n.NodeClass == NodeClass.Object)
        .Select(n => n.ToHylaNode(this, folder))
        .ToList();

      //add nodes to cache
      foreach (var node in nodes)
        AddNodeToCache(node);

      return nodes;
    }

    /// <summary>
    /// Finds a node on the Opc Server
    /// </summary>
    /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
    /// E.g: the tag `foo.bar` finds the tag `bar` on the folder `foo`</param>
    /// <returns>If there is a tag, it returns it, otherwise it throws an </returns>
    public UaNode FindNode(string tag)
    {
      // if the tag already exists in cache, return it
      if (_nodesCache.ContainsKey(tag))
        return _nodesCache[tag];

      // try to find the tag otherwise
      var found = FindNode(tag, RootNode);
      if (found != null)
      {
        AddNodeToCache(found);
        return found;
      }

      // throws an exception if not found
      throw new OpcException(string.Format("The tag \"{0}\" doesn't exist on the Server", tag));
    }

    /// <summary>
    /// Gets the root node of the server
    /// </summary>
    public UaNode RootNode { get; private set; }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public void Dispose()
    {
      _session.RemoveSubscriptions(_session.Subscriptions);
      _session.Close();
      _session.Dispose();
      GC.SuppressFinalize(this);
    }

    #endregion

    #region private methods

    private static void CheckReturnValue(StatusCode status)
    {
      if (status.ToString() != "Good")
        throw new OpcException(string.Format("Invalid response from the server. (Response Status: {0})", status));
    }

    /// <summary>
    /// Adds a node to the cache using the tag as its key
    /// </summary>
    /// <param name="node">the node to add</param>
    private void AddNodeToCache(UaNode node)
    {
      if (!_nodesCache.ContainsKey(node.Tag))
        _nodesCache.Add(node.Tag, node);
    }

    /// <summary>
    /// Crappy method to initialize the session. I don't know what many of these things do, sincerely.
    /// </summary>
    private static Session InitializeSession(Uri url)
    {
      var l = new CertificateValidator();
      l.CertificateValidation += (sender, eventArgs) =>
      {
        eventArgs.Accept = true;
      };
      var appInstance = new ApplicationInstance
      {
        ApplicationType = ApplicationType.Client,
        ConfigSectionName = "h-opc-client",
        ApplicationConfiguration = new ApplicationConfiguration
        {
          ApplicationUri = url.ToString(),
          ApplicationName = "h-opc-client",
          ApplicationType = ApplicationType.Client,
          CertificateValidator = l,
          SecurityConfiguration = new SecurityConfiguration
          {
            AutoAcceptUntrustedCertificates = true
          },
          TransportQuotas = new TransportQuotas
          {
            OperationTimeout = 600000,
            MaxStringLength = 1048576,
            MaxByteStringLength = 1048576,
            MaxArrayLength = 65535,
            MaxMessageSize = 4194304,
            MaxBufferSize = 65535,
            ChannelLifetime = 300000,
            SecurityTokenLifetime = 3600000
          },
          ClientConfiguration = new ClientConfiguration
          {
            DefaultSessionTimeout = 60000,
            MinSubscriptionLifetime = 10000
          },
          DisableHiResClock = true
        }
      };
      var endpoints = ClientUtils.SelectEndpoint(url, false);
      var session = Session.Create(appInstance.ApplicationConfiguration,
        new ConfiguredEndpoint(null, endpoints,
          EndpointConfiguration.Create(appInstance.ApplicationConfiguration)), false, false,
        appInstance.ApplicationConfiguration.ApplicationName, 60000U, null, new string[] { });

      return session;
    }

    /// <summary>
    /// Finds a node starting from the specified node as the root folder
    /// </summary>
    /// <param name="tag">the tag to find</param>
    /// <param name="node">the root node</param>
    /// <returns></returns>
    private UaNode FindNode(string tag, UaNode node)
    {
      var folders = tag.Split('.');
      var head = folders.FirstOrDefault();
      UaNode found;
      try
      {
        var subNodes = node.SubNodes.Cast<UaNode>();
        found = subNodes.Single(n => n.Name == head);
      }
      catch (Exception ex)
      {
        throw new OpcException(string.Format("The tag \"{0}\" doesn't exist on folder \"{1}\"", head, node.Tag), ex);
      }

      return folders.Length == 1
        ? found // last node, return it
        : FindNode(string.Join(".", folders.Except(new[] { head })), found); // find sub nodes
    }

    #endregion
  }
}
