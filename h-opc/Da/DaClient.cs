﻿using Hylasoft.Opc.Common;
using Opc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Factory = OpcCom.Factory;
using OpcDa = Opc.Da;

namespace Hylasoft.Opc.Da
{
    /// <summary>
    /// Client Implementation for DA
    /// </summary>
    public class DaClient : IOpcClient
    {
        #region # Fields and Constructors

        /// <summary>
        /// Default monitor interval in Milliseconds
        /// </summary>
        private const int DefaultMonitorInterval = 100;

        /// <summary>
        /// OPC URL
        /// </summary>
        private readonly URL _url;

        /// <summary>
        /// OpcDa underlying server object
        /// </summary>
        private OpcDa.Server _server;

        /// <summary>
        /// Subscription auto identifier
        /// </summary>
        private long _sub;

        /// <summary>
        /// Initialize a new Data Access Client
        /// </summary>
        /// <param name="serverUrl">The url of the server to connect to. WARNING: If server URL includes
        /// spaces (ex. "RSLinx OPC Server") then pass the server URL in to the constructor as an Opc.URL object
        /// directly instead.</param>
        public DaClient(Uri serverUrl)
        {
            this._url = new URL(serverUrl.AbsolutePath)
            {
                Scheme = serverUrl.Scheme,
                HostName = serverUrl.Host
            };
        }

        /// <summary>
        /// Initialize a new Data Access Client
        /// </summary>
        /// <param name="serverUrl">The url of the server to connect to</param>
        public DaClient(URL serverUrl)
        {
            this._url = serverUrl;
        }

        #endregion

        #region # Properties

        #region Monitor interval in Milliseconds —— int? MonitorInterval
        /// <summary>
        /// Monitor interval in Milliseconds
        /// </summary>
        public int? MonitorInterval { get; set; }
        #endregion

        #region Gets the current status of the OPC Client —— OpcStatus Status
        /// <summary>
        /// Gets the current status of the OPC Client
        /// </summary>
        public OpcStatus Status { get; private set; }
        #endregion

        #region OpcDa underlying server object —— OpcDa.Server Server
        /// <summary>
        /// OpcDa underlying server object
        /// </summary>
        public OpcDa.Server Server
        {
            get { return this._server; }
        }
        #endregion

        #endregion

        #region # Methods

        //Implements

        #region Connect the client to the OPC Server —— void Connect()
        /// <summary>
        /// Connect the client to the OPC Server
        /// </summary>
        public void Connect()
        {
            if (this.Status == OpcStatus.Connected)
            {
                return;
            }

            this._server = new OpcDa.Server(new Factory(), this._url);
            this._server.Connect();
            this.Status = OpcStatus.Connected;
        }
        #endregion

        #region Gets the datatype of an OPC tag —— Type GetDataType(string tag)
        /// <summary>
        /// Gets the datatype of an OPC tag
        /// </summary>
        /// <param name="tag">Tag to get datatype of</param>
        /// <returns>System Type</returns>
        public System.Type GetDataType(string tag)
        {
            OpcDa.Item item = new OpcDa.Item { ItemName = tag };
            OpcDa.ItemProperty result;

            try
            {
                OpcDa.ItemPropertyCollection propertyCollection = this._server.GetProperties(new ItemIdentifier[] { item }, new[] { new OpcDa.PropertyID(1) }, false)[0];
                result = propertyCollection[0];
            }
            catch (NullReferenceException)
            {
                throw new OpcException("Could not find node because server not connected.");
            }

            return result.DataType;
        }
        #endregion

        #region Read a tag —— ReadEvent Read(string tag)
        /// <summary>
        /// Read a tag
        /// </summary>
        /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` reads the tag `bar` on the folder `foo`</param>
        /// <returns>The value retrieved from the OPC</returns>
        public ReadEvent Read(string tag)
        {
            OpcDa.Item item = new OpcDa.Item { ItemName = tag };
            if (this.Status == OpcStatus.NotConnected)
            {
                throw new OpcException("Server not connected. Cannot read tag.");
            }
            OpcDa.ItemValueResult result = this._server.Read(new[] { item })[0];

            ReadEvent readEvent = new ReadEvent();
            readEvent.Value = result.Value;
            readEvent.SourceTimestamp = result.Timestamp;
            readEvent.ServerTimestamp = result.Timestamp;
            if (result.Quality == OpcDa.Quality.Good) readEvent.Quality = Quality.Good;
            if (result.Quality == OpcDa.Quality.Bad) readEvent.Quality = Quality.Bad;

            return readEvent;
        }
        #endregion

        #region Read a tag —— ReadEvent<T> Read<T>(string tag)
        /// <summary>
        /// Read a tag
        /// </summary>
        /// <typeparam name="T">The type of tag to read</typeparam>
        /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` reads the tag `bar` on the folder `foo`</param>
        /// <returns>The value retrieved from the OPC</returns>
        public ReadEvent<T> Read<T>(string tag)
        {
            ReadEvent readEvent = this.Read(tag);

            ReadEvent<T> readEventGeneric = new ReadEvent<T>
            {
                Value = this.TryCastResult<T>(readEvent.Value),
                Quality = readEvent.Quality,
                ServerTimestamp = readEvent.ServerTimestamp,
                SourceTimestamp = readEvent.SourceTimestamp
            };

            return readEventGeneric;
        }
        #endregion

        #region Read a tag asynchronusly —— Task<ReadEvent<T>> ReadAsync<T>(string tag)
        /// <summary>
        /// Read a tag asynchronusly
        /// </summary>
        public async Task<ReadEvent<T>> ReadAsync<T>(string tag)
        {
            return await Task.Run(() => this.Read<T>(tag));
        }
        #endregion

        #region Write a value on the specified opc tag —— void Write<T>(string tag, T item)
        /// <summary>
        /// Write a value on the specified opc tag
        /// </summary>
        /// <typeparam name="T">The type of tag to write on</typeparam>
        /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` writes on the tag `bar` on the folder `foo`</param>
        /// <param name="item"></param>
        public void Write<T>(string tag, T item)
        {
            OpcDa.ItemValue itmVal = new OpcDa.ItemValue
            {
                ItemName = tag,
                Value = item
            };
            IdentifiedResult result = this._server.Write(new[] { itmVal })[0];

            if (result == null)
            {
                throw new OpcException("The server replied with an empty response");
            }
            if (result.ResultID.ToString() != "S_OK")
            {
                throw new OpcException($"Invalid response from the server. (Response Status: {result.ResultID}, Opc Tag: {tag})");
            }
        }
        #endregion

        #region Write a value on the specified opc tag asynchronously —— Task WriteAsync<T>(string tag, T item)
        /// <summary>
        /// Write a value on the specified opc tag asynchronously
        /// </summary>
        public async Task WriteAsync<T>(string tag, T item)
        {
            await Task.Run(() => this.Write(tag, item));
        }
        #endregion

        #region Monitor the specified tag for changes —— void Monitor(string tag, Action<ReadEvent, Action> callback)
        /// <summary>
        /// Monitor the specified tag for changes
        /// </summary>
        /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` monitors the tag `bar` on the folder `foo`</param>
        /// <param name="callback">the callback to execute when the value is changed.
        /// The first parameter is a MonitorEvent object which represents the data point, the second is an `unsubscribe` function to unsubscribe the callback</param>
        public void Monitor(string tag, Action<ReadEvent, Action> callback)
        {
            OpcDa.SubscriptionState subItem = new OpcDa.SubscriptionState
            {
                Name = (++this._sub).ToString(CultureInfo.InvariantCulture),
                Active = true,
                UpdateRate = this.MonitorInterval ?? DefaultMonitorInterval
            };
            OpcDa.ISubscription sub = this._server.CreateSubscription(subItem);

            // I have to start a new thread here because unsubscribing
            // the subscription during a datachanged event causes a deadlock
            Action unsubscribe = () => new Thread(o => this._server.CancelSubscription(sub)).Start();

            sub.DataChanged += (handle, requestHandle, values) =>
            {
                ReadEvent monitorEvent = new ReadEvent();
                monitorEvent.Value = values[0].Value;
                monitorEvent.SourceTimestamp = values[0].Timestamp;
                monitorEvent.ServerTimestamp = values[0].Timestamp;
                if (values[0].Quality == OpcDa.Quality.Good) monitorEvent.Quality = Quality.Good;
                if (values[0].Quality == OpcDa.Quality.Bad) monitorEvent.Quality = Quality.Bad;
                callback(monitorEvent, unsubscribe);
            };
            sub.AddItems(new[] { new OpcDa.Item { ItemName = tag } });
            sub.SetEnabled(true);
        }
        #endregion

        #region Monitor the specified tag for changes —— void Monitor<T>(string tag, Action<ReadEvent<T>, Action> callback)
        /// <summary>
        /// Monitor the specified tag for changes
        /// </summary>
        /// <typeparam name="T">the type of tag to monitor</typeparam>
        /// <param name="tag">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` monitors the tag `bar` on the folder `foo`</param>
        /// <param name="callback">the callback to execute when the value is changed.
        /// The first parameter is a MonitorEvent object which represents the data point, the second is an `unsubscribe` function to unsubscribe the callback</param>
        public void Monitor<T>(string tag, Action<ReadEvent<T>, Action> callback)
        {
            OpcDa.SubscriptionState subItem = new OpcDa.SubscriptionState
            {
                Name = (++this._sub).ToString(CultureInfo.InvariantCulture),
                Active = true,
                UpdateRate = this.MonitorInterval ?? DefaultMonitorInterval
            };
            OpcDa.ISubscription sub = this._server.CreateSubscription(subItem);

            // I have to start a new thread here because unsubscribing
            // the subscription during a datachanged event causes a deadlock
            Action unsubscribe = () => new Thread(o => this._server.CancelSubscription(sub)).Start();

            sub.DataChanged += (handle, requestHandle, values) =>
            {
                T casted = this.TryCastResult<T>(values[0].Value);
                ReadEvent<T> monitorEvent = new ReadEvent<T>();
                monitorEvent.Value = casted;
                monitorEvent.SourceTimestamp = values[0].Timestamp;
                monitorEvent.ServerTimestamp = values[0].Timestamp;
                if (values[0].Quality == OpcDa.Quality.Good) monitorEvent.Quality = Quality.Good;
                if (values[0].Quality == OpcDa.Quality.Bad) monitorEvent.Quality = Quality.Bad;
                callback(monitorEvent, unsubscribe);
            };
            sub.AddItems(new[] { new OpcDa.Item { ItemName = tag } });
            sub.SetEnabled(true);
        }
        #endregion

        #region Monitor the specified tags for changes —— void Monitor(IEnumerable<string> tags...
        /// <summary>
        /// Monitor the specified tags for changes
        /// </summary>
        /// <param name="tags">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` monitors the tag `bar` on the folder `foo`</param>
        /// <param name="callback">the callback to execute when the value is changed.
        /// The first parameter is the new values of the nodes, the second is an `unsubscribe` function to unsubscribe the callback</param>
        public void Monitor(IEnumerable<string> tags, Action<IDictionary<string, ReadEvent>, Action> callback)
        {
            //Updated by Lee

            #region # Check

            tags = tags?.Distinct().ToArray() ?? new string[0];
            if (!tags.Any())
            {
                throw new ArgumentNullException(nameof(tags), "tags cannot be empty !");
            }

            #endregion

            OpcDa.SubscriptionState subItem = new OpcDa.SubscriptionState
            {
                Name = (++this._sub).ToString(CultureInfo.InvariantCulture),
                Active = true,
                UpdateRate = this.MonitorInterval ?? DefaultMonitorInterval
            };
            OpcDa.ISubscription sub = this._server.CreateSubscription(subItem);

            // I have to start a new thread here because unsubscribing
            // the subscription during a datachanged event causes a deadlock
            Action unsubscribe = () => new Thread(o => this._server.CancelSubscription(sub)).Start();

            IDictionary<string, ReadEvent> readEvents = new ConcurrentDictionary<string, ReadEvent>();

            sub.DataChanged += (handle, requestHandle, values) =>
            {
                foreach (OpcDa.ItemValueResult itemValueResult in values)
                {
                    ReadEvent monitorEvent = new ReadEvent();
                    monitorEvent.Value = itemValueResult.Value;
                    monitorEvent.SourceTimestamp = itemValueResult.Timestamp;
                    monitorEvent.ServerTimestamp = itemValueResult.Timestamp;
                    if (itemValueResult.Quality == OpcDa.Quality.Good) monitorEvent.Quality = Quality.Good;
                    if (itemValueResult.Quality == OpcDa.Quality.Bad) monitorEvent.Quality = Quality.Bad;

                    string tag = itemValueResult.ItemName.ToString();
                    if (readEvents.ContainsKey(tag))
                    {
                        readEvents[tag] = monitorEvent;
                    }
                    else
                    {
                        readEvents.Add(tag, monitorEvent);
                    }
                }

                callback(readEvents, unsubscribe);
            };

            sub.AddItems(tags.Select(tag => new OpcDa.Item { ItemName = tag }).ToArray());
            sub.SetEnabled(true);
        }
        #endregion

        #region Monitor the specified tags for changes —— void MonitorChanges(IEnumerable<string> tags...
        /// <summary>
        /// Monitor the specified tags for changes
        /// </summary>
        /// <param name="tags">The fully-qualified identifier of the tag. You can specify a subfolder by using a comma delimited name.
        /// E.g: the tag `foo.bar` monitors the tag `bar` on the folder `foo`</param>
        /// <param name="callback">the callback to execute when the value is changed.
        /// The first parameter is the new values of the nodes, the second is an `unsubscribe` function to unsubscribe the callback</param>
        public void MonitorChanges(IEnumerable<string> tags, Action<IDictionary<string, ReadEvent>, Action> callback)
        {
            //Updated by Lee

            #region # Check

            tags = tags?.Distinct().ToArray() ?? new string[0];
            if (!tags.Any())
            {
                throw new ArgumentNullException(nameof(tags), "tags cannot be empty !");
            }

            #endregion

            OpcDa.SubscriptionState subItem = new OpcDa.SubscriptionState
            {
                Name = (++this._sub).ToString(CultureInfo.InvariantCulture),
                Active = true,
                UpdateRate = this.MonitorInterval ?? DefaultMonitorInterval
            };
            OpcDa.ISubscription sub = this._server.CreateSubscription(subItem);

            // I have to start a new thread here because unsubscribing
            // the subscription during a datachanged event causes a deadlock
            Action unsubscribe = () => new Thread(o => this._server.CancelSubscription(sub)).Start();

            sub.DataChanged += (handle, requestHandle, values) =>
            {
                IDictionary<string, ReadEvent> readEvents = new ConcurrentDictionary<string, ReadEvent>();

                foreach (OpcDa.ItemValueResult itemValueResult in values)
                {
                    ReadEvent monitorEvent = new ReadEvent();
                    monitorEvent.Value = itemValueResult.Value;
                    monitorEvent.SourceTimestamp = itemValueResult.Timestamp;
                    monitorEvent.ServerTimestamp = itemValueResult.Timestamp;
                    if (itemValueResult.Quality == OpcDa.Quality.Good) monitorEvent.Quality = Quality.Good;
                    if (itemValueResult.Quality == OpcDa.Quality.Bad) monitorEvent.Quality = Quality.Bad;

                    string tag = itemValueResult.ItemName.ToString();
                    readEvents.Add(tag, monitorEvent);
                }

                callback(readEvents, unsubscribe);
            };

            sub.AddItems(tags.Select(tag => new OpcDa.Item { ItemName = tag }).ToArray());
            sub.SetEnabled(true);
        }
        #endregion

        #region Release unmanaged resources —— void Dispose()
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (this.Server != null)
            {
                this._server.Dispose();
                this.Status = OpcStatus.NotConnected;

                GC.SuppressFinalize(this);
            }
        }
        #endregion


        //Private

        #region Casts result of monitoring and reading values —— T TryCastResult<T>(object value)
        /// <summary>
        /// Casts result of monitoring and reading values
        /// </summary>
        /// <param name="value">Value to convert</param>
        /// <typeparam name="T">Type of object to try to cast</typeparam>
        /// <returns>The casted result</returns>
        private T TryCastResult<T>(object value)
        {
            try
            {
                return (T)value;
            }
            catch (InvalidCastException)
            {
                throw new InvalidCastException($"Could not monitor tag. Cast failed for type \"{typeof(T)}\" on the new value \"{value}\" with type \"{value.GetType()}\". Make sure tag data type matches.");
            }
        }
        #endregion

        #endregion
    }
}

