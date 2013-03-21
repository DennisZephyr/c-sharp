﻿//Build Date: Jan 15, 2013
#if (__MonoCS__)
#define TRACE
#endif

using System;
using System.IO;
using System.Text;
using System.Net;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Configuration;
using Microsoft.Win32;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if (SILVERLIGHT || WINDOWS_PHONE)
using System.Windows.Threading;
using System.IO.IsolatedStorage;

#endif
namespace PubNubMessaging.Core
{
    // INotifyPropertyChanged provides a standard event for objects to notify clients that one of its properties has changed

    public class Pubnub : INotifyPropertyChanged
    {
        const int pubnubWebRequestCallbackIntervalInSeconds = 310;
        const int pubnubOperationTimeoutIntervalInSeconds = 200;
        const int pubnubNetworkTcpCheckIntervalInSeconds = 15;
        const int pubnubNetworkCheckRetries = 50;
        const int pubnubWebRequestRetryIntervalInSeconds = 10;
        bool overrideTcpKeepAlive = true;
        const LoggingMethod.Level pubnubLogLevel = LoggingMethod.Level.Error;

#if (!SILVERLIGHT && !WINDOWS_PHONE)
        bool pubnubEnableProxyConfig = true;
#endif

        // Common property changed event
        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        ConcurrentDictionary<string, long> _channelSubscription = new ConcurrentDictionary<string, long>();
        ConcurrentDictionary<string, long> _channelPresence = new ConcurrentDictionary<string, long>();
        ConcurrentDictionary<string, RequestState> _channelRequest = new ConcurrentDictionary<string, RequestState>();
        ConcurrentDictionary<string, bool> _channelInternetStatus = new ConcurrentDictionary<string, bool>();
        ConcurrentDictionary<string, int> _channelInternetRetry = new ConcurrentDictionary<string, int>();
        ConcurrentDictionary<string, Timer> _channelReconnectTimer = new ConcurrentDictionary<string, Timer>();
        ConcurrentDictionary<Uri, Timer> _channelHeartbeatTimer = new ConcurrentDictionary<Uri, Timer>();

        private IPubnubUnitTest _pubnubUnitTest;
        public IPubnubUnitTest PubnubUnitTest
        {
            get
            {
                return _pubnubUnitTest;
            }
            set
            {
                _pubnubUnitTest = value;
            }
        }

#if (!SILVERLIGHT && !WINDOWS_PHONE)
        //Proxy
        private PubnubProxy _pubnubProxy = null;
        public PubnubProxy Proxy
        {
            get
            {
                return _pubnubProxy;
            }
            set
            {
                _pubnubProxy = value;
                if (_pubnubProxy == null)
                {
                    throw new ArgumentException("Missing Proxy Details");
                }
                if (string.IsNullOrEmpty(_pubnubProxy.ProxyServer) || (_pubnubProxy.ProxyPort <= 0) || string.IsNullOrEmpty(_pubnubProxy.ProxyUserName) || string.IsNullOrEmpty(_pubnubProxy.ProxyPassword))
                {
                    _pubnubProxy = null;
                    throw new MissingFieldException("Insufficient Proxy Details");
                }
            }
        }
#endif

        System.Threading.Timer heartBeatTimer;

        private static bool _pubnetSystemActive = true;

        // History of Messages (Obsolete)
        private List<object> _history = new List<object>();
        public List<object> History { get { return _history; } set { _history = value; RaisePropertyChanged("History"); } }

        // Subscribe
        private ConcurrentDictionary<string, object> _subscribeMessage = new ConcurrentDictionary<string, object>();

        // Presence
        private ConcurrentDictionary<string, object> _presenceMessage = new ConcurrentDictionary<string, object>();

        // Timestamp
        private List<object> _time = new List<object>();

        // Pubnub Core API implementation
        private string origin = "pubsub.pubnub.com";
        private string publishKey = "";
        private string subscribeKey = "";
        private string secretKey = "";
        private string cipherKey = "";
        private bool ssl = false;
        private string sessionUUID = "";
        private string parameters = "";

        public string SessionUUID
        {
            get
            {
                return sessionUUID;
            }
            set
            {
                sessionUUID = value;
            }
        }

        /**
         * Pubnub instance initialization function
         * 
         * @param string publishKey.
         * @param string subscribeKey.
         * @param string secretKey.
         * @param bool sslOn
         */
        private void Init(string publishKey, string subscribeKey, string secretKey, string cipherKey, bool sslOn)
        {
#if(MONOTOUCH || MONODROID || SILVERLIGHT || WINDOWS_PHONE)
            LoggingMethod.LogLevel = pubnubLogLevel;
#else
            string configuredLogLevel = ConfigurationManager.AppSettings["PubnubMessaging.LogLevel"];
            int logLevelValue;
            if (!Int32.TryParse(configuredLogLevel, out logLevelValue))
            {
                LoggingMethod.LogLevel = pubnubLogLevel;
            }
            else
            {
                LoggingMethod.LogLevel = (LoggingMethod.Level)logLevelValue;
            }
#endif

            this.publishKey = publishKey;
            this.subscribeKey = subscribeKey;
            this.secretKey = secretKey;
            this.cipherKey = cipherKey;
            this.ssl = sslOn;
            
            VerifyOrSetSessionUUID();

            // SSL is ON?
            if (this.ssl)
                this.origin = "https://" + this.origin;
            else
                this.origin = "http://" + this.origin;

            //Initiate System Events for PowerModeChanged - to monitor suspend/resume
            InitiatePowerModeCheck();
        }

        private void ReconnectNetwork<T>(ReconnectState<T> netState)
        {
            System.Threading.Timer timer = new Timer(new TimerCallback(ReconnectNetworkCallback<T>), netState, 0, pubnubNetworkTcpCheckIntervalInSeconds * 1000);
            _channelReconnectTimer.AddOrUpdate(netState.channel, timer, (key, oldState) => timer);
        }

        void ReconnectNetworkCallback<T>(Object reconnectState)
        {
            try
            {
                ReconnectState<T> netState = reconnectState as ReconnectState<T>;
                if (netState != null)
                {
                    if (_channelInternetStatus.ContainsKey(netState.channel)
                        && (netState.type == ResponseType.Subscribe || netState.type == ResponseType.Presence))
                    {
                        if (_channelInternetStatus[netState.channel])
                        {
                            //Reset Retry if previous state is true
                            _channelInternetRetry.AddOrUpdate(netState.channel, 0, (key, oldValue) => 0);
                        }
                        else
                        {
                            _channelInternetRetry.AddOrUpdate(netState.channel, 1, (key, oldValue) => oldValue + 1);
                            LoggingMethod.WriteToLog(string.Format("DateTime {0}, {1} {2} reconnectNetworkCallback. Retry {3} of {4}", DateTime.Now.ToString(), netState.channel, netState.type, _channelInternetRetry[netState.channel], pubnubNetworkCheckRetries), LoggingMethod.LevelInfo);
                        }
                    }

                    if (_channelInternetStatus[netState.channel])
                    {
                        if (_channelReconnectTimer.ContainsKey(netState.channel))
                        {
                            _channelReconnectTimer[netState.channel].Change(Timeout.Infinite, Timeout.Infinite);
                            _channelReconnectTimer[netState.channel].Dispose();
                        }

                        LoggingMethod.WriteToLog(string.Format("DateTime {0}, {1} {2} reconnectNetworkCallback. Internet Available : {3}", DateTime.Now.ToString(), netState.channel, netState.type, _channelInternetStatus[netState.channel]), LoggingMethod.LevelInfo);
                        switch (netState.type)
                        {
                            case ResponseType.Subscribe:
                                SubscribeRequest(netState.channel, netState.timetoken, netState.callback, netState.connectCallback, false);
                                break;
                            case ResponseType.Presence:
                                PresenceRequest(netState.channel, netState.timetoken, netState.callback, false);
                                break;
                            default:
                                break;
                        }
                    }
                    else if (_channelInternetRetry[netState.channel] >= pubnubNetworkCheckRetries)
                    {
                        if (_channelReconnectTimer.ContainsKey(netState.channel))
                        {
                            _channelReconnectTimer[netState.channel].Change(Timeout.Infinite, Timeout.Infinite);
                            _channelReconnectTimer[netState.channel].Dispose();
                        }
                        switch (netState.type)
                        {
                            case ResponseType.Subscribe:
                                SubscribeExceptionHandler(netState.channel, netState.callback, netState.connectCallback, true);
                                break;
                            case ResponseType.Presence:
                                PresenceExceptionHandler(netState.channel, netState.callback, true);
                                break;
                            default:
                                break;
                        }
                    }
                }
                else
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Unknown request state in reconnectNetworkCallback", DateTime.Now.ToString()), LoggingMethod.LevelError);
                }
            }
            catch (Exception ex)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0} method:reconnectNetworkCallback \n Exception Details={1}", DateTime.Now.ToString(), ex.ToString()), LoggingMethod.LevelError);
            }
        }


        private void InitiatePowerModeCheck()
        {
#if (!SILVERLIGHT && !WINDOWS_PHONE && !MONOTOUCH && !MONODROID)
            try
            {
                SystemEvents.PowerModeChanged += new PowerModeChangedEventHandler(SystemEvents_PowerModeChanged);
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Initiated System Event - PowerModeChanged.", DateTime.Now.ToString()), LoggingMethod.LevelInfo);
            }
            catch (Exception ex)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0} No support for System Event - PowerModeChanged.", DateTime.Now.ToString()), LoggingMethod.LevelError);
                LoggingMethod.WriteToLog(string.Format("DateTime {0} {1}", DateTime.Now.ToString(), ex.ToString()), LoggingMethod.LevelError);
            }
#endif
        }

#if (!SILVERLIGHT && !WINDOWS_PHONE && !MONOTOUCH && !MONODROID)
        void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _pubnetSystemActive = false;
                TerminatePendingWebRequest();
                if (overrideTcpKeepAlive)
                {
                    heartBeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }

                LoggingMethod.WriteToLog(string.Format("DateTime {0}, System entered into Suspend Mode.", DateTime.Now.ToString()), LoggingMethod.LevelInfo);

                if (overrideTcpKeepAlive)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Disabled Timer for heartbeat ", DateTime.Now.ToString()), LoggingMethod.LevelInfo);
                }
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _pubnetSystemActive = true;
                if (overrideTcpKeepAlive)
                {
                    heartBeatTimer.Change(
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? -1 : pubnubNetworkTcpCheckIntervalInSeconds * 1000,
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? -1 : pubnubNetworkTcpCheckIntervalInSeconds * 1000);
                }

                LoggingMethod.WriteToLog(string.Format("DateTime {0}, System entered into Resume/Awake Mode.", DateTime.Now.ToString()), LoggingMethod.LevelInfo);

                if (overrideTcpKeepAlive)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Enabled Timer for heartbeat ", DateTime.Now.ToString()), LoggingMethod.LevelInfo);
                }
            }
        }
#endif
        private void TerminatePendingWebRequest()
        {
            TerminatePendingWebRequest(null);
        }
        
        private void TerminatePendingWebRequest(RequestState state)
        {
            if (state != null && state.request != null)
            {
                TerminateHeartbeatTimer(state.request.RequestUri);
                state.request.Abort();
                LoggingMethod.WriteToLog(string.Format("DateTime {0} TerminatePendingWebRequest {1}", DateTime.Now.ToString(), state.request.RequestUri.ToString()), LoggingMethod.LevelInfo);

                RequestState removedReq;
                bool removeKey = _channelRequest.TryRemove(state.channel, out removedReq);
                if (!removeKey)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0} Unable to remove web request from dictionary in TerminatePendingWebRequest for channel= {1}", DateTime.Now.ToString(), state.channel), LoggingMethod.LevelError);
                }
            }
            else
            {
                ConcurrentDictionary<string, RequestState> webRequest = _channelRequest;
                ICollection<string> keyCollection = _channelRequest.Keys;
                foreach (string key in keyCollection)
                {
                    RequestState currentRequest = _channelRequest[key];
                    if (currentRequest.request != null)
                    {
                        if (currentRequest.type == ResponseType.Subscribe)
                        {
                            Unsubscribe<string>(currentRequest.channel, null);
                        }
                        else if (currentRequest.type == ResponseType.Presence)
                        {
                            PresenceUnsubscribe<string>(currentRequest.channel.Replace("-pnpres", ""), null);
                        }
                        else
                        {
                            TerminateHeartbeatTimer(currentRequest.request.RequestUri);
                            currentRequest.request.Abort();
                            LoggingMethod.WriteToLog(string.Format("DateTime {0} TerminatePendingWebRequest {1}", DateTime.Now.ToString(), currentRequest.request.RequestUri.ToString()), LoggingMethod.LevelInfo);
                        }
                        bool removeKey = _channelRequest.TryRemove(key, out currentRequest);
                        if (!removeKey)
                        {
                            LoggingMethod.WriteToLog(string.Format("DateTime {0} Unable to remove web request from dictionary in TerminatePendingWebRequest for channel= {1}", DateTime.Now.ToString(), key), LoggingMethod.LevelError);
                        }
                    }
                }
            }
        }

#if (!SILVERLIGHT && !WINDOWS_PHONE && !MONOTOUCH && !MONODROID)
        ~Pubnub()
        {
            //detach
            SystemEvents.PowerModeChanged -= new PowerModeChangedEventHandler(SystemEvents_PowerModeChanged);
        }
#endif
        /**
         * PubNub 3.0 API
         * 
         * Prepare Pubnub messaging class initial state
         * 
         * @param string publishKey.
         * @param string subscribeKey.
         * @param string secretKey.
         * @param bool sslOn
         */
        public Pubnub(string publishKey, string subscribeKey, string secretKey, string cipherKey, bool sslOn)
        {
            this.Init(publishKey, subscribeKey, secretKey, cipherKey, sslOn);
        }

        /**
         * PubNub 2.0 Compatibility
         * 
         * Prepare Pubnub messaging class initial state
         * 
         * @param string publishKey.
         * @param string subscribeKey.
         */
        public Pubnub(string publishKey, string subscribeKey)
        {
            this.Init(publishKey, subscribeKey, "", "", false);
        }

        /// <summary>
        /// PubNub without SSL
        /// Prepare Pubnub messaging class initial state
        /// </summary>
        /// <param name="publishKey"></param>
        /// <param name="subscribeKey"></param>
        /// <param name="secretKey"></param>
        public Pubnub(string publishKey, string subscribeKey, string secretKey)
        {
            this.Init(publishKey, subscribeKey, secretKey, "", false);
        }

        /// <summary>
        /// History (Obsolete)
        /// Load history from a channel
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="limit"></param>
        /// <returns></returns>
        [Obsolete("This method should no longer be used, please use DetailedHistory() instead.")]
        public bool history(string channel, int limit)
        {
            List<string> url = new List<string>();

            url.Add("history");
            url.Add(this.subscribeKey);
            url.Add(channel);
            url.Add("0");
            url.Add(limit.ToString());

            return ProcessRequest(url, ResponseType.History);
        }

        /**
         * Detailed History
         */
        public bool DetailedHistory(string channel, long start, long end, int count, bool reverse, Action<object> userCallback)
        {
            return DetailedHistory<object>(channel, start, end, count, reverse, userCallback);
        }

        public bool DetailedHistory<T>(string channel, long start, long end, int count, bool reverse, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }

            Uri request = BuildDetailedHistoryRequest(channel, start, end, count, reverse);

            return UrlProcessRequest<T>(request, channel, ResponseType.DetailedHistory, userCallback, null, false);
        }

        public bool DetailedHistory(string channel, long start, Action<object> userCallback, bool reverse)
        {
            return DetailedHistory<object>(channel, start, -1, -1, reverse, userCallback);
        }

        public bool DetailedHistory<T>(string channel, long start, Action<T> userCallback, bool reverse)
        {
            return DetailedHistory<T>(channel, start, -1, -1, reverse, userCallback);
        }

        public bool DetailedHistory(string channel, int count, Action<object> userCallback)
        {
            return DetailedHistory<object>(channel, -1, -1, count, false, userCallback);
        }

        public bool DetailedHistory<T>(string channel, int count, Action<T> userCallback)
        {
            return DetailedHistory<T>(channel, -1, -1, count, false, userCallback);
        }

        /// <summary>
        /// Publish
        /// Send a message to a channel
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="message"></param>
        /// <param name="userCallback"></param>
        /// <returns></returns>
        public bool Publish(string channel, object message, Action<object> userCallback)
        {
            return Publish<object>(channel, message, userCallback);
        }

        public bool Publish<T>(string channel, object message, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()) || message == null)
            {
                throw new ArgumentException("Missing Channel or Message");
            }

            if (string.IsNullOrEmpty(this.publishKey) || string.IsNullOrEmpty(this.publishKey.Trim()) || this.publishKey.Length <= 0)
            {
                throw new MissingFieldException("Invalid publish key");
            }

            if (userCallback == null)
            {
                throw new ArgumentException("Missing Callback");
            }

            Uri request = BuildPublishRequest(channel, message);

            return UrlProcessRequest<T>(request, channel, ResponseType.Publish, userCallback, null, false); //connectCallback = null
        }

        private string JsonEncodePublishMsg(object originalMessage)
        {
            string message = SerializeToJsonString(originalMessage);


            if (this.cipherKey.Length > 0)
            {
                PubnubCrypto aes = new PubnubCrypto(this.cipherKey);
                string encryptMessage = aes.Encrypt(message);
                message = SerializeToJsonString(encryptMessage);
            }

            return message;
        }

        private List<object> DecodeMessage(List<object> message, ResponseType type)
        {
            List<object> receivedMessage = new List<object>();


            if (type == ResponseType.Presence || type == ResponseType.Publish || type == ResponseType.Time || type == ResponseType.Here_Now || type == ResponseType.Leave)
            {
                return message;
            }
            else if (type == ResponseType.DetailedHistory)
            {
                receivedMessage = DecodeDecryptLoop(message);
            }
            else
            {
                receivedMessage = DecodeDecryptLoop(message);
            }
            return receivedMessage;
        }

        private List<object> DecodeDecryptLoop(List<object> message)
        {
            List<object> returnMessage = new List<object>();
            if (this.cipherKey.Length > 0)
            {
                PubnubCrypto aes = new PubnubCrypto(this.cipherKey);
                var myObjectArray = (from item in message select item as object).ToArray();
                IEnumerable enumerable = myObjectArray[0] as IEnumerable;
                if (enumerable != null)
                {
                    List<object> receivedMsg = new List<object>();
                    foreach (object element in enumerable)
                    {
                        string decryptMessage = aes.Decrypt(element.ToString());
                        object decodeMessage = (decryptMessage == "**DECRYPT ERROR**") ? decryptMessage : JsonConvert.DeserializeObject<object>(decryptMessage);
                        receivedMsg.Add(decodeMessage);
                    }
                    returnMessage.Add(receivedMsg);
                }

                for (int index = 1; index < myObjectArray.Length; index++)
                {
                    returnMessage.Add(myObjectArray[index]);
                }
                return returnMessage;
            }
            else
            {
                var myObjectArray = (from item in message select item as object).ToArray();
                IEnumerable enumerable = myObjectArray[0] as IEnumerable;
                if (enumerable != null)
                {
                    List<object> receivedMessage = new List<object>();
                    foreach (object element in enumerable)
                    {
                        receivedMessage.Add(element);
                    }
                    returnMessage.Add(receivedMessage);
                }
                for (int index = 1; index < myObjectArray.Length; index++)
                {
                    returnMessage.Add(myObjectArray[index]);
                }
                return returnMessage;
            }
        }


        /// <summary>
        /// Subscribe
        /// Listen for a message on a channel
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="userCallback"></param>
        public void Subscribe(string channel, Action<object> userCallback)
        {
            Subscribe<object>(channel, userCallback);
        }

        public void Subscribe(string channel, Action<object> userCallback, Action<object> connectCallback)
        {
            Subscribe<object>(channel, userCallback, connectCallback);
        }

        public void Subscribe<T>(string channel, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }
            if (userCallback == null)
            {
                throw new ArgumentException("Missing Callback");
            }

            SubscribeInit<T>(channel, userCallback, null);
        }

        public void Subscribe<T>(string channel, Action<T> userCallback, Action<T> connectCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }
            if (userCallback == null)
            {
                throw new ArgumentException("Missing userCallback");
            }
            if (connectCallback == null)
            {
                throw new ArgumentException("Missing connectCallback");
            }

            SubscribeInit<T>(channel, userCallback, connectCallback);
        }

        private void SubscribeInit<T>(string channel, Action<T> userCallback, Action<T> connectCallback)
        {
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, requested subscribe for channel={1}", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);

            if (_channelSubscription.ContainsKey(channel))
            {
                List<object> result = new List<object>();
                string jsonString = "[0, \"Already subscribed\"]";
                result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                result.Add(channel);
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON subscribe response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                GoToCallback<T>(result, userCallback);
            }
            else
            {
                object subscribeMessage = _subscribeMessage.TryRemove(channel, out subscribeMessage); //Clear the dictionary received for last sub msg
                _channelSubscription.GetOrAdd(channel, 0);
                ResetInternetCheckSettings(channel);
                SubscribeRequest<T>(channel, 0, userCallback, connectCallback, false);
            }

        }

        private void ResetInternetCheckSettings(string channel)
        {
            if (_channelInternetStatus.ContainsKey(channel))
            {
                _channelInternetStatus.AddOrUpdate(channel, true, (key, oldValue) => true);
            }
            else
            {
                _channelInternetStatus.GetOrAdd(channel, true); //Set to true for internet connection
            }

            if (_channelInternetRetry.ContainsKey(channel))
            {
                _channelInternetRetry.AddOrUpdate(channel, 0, (key, oldValue) => 0);
            }
            else
            {
                _channelInternetRetry.GetOrAdd(channel, 0); //Initialize the internet retry count
            }
        }

        void OnPubnubWebRequestTimeout(object state, bool timeout)
        {
            if (timeout)
            {
                RequestState currentState = state as RequestState;
                if (currentState != null)
                {
                    PubnubWebRequest request = currentState.request;
                    if (request != null)
                    {
                        currentState.timeout = true;
                        LoggingMethod.WriteToLog(string.Format("DateTime: {0}, OnPubnubWebRequestTimeout: client request timeout reached.Request aborted for channel = {1}", DateTime.Now.ToString(), currentState.channel), LoggingMethod.LevelInfo);
                        request.Abort();
                    }
                }
                else
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime: {0}, OnPubnubWebRequestTimeout: client request timeout reached. However state is unknown", DateTime.Now.ToString()), LoggingMethod.LevelError);
                }
            }
        }

        void OnPubnubHeartBeatTimeoutCallback(Object heartbeatState)
        {
            LoggingMethod.WriteToLog(string.Format("DateTime: {0}, **OnPubnubHeartBeatTimeoutCallback**", DateTime.Now.ToString()), LoggingMethod.LevelVerbose);

            RequestState currentState = heartbeatState as RequestState;
            if (currentState != null)
            {
                if (_channelInternetStatus.ContainsKey(currentState.channel)
                    && (currentState.type == ResponseType.Subscribe || currentState.type == ResponseType.Presence)
                    && overrideTcpKeepAlive)
                {
                    bool networkConnection = ClientNetworkStatus.CheckInternetStatus(_pubnetSystemActive);

                    _channelInternetStatus[currentState.channel] = networkConnection;

                    LoggingMethod.WriteToLog(string.Format("DateTime: {0}, OnPubnubHeartBeatTimeoutCallback - Internet connection = {1}", DateTime.Now.ToString(), networkConnection), LoggingMethod.LevelVerbose);
                    if (!networkConnection)
                    {
                        TerminatePendingWebRequest(currentState);
                    }
                }


            }

        }


        /// <summary>
        /// Check the response of the REST API and call for re-subscribe
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="subscribeResult"></param>
        /// <param name="userCallback"></param>
        private void SubscribeInternalCallback<T>(object subscribeResult, Action<T> userCallback, Action<T> connectCallback)
        {
            List<object> message = subscribeResult as List<object>;
            string channelName = "";
            if (message != null && message.Count >= 3)
            {
                channelName = message[2].ToString();
            }
            else
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Lost Channel Name for resubscribe", DateTime.Now.ToString()), LoggingMethod.LevelError);
                return;
            }

            if (!_channelSubscription.ContainsKey(channelName))
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Due to Unsubscribe, further re-subscription was stopped for channel {1}", DateTime.Now.ToString(), channelName.ToString()), LoggingMethod.LevelInfo);
                return;
            }


            if (message != null && message.Count >= 3)
            {
                SubscribeRequest<T>(channelName, (object)message[1], userCallback, connectCallback, false);
            }
        }

        private void PresenceInternalCallback<T>(object presenceResult, Action<T> userCallback)
        {
            List<object> message = presenceResult as List<object>;
            string channelName = "";
            if (message != null && message.Count >= 3)
            {
                channelName = message[2].ToString();
            }
            else
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Lost Channel Name for re-presence", DateTime.Now.ToString()), LoggingMethod.LevelError);
                return;
            }


            if (message != null && message.Count >= 3)
            {
                PresenceRequest<T>(channelName, (object)message[1], userCallback, false);
            }
        }

        /// <summary>
        /// To unsubscribe a channel
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="userCallback"></param>
        public void Unsubscribe(string channel, Action<object> userCallback)
        {
            Unsubscribe<object>(channel, userCallback);
        }

        /// <summary>
        /// To unsubscribe a channel
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="channel"></param>
        /// <param name="userCallback"></param>
        public void Unsubscribe<T>(string channel, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }

            bool unsubscribeStatus = false;
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, requested unsubscribe for channel={1}", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);

            string jsonString = "";
            List<object> result = new List<object>();

            if (_channelSubscription.ContainsKey(channel))
            {
                long unsubValue;
                unsubscribeStatus = _channelSubscription.TryRemove(channel, out unsubValue);

                if (unsubscribeStatus)
                {
                    if (_channelRequest.ContainsKey(channel))
                    {
                        PubnubWebRequest storedRequest = _channelRequest[channel].request;
                        TerminateHeartbeatTimer(storedRequest.RequestUri);
                        storedRequest.Abort();
                    }
                    object subscribeMessage = _subscribeMessage.TryRemove(channel, out subscribeMessage); //Clear the dictionary received for last sub msg

                    jsonString = string.Format("[1, \"Unsubscribed from {0}\"]", channel);
                }
                else
                {
                    jsonString = string.Format("[1, \"Error unsubscribing from {0}\"]", channel);
                }



                result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                result.Add(channel);

                LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON unsubscribe response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                GoToCallback<T>(result, userCallback);

                //just fire leave() event to REST API for safeguard
                Uri request = BuildLeaveRequest(channel);
                UrlProcessRequest<T>(request, channel, ResponseType.Leave, null, null, false); // connectCallback = null

            }
            else
            {
                result = new List<object>();
                jsonString = "[0, \"Channel Not Subscribed\"]";
                result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                result.Add(channel);
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON unsubscribe response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                GoToCallback<T>(result, userCallback);
            }
        }

        /// <summary>
        /// SubscribeRequest private method for Subscribe
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="channel"></param>
        /// <param name="timetoken"></param>
        /// <param name="userCallback"></param>
        /// <param name="connectCallback"></param>
        /// <param name="reconnect"></param>
        private void SubscribeRequest<T>(string channel, object timetoken, Action<T> userCallback, Action<T> connectCallback, bool reconnect)
        {
            //Exit if the channel is unsubscribed
            if (!_channelSubscription.ContainsKey(channel))
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Due to Unsubscribe, further subscription was stopped for channel {1}", DateTime.Now.ToString(), channel.ToString()), LoggingMethod.LevelInfo);
                return;
            }
            _channelSubscription.AddOrUpdate(channel, Convert.ToInt64(timetoken.ToString()), (key, oldValue) => Convert.ToInt64(timetoken.ToString())); //Store the timetoken

            if (_channelInternetStatus.ContainsKey(channel) && (!_channelInternetStatus[channel]) && _pubnetSystemActive)
            {
                if (_channelInternetRetry.ContainsKey(channel) && (_channelInternetRetry[channel] >= pubnubNetworkCheckRetries))
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Subscribe channel={1} - No internet connection. MAXed retries for internet ", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);
                    SubscribeExceptionHandler<T>(channel, userCallback, connectCallback, true);
                    return;
                }

                if (overrideTcpKeepAlive)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Subscribe - No internet connection for {1}", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);

                    ReconnectState<T> netState = new ReconnectState<T>();
                    netState.channel = channel;
                    netState.type = ResponseType.Subscribe;
                    netState.callback = userCallback;
                    netState.connectCallback = connectCallback;
                    netState.timetoken = timetoken;

                    ReconnectNetwork<T>(netState);
                    return;
                }
            }


            // Begin recursive subscribe
            try
            {
                // Build URL
                Uri request = BuildSubscribeRequest(channel, timetoken);

                // Wait for message
                UrlProcessRequest<T>(request, channel, ResponseType.Subscribe, userCallback, connectCallback, reconnect);
            }
            catch (Exception ex)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0} method:_subscribe \n channel={1} \n timetoken={2} \n Exception Details={3}", DateTime.Now.ToString(), channel, timetoken.ToString(), ex.ToString()), LoggingMethod.LevelError);
                this.SubscribeRequest<T>(channel, timetoken, userCallback, connectCallback, false);
            }
        }

        /// <summary>
        /// Presence
        /// Listen for a presence message on a channel
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="userCallback"></param>
        public void Presence(string channel, Action<object> userCallback)
        {
            Presence<object>(channel, userCallback);
        }

        public void Presence<T>(string channel, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }

            if (userCallback == null)
            {
                throw new ArgumentException("Missing Callback");
            }

            channel = string.Format("{0}-pnpres", channel);
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, requested presence for channel={1}", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);

            if (_channelPresence.ContainsKey(channel))
            {
                List<object> result = new List<object>();
                string jsonString = "[0, \"Presence Already subscribed\"]";
                result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                result.Add(channel.Replace("-pnpres", ""));
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON presence response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                GoToCallback<T>(result, userCallback);
            }
            else
            {
                _channelPresence.GetOrAdd(channel, 0);
                ResetInternetCheckSettings(channel);
                this.PresenceRequest<T>(channel, 0, userCallback, false);
            }
        }

        /// <summary>
        /// PresenceRequest - private method for Presence
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="channel"></param>
        /// <param name="timetoken"></param>
        /// <param name="userCallback"></param>
        /// <param name="reconnect"></param>
        private void PresenceRequest<T>(string channel, object timetoken, Action<T> userCallback, bool reconnect)
        {
            //Exit if the channel is unsubscribed
            if (!_channelPresence.ContainsKey(channel))
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Due to Presence-unsubscribe, further presence was stopped for channel {1}", DateTime.Now.ToString(), channel.ToString()), LoggingMethod.LevelInfo);
                return;
            }
            _channelPresence.AddOrUpdate(channel, Convert.ToInt64(timetoken.ToString()), (key, oldValue) => Convert.ToInt64(timetoken.ToString())); //Store the timetoken

            if (_channelInternetStatus.ContainsKey(channel) && (!_channelInternetStatus[channel]) && _pubnetSystemActive)
            {
                if (_channelInternetRetry.ContainsKey(channel) && (_channelInternetRetry[channel] >= pubnubNetworkCheckRetries))
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Presence channel={1} - No internet connection. MAXed retries for internet ", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);
                    PresenceExceptionHandler<T>(channel, userCallback, true);
                    return;
                }

                if (overrideTcpKeepAlive)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Presence - No internet connection for {1}", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);

                    ReconnectState<T> netState = new ReconnectState<T>();
                    netState.channel = channel;
                    netState.type = ResponseType.Presence;
                    netState.callback = userCallback;
                    netState.timetoken = timetoken;

                    ReconnectNetwork<T>(netState);
                    return;
                }
            }

            // Begin recursive subscribe
            try
            {
                // Build URL
                Uri request = BuildPresenceRequest(channel, timetoken);

                // Wait for message
                UrlProcessRequest<T>(request, channel, ResponseType.Presence, userCallback, null, reconnect); // connectCallback = null
            }
            catch (Exception ex)
            {
                LoggingMethod.WriteToLog(string.Format("method:_presence \n channel={0} \n timetoken={1} \n Exception Details={2}", channel, timetoken.ToString(), ex.ToString()), LoggingMethod.LevelError);
                this.PresenceRequest<T>(channel, timetoken, userCallback, false);
            }
        }

        public void PresenceUnsubscribe(string channel, Action<object> userCallback)
        {
            PresenceUnsubscribe<object>(channel, userCallback);
        }

        public void PresenceUnsubscribe<T>(string channel, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }

            channel = string.Format("{0}-pnpres", channel);

            bool unsubscribeStatus = false;
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, requested presence-unsubscribe for channel={1}", DateTime.Now.ToString(), channel), LoggingMethod.LevelInfo);

            string jsonString = "";
            List<object> result = new List<object>();

            if (_channelPresence.ContainsKey(channel))
            {
                long unsubPreValue;
                unsubscribeStatus = _channelPresence.TryRemove(channel, out unsubPreValue);

                if (unsubscribeStatus)
                {
                    if (_channelRequest.ContainsKey(channel))
                    {
                        PubnubWebRequest storedRequest = _channelRequest[channel].request;
                        TerminateHeartbeatTimer(storedRequest.RequestUri);
                        storedRequest.Abort();
                    }
                    object presenceMsg = _presenceMessage.TryRemove(channel, out presenceMsg); //Clear the dictionary received for last presence msg
                    jsonString = string.Format("[1, \"Presence-Unsubscribed from {0}\"]", channel.Replace("-pnpres", ""));
                }
                else
                {
                    jsonString = string.Format("[1, \"Error presence-unsubscribing from {0}\"]", channel.Replace("-pnpres", ""));
                }


                result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                result.Add(channel.Replace("-pnpres", ""));

                LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON presence-unsubscribe response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                GoToCallback<T>(result, userCallback);
            }
            else
            {
                result = new List<object>();
                jsonString = "[0, \"Channel Not Subscribed\"]";
                result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                result.Add(channel.Replace("-pnpres", ""));
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON presence-unsubscribe response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                GoToCallback<T>(result, userCallback);
            }
        }

        public bool HereNow(string channel, Action<object> userCallback)
        {
            return HereNow<object>(channel, userCallback);
        }

        public bool HereNow<T>(string channel, Action<T> userCallback)
        {
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(channel.Trim()))
            {
                throw new ArgumentException("Missing Channel");
            }

            Uri request = BuildHereNowRequest(channel);

            return UrlProcessRequest<T>(request, channel, ResponseType.Here_Now, userCallback, null, false); // connectCallback = null
        }


        /// <summary>
        /// Time
        /// Timestamp from PubNub Cloud
        /// </summary>
        /// <param name="userCallback"></param>
        /// <returns></returns>
        public bool Time(Action<object> userCallback)
        {
            return Time<object>(userCallback);
        }

        public bool Time<T>(Action<T> userCallback)
        {
            Uri request = BuildTimeRequest();

            return UrlProcessRequest<T>(request, "", ResponseType.Time, userCallback, null, false); // connectCallback = null
        }

       /// <summary>
       /// Http Get Request process
       /// </summary>
       /// <param name="urlComponents"></param>
       /// <param name="type"></param>
       /// <returns></returns>
        private bool ProcessRequest(List<string> urlComponents, ResponseType type)
        {
            List<object> result = new List<object>();
            string channelName = GetChannelName(urlComponents, type);
            StringBuilder url = new StringBuilder();

            // Add Origin To The Request
            url.Append(this.origin);

            // Generate URL with UTF-8 Encoding
            foreach (string url_bit in urlComponents)
            {
                url.Append("/");
                url.Append(EncodeUricomponent(url_bit, type));
            }

            VerifyOrSetSessionUUID();
            if (type == ResponseType.Presence || type == ResponseType.Subscribe)
            {
                url.Append("?uuid=");
                url.Append(this.sessionUUID);
            }

            if (type == ResponseType.DetailedHistory)
                url.Append(parameters);

            Uri requestUri = new Uri(url.ToString());

#if ((!__MonoCS__) && (!SILVERLIGHT) && !WINDOWS_PHONE)
            // Force canonical path and query
            FieldInfo flagsFieldInfo = typeof(Uri).GetField("m_Flags", BindingFlags.Instance | BindingFlags.NonPublic);
            ulong flags = (ulong)flagsFieldInfo.GetValue(requestUri);
            flags &= ~((ulong)0x30); // Flags.PathNotCanonical|Flags.QueryNotCanonical
            flagsFieldInfo.SetValue(requestUri, flags);
#endif

            // Create Request
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUri);

            try
            {
                // Make request with the following inline Asynchronous callback
                IAsyncResult asyncResult = request.BeginGetResponse(new AsyncCallback((asynchronousResult) =>
                {
                    HttpWebRequest asyncWebRequest = (HttpWebRequest)asynchronousResult.AsyncState;
                    HttpWebResponse asyncWebResponse = (HttpWebResponse)asyncWebRequest.EndGetResponse(asynchronousResult);
                    using (StreamReader streamReader = new StreamReader(asyncWebResponse.GetResponseStream()))
                    {
                        // Deserialize the result
                        string jsonString = streamReader.ReadToEnd();
                        result = WrapResultBasedOnResponseType(type, jsonString, channelName, false);
                    }
                }), request

                );

                return true;
            }
            catch (System.Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return false;
            }
        }

        private void VerifyOrSetSessionUUID()
        {
            if (string.IsNullOrEmpty(this.sessionUUID) || string.IsNullOrEmpty(this.sessionUUID.Trim()))
            {
                this.sessionUUID = Guid.NewGuid().ToString();
            }
        }

        /// <summary>
        /// Http Get Request process
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="requestUri"></param>
        /// <param name="channelName"></param>
        /// <param name="type"></param>
        /// <param name="userCallback"></param>
        /// <param name="connectCallback"></param>
        /// <param name="reconnect"></param>
        /// <returns></returns>
        private bool UrlProcessRequest<T>(Uri requestUri, string channelName, ResponseType type, Action<T> userCallback, Action<T> connectCallback, bool reconnect)
        {
            List<object> result = new List<object>();

            try
            {
                // Create Request
                PubnubWebRequestCreator requestCreator = new PubnubWebRequestCreator(_pubnubUnitTest);
                PubnubWebRequest request = (PubnubWebRequest)requestCreator.Create(requestUri);

#if (!SILVERLIGHT && !WINDOWS_PHONE)
                if (pubnubEnableProxyConfig && _pubnubProxy != null)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, ProxyServer={1}; ProxyPort={2}; ProxyUserName={3}", DateTime.Now.ToString(), _pubnubProxy.ProxyServer, _pubnubProxy.ProxyPort, _pubnubProxy.ProxyUserName), LoggingMethod.LevelInfo);
                    WebProxy webProxy = new WebProxy(_pubnubProxy.ProxyServer, _pubnubProxy.ProxyPort);
                    webProxy.Credentials = new NetworkCredential(_pubnubProxy.ProxyUserName, _pubnubProxy.ProxyPassword);
                    request.Proxy = webProxy;
                }

                request.Timeout = GetTimeoutInSecondsForResponseType(type) * 1000;
#endif
                if ((!_channelSubscription.ContainsKey(channelName) && type == ResponseType.Subscribe)
                    || (!_channelPresence.ContainsKey(channelName) && type == ResponseType.Presence))
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, Due to Unsubscribe, request aborted for channel={1}", DateTime.Now.ToString(), channelName), LoggingMethod.LevelInfo);
                    request.Abort();
                }

                RequestState pubnubRequestState = new RequestState();
                pubnubRequestState.request = request;
                pubnubRequestState.channel = channelName;
                pubnubRequestState.type = type;

                if (type == ResponseType.Subscribe || type == ResponseType.Presence)
                {
                    _channelRequest.AddOrUpdate(channelName, pubnubRequestState, (key, oldState) => pubnubRequestState);
                }

                if (overrideTcpKeepAlive)
                {
                    //Eventhough heart-beat is disabled, run one time to check internet connection by setting dueTime=0
                    heartBeatTimer = new System.Threading.Timer(
                        new TimerCallback(OnPubnubHeartBeatTimeoutCallback), pubnubRequestState, 0,
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? Timeout.Infinite : pubnubNetworkTcpCheckIntervalInSeconds * 1000);
                    _channelHeartbeatTimer.AddOrUpdate(requestUri, heartBeatTimer, (key, oldState) => heartBeatTimer);
                }
                else
                {
#if ((!__MonoCS__) && (!SILVERLIGHT) && !WINDOWS_PHONE)
                    request.ServicePoint.SetTcpKeepAlive(true, pubnubNetworkTcpCheckIntervalInSeconds * 1000, 1000);
#endif
                }
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Request={1}", DateTime.Now.ToString(), requestUri.ToString()), LoggingMethod.LevelInfo);

                // Make request with the following inline Asynchronous callback
                IAsyncResult asyncResult = request.BeginGetResponse(new AsyncCallback((asynchronousResult) =>
                {
                    try
                    {
                        RequestState asynchRequestState = (RequestState)asynchronousResult.AsyncState;
                        PubnubWebRequest asyncWebRequest = (PubnubWebRequest)asynchRequestState.request;
                        if (asyncWebRequest != null)
                        {
                            using (PubnubWebResponse asyncWebResponse = (PubnubWebResponse)asyncWebRequest.EndGetResponse(asynchronousResult))
                            {
                                pubnubRequestState.response = asyncWebResponse;

                                using (StreamReader streamReader = new StreamReader(asyncWebResponse.GetResponseStream()))
                                {
                                    if (type == ResponseType.Subscribe || type == ResponseType.Presence)
                                    {
                                        _channelInternetStatus.AddOrUpdate(channelName, true, (key, oldValue) => true);
                                    }
                                    // Deserialize the result
                                    string jsonString = streamReader.ReadToEnd();
                                    streamReader.Close();

                                    if (overrideTcpKeepAlive)
                                    {
                                        TerminateHeartbeatTimer(requestUri);
                                    }

                                    LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON for channel={1} ({2}) ={3}", DateTime.Now.ToString(), channelName, type.ToString(), jsonString), LoggingMethod.LevelInfo);

                                    result = WrapResultBasedOnResponseType(type, jsonString, channelName, reconnect);
                                }
                                asyncWebResponse.Close();
                            }
                        }
                        else
                        {
                            LoggingMethod.WriteToLog(string.Format("DateTime {0}, Request aborted for channel={1}", DateTime.Now.ToString(), channelName), LoggingMethod.LevelInfo);
                        }

                        if (result != null && result.Count >= 1 && userCallback != null)
                        {
                            ResponseToConnectCallback<T>(result, type, channelName, connectCallback);
                            ResponseToUserCallback<T>(result, type, channelName, userCallback);
                        }

                        switch (type)
                        {
                            case ResponseType.Subscribe:
                                SubscribeInternalCallback<T>(result, userCallback, connectCallback);
                                break;
                            case ResponseType.Presence:
                                PresenceInternalCallback<T>(result, userCallback);
                                break;
                            default:
                                break;
                        }
                    }
                    catch (WebException webEx)
                    {
                        LoggingMethod.WriteToLog(string.Format("DateTime {0}, WebException: {1} for URL: {2}", DateTime.Now.ToString(), webEx.ToString(), requestUri.ToString()), LoggingMethod.LevelError);

                        if (overrideTcpKeepAlive)
                        {
                            TerminateHeartbeatTimer(requestUri);
                        }

                        RequestState state = (RequestState)asynchronousResult.AsyncState;

                        if (state.response != null)
                        {
                            state.response.Close();
                            state.request.Abort();
                        }

#if (!SILVERLIGHT)
                        if ((webEx.Status == WebExceptionStatus.NameResolutionFailure //No network
                            || webEx.Status == WebExceptionStatus.ConnectFailure //Sending Keep-alive packet failed (No network)/Server is down.
                            || webEx.Status == WebExceptionStatus.ServerProtocolViolation//Problem with proxy or ISP
                            || webEx.Status == WebExceptionStatus.ProtocolError
                            ) && (!overrideTcpKeepAlive))
                        {
                            //internet connection problem.
                            LoggingMethod.WriteToLog(string.Format("DateTime {0}, _urlRequest - Internet connection problem", DateTime.Now.ToString()), LoggingMethod.LevelError);

                            if (_channelInternetStatus.ContainsKey(channelName)
                                && (type == ResponseType.Subscribe || type == ResponseType.Presence))
                            {
                                if (_channelInternetStatus[channelName])
                                {
                                    //Reset Retry if previous state is true
                                    _channelInternetRetry.AddOrUpdate(channelName, 0, (key, oldValue) => 0);
                                }
                                else
                                {
                                    _channelInternetRetry.AddOrUpdate(channelName, 1, (key, oldValue) => oldValue + 1);
                                    LoggingMethod.WriteToLog(string.Format("DateTime {0} {1} channel = {2} _urlRequest - Internet connection retry {3} of {4}", DateTime.Now.ToString(), type, channelName, _channelInternetRetry[channelName], pubnubNetworkCheckRetries), LoggingMethod.LevelInfo);
                                }
                                _channelInternetStatus[channelName] = false;
                                Thread.Sleep(pubnubWebRequestRetryIntervalInSeconds * 1000);
                            }
                        }
#endif
                        UrlRequestCommonExceptionHandler<T>(type, channelName, state.timeout, userCallback, connectCallback);
                    }
                    catch (Exception ex)
                    {
                        RequestState state = (RequestState)asynchronousResult.AsyncState;
                        if (state.response != null)
                            state.response.Close();

                        LoggingMethod.WriteToLog(string.Format("DateTime {0} Exception= {1} for URL: {2}", DateTime.Now.ToString(), ex.ToString(), requestUri.ToString()), LoggingMethod.LevelError);
                        UrlRequestCommonExceptionHandler<T>(type, channelName, state.timeout, userCallback, connectCallback);
                    }

                }), pubnubRequestState);

#if (__MonoCS__)
                    if (!asyncResult.AsyncWaitHandle.WaitOne(GetTimeoutInSecondsForResponseType(type) * 1000))
                    {
                        OnPubnubWebRequestTimeout(pubnubRequestState, true);
                    }
#elif (SILVERLIGHT || WINDOWS_PHONE)
                Timer webRequestTimer = new Timer(OnPubnubWebRequestTimeout, pubnubRequestState, GetTimeoutInSecondsForResponseType(type) * 1000, Timeout.Infinite);
#else
                ThreadPool.RegisterWaitForSingleObject(asyncResult.AsyncWaitHandle, new WaitOrTimerCallback(OnPubnubWebRequestTimeout), pubnubRequestState, GetTimeoutInSecondsForResponseType(type) * 1000, true);
#endif
                return true;
            }
            catch (System.Exception ex)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0} Exception={1}", DateTime.Now.ToString(), ex.ToString()), LoggingMethod.LevelError);
                UrlRequestCommonExceptionHandler<T>(type, channelName, false, userCallback, connectCallback);
                return false;
            }
        }

        private int GetTimeoutInSecondsForResponseType(ResponseType type)
        {
            int timeout;
            if (type == ResponseType.Subscribe || type == ResponseType.Presence)
            {
                timeout = pubnubWebRequestCallbackIntervalInSeconds;
            }
            else
            {
                timeout = pubnubOperationTimeoutIntervalInSeconds;
            }
            return timeout;
        }
        
        private void TerminateHeartbeatTimer()
        {
            TerminateHeartbeatTimer(null);
        }

        private void TerminateHeartbeatTimer(Uri requestUri)
        {
            if (requestUri != null)
            {
                if (_channelHeartbeatTimer.ContainsKey(requestUri))
                {
                    Timer requestHeatbeatTimer = _channelHeartbeatTimer[requestUri];
                    requestHeatbeatTimer.Change(
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? -1 : pubnubNetworkTcpCheckIntervalInSeconds * 1000,
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? -1 : pubnubNetworkTcpCheckIntervalInSeconds * 1000);
                    requestHeatbeatTimer.Dispose();
                    Timer removedTimer = null;
                    bool removed = _channelHeartbeatTimer.TryRemove(requestUri, out removedTimer);
                    if (!removed)
                    {
                        LoggingMethod.WriteToLog(string.Format("DateTime {0} Unable to remove heartbeat reference from collection for {1}", DateTime.Now.ToString(), requestUri.ToString()), LoggingMethod.LevelInfo);
                    }
                }
            }
            else
            {
                ConcurrentDictionary<Uri, Timer> timerCollection = _channelHeartbeatTimer;
                ICollection<Uri> keyCollection = timerCollection.Keys;
                foreach (Uri key in keyCollection)
                {
                    if (_channelHeartbeatTimer.ContainsKey(key))
                    {
                        Timer currentTimer = _channelHeartbeatTimer[key];
                        currentTimer.Dispose();
                        Timer removedTimer = null;
                        bool removed = _channelHeartbeatTimer.TryRemove(key, out removedTimer);
                        if (!removed)
                        {
                            LoggingMethod.WriteToLog(string.Format("DateTime {0} TerminateHeartbeatTimer(null) - Unable to remove heartbeat reference from collection for {1}", DateTime.Now.ToString(), key.ToString()), LoggingMethod.LevelInfo);
                        }
                    }
                }
            }
        }

        private void TerminateReconnectTimer()
        {
            TerminateReconnectTimer(null);
        }

        private void TerminateReconnectTimer(string channelName)
        {
            if (channelName != null)
            {
                if (_channelReconnectTimer.ContainsKey(channelName))
                {
                    Timer channelReconnectTimer = _channelReconnectTimer[channelName];
                    channelReconnectTimer.Change(
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? -1 : pubnubNetworkTcpCheckIntervalInSeconds * 1000,
                        (-1 == pubnubNetworkTcpCheckIntervalInSeconds) ? -1 : pubnubNetworkTcpCheckIntervalInSeconds * 1000);
                    channelReconnectTimer.Dispose();
                    Timer removedTimer = null;
                    bool removed = _channelReconnectTimer.TryRemove(channelName, out removedTimer);
                    if (!removed)
                    {
                        LoggingMethod.WriteToLog(string.Format("DateTime {0} TerminateReconnectTimer - Unable to remove reconnect timer reference from collection for {1}", DateTime.Now.ToString(), channelName), LoggingMethod.LevelInfo);
                    }
                }
            }
            else
            {
                ConcurrentDictionary<string, Timer> reconnectCollection = _channelReconnectTimer;
                ICollection<string> keyCollection = reconnectCollection.Keys;
                foreach (string key in keyCollection)
                {
                    if (_channelReconnectTimer.ContainsKey(key))
                    {
                        Timer currentTimer = _channelReconnectTimer[key];
                        currentTimer.Dispose();
                        Timer removedTimer = null;
                        bool removed = _channelReconnectTimer.TryRemove(key, out removedTimer);
                        if (!removed)
                        {
                            LoggingMethod.WriteToLog(string.Format("DateTime {0} TerminateReconnectTimer(null) - Unable to remove reconnect timer reference from collection for {1}", DateTime.Now.ToString(), key.ToString()), LoggingMethod.LevelInfo);
                        }
                    }
                }
            }
        }

        private void ResponseToConnectCallback<T>(List<object> result, ResponseType type, string channelName, Action<T> connectCallback)
        {
            //Check callback exists and make sure previous timetoken = 0
            if (connectCallback != null
                && _channelSubscription.ContainsKey(channelName)
                && _channelSubscription[channelName].Equals(0))
            {
                switch (type)
                {
                    case ResponseType.Subscribe:
                        string jsonString = "";
                        List<object> connectResult = new List<object>();
                        jsonString = string.Format("[1, \"Connected\"]");
                        connectResult = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                        connectResult.Add(channelName);
                        GoToCallback<T>(connectResult, connectCallback);
                        break;
                    default:
                        break;
                }
            }

        }

        public Uri BuildTimeRequest()
        {
            List<string> url = new List<string>();

            url.Add("time");
            url.Add("0");

            return BuildRestApiRequest<Uri>(url, ResponseType.Time);

        }

        private Uri BuildLeaveRequest(string channel)
        {
            List<string> url = new List<string>();

            url.Add("v2");
            url.Add("presence");
            url.Add("sub_key");
            url.Add(this.subscribeKey);
            url.Add("channel");
            url.Add(channel);
            url.Add("leave");

            return BuildRestApiRequest<Uri>(url, ResponseType.Leave);
        }

        private Uri BuildHereNowRequest(string channel)
        {
            List<string> url = new List<string>();

            url.Add("v2");
            url.Add("presence");
            url.Add("sub_key");
            url.Add(this.subscribeKey);
            url.Add("channel");
            url.Add(channel);

            return BuildRestApiRequest<Uri>(url, ResponseType.Here_Now);
        }

        private Uri BuildDetailedHistoryRequest(string channel, long start, long end, int count, bool reverse)
        {
            parameters = "";
            if (count <= -1) count = 100;
            parameters = "?count=" + count;
            if (reverse)
                parameters = parameters + "&" + "reverse=" + reverse.ToString().ToLower();
            if (start != -1)
                parameters = parameters + "&" + "start=" + start.ToString().ToLower();
            if (end != -1)
                parameters = parameters + "&" + "end=" + end.ToString().ToLower();

            List<string> url = new List<string>();

            url.Add("v2");
            url.Add("history");
            url.Add("sub-key");
            url.Add(this.subscribeKey);
            url.Add("channel");
            url.Add(channel);

            return BuildRestApiRequest<Uri>(url, ResponseType.DetailedHistory);
        }

        private Uri BuildSubscribeRequest(string channel, object timetoken)
        {
            List<string> url = new List<string>();
            url.Add("subscribe");
            url.Add(this.subscribeKey);
            url.Add(channel);
            url.Add("0");
            url.Add(timetoken.ToString());

            return BuildRestApiRequest<Uri>(url, ResponseType.Subscribe);
        }

        private Uri BuildPresenceRequest(string channel, object timetoken)
        {
            List<string> url = new List<string>();
            url.Add("subscribe");
            url.Add(this.subscribeKey);
            url.Add(channel);
            url.Add("0");
            url.Add(timetoken.ToString());

            return BuildRestApiRequest<Uri>(url, ResponseType.Presence);
        }

        private Uri BuildPublishRequest(string channel, object originalMessage)
        {
            string message = JsonEncodePublishMsg(originalMessage);

            // Generate String to Sign
            string signature = "0";
            if (this.secretKey.Length > 0)
            {
                StringBuilder string_to_sign = new StringBuilder();
                string_to_sign
                    .Append(this.publishKey)
                    .Append('/')
                    .Append(this.subscribeKey)
                    .Append('/')
                    .Append(this.secretKey)
                    .Append('/')
                    .Append(channel)
                    .Append('/')
                    .Append(message); // 1

                // Sign Message
                signature = Md5(string_to_sign.ToString());
            }

            // Build URL
            List<string> url = new List<string>();
            url.Add("publish");
            url.Add(this.publishKey);
            url.Add(this.subscribeKey);
            url.Add(signature);
            url.Add(channel);
            url.Add("0");
            url.Add(message);

            return BuildRestApiRequest<Uri>(url, ResponseType.Publish);
        }

        private Uri BuildRestApiRequest<T>(List<string> urlComponents, ResponseType type)
        {
            StringBuilder url = new StringBuilder();

            // Add Origin To The Request
            url.Append(this.origin);

            // Generate URL with UTF-8 Encoding
            foreach (string urlBit in urlComponents)
            {
                url.Append("/");
                url.Append(EncodeUricomponent(urlBit, type));
            }

            VerifyOrSetSessionUUID();
            if (type == ResponseType.Presence || type == ResponseType.Subscribe || type == ResponseType.Leave)
            {
                url.Append("?uuid=");
                url.Append(this.sessionUUID);
            }

            if (type == ResponseType.DetailedHistory)
                url.Append(parameters);

#if (WINDOWS_PHONE)
            if (type == ResponseType.Presence || type == ResponseType.Subscribe || type == ResponseType.Leave || type == ResponseType.DetailedHistory)
            {
                url.Append("&nocache=");
                url.Append(Guid.NewGuid().ToString());
            }
            else
            {
                url.Append("?nocache=");
                url.Append(Guid.NewGuid().ToString());
            }
#endif

            Uri requestUri = new Uri(url.ToString());

#if ((!__MonoCS__) && (!SILVERLIGHT) && (!WINDOWS_PHONE))
            if ((type == ResponseType.Publish || type == ResponseType.Subscribe || type == ResponseType.Presence))
            {
                // Force canonical path and query
                string paq = requestUri.PathAndQuery;
                FieldInfo flagsFieldInfo = typeof(Uri).GetField("m_Flags", BindingFlags.Instance | BindingFlags.NonPublic);
                ulong flags = (ulong)flagsFieldInfo.GetValue(requestUri);
                flags &= ~((ulong)0x30); // Flags.PathNotCanonical|Flags.QueryNotCanonical
                flagsFieldInfo.SetValue(requestUri, flags);
            }
#endif

            return requestUri;
        }

        void OnPubnubWebRequestTimeout(Object requestState)
        {
            RequestState currentState = requestState as RequestState;
            if (currentState != null && currentState.response == null && currentState.request != null)
            {
                currentState.timeout = true;
                currentState.request.Abort();
                LoggingMethod.WriteToLog(string.Format("DateTime: {0}, **WP7 OnPubnubWebRequestTimeout**", DateTime.Now.ToString()), LoggingMethod.LevelError);
            }
        }

        private void UrlRequestCommonExceptionHandler<T>(ResponseType type, string channelName, bool requestTimeout, Action<T> userCallback, Action<T> connectCallback)
        {
            if (type == ResponseType.Subscribe)
            {
                SubscribeExceptionHandler<T>(channelName, userCallback, connectCallback, false);
            }
            else if (type == ResponseType.Presence)
            {
                PresenceExceptionHandler<T>(channelName, userCallback, false);
            }
            else if (type == ResponseType.Publish)
            {
                PublishExceptionHandler<T>(channelName, requestTimeout, userCallback);
            }
            else if (type == ResponseType.Here_Now)
            {
                HereNowExceptionHandler<T>(channelName, requestTimeout, userCallback);
            }
            else if (type == ResponseType.DetailedHistory)
            {
                DetailedHistoryExceptionHandler<T>(channelName, requestTimeout, userCallback);
            }
            else if (type == ResponseType.Time)
            {
                TimeExceptionHandler<T>(requestTimeout,userCallback);
            }
            else if (type == ResponseType.Leave)
            {
                //no action at this time
            }
        }

        private void ResponseToUserCallback<T>(List<object> result, ResponseType type, string channelName, Action<T> userCallback)
        {
            switch (type)
            {
                case ResponseType.Subscribe:
                    var messages = (from item in result
                                select item as object).ToArray();
                    if (messages != null && messages.Length > 0)
                    {
                        List<object> messageList = messages[0] as List<object>;
                        if (messageList != null && messageList.Count > 0)
                        {
                            foreach (object item in messageList)
                            {
                                List<object> itemMessage = new List<object>();
                                itemMessage.Add(item);
                                for (int index = 1; index < messages.Length; index++)
                                {
                                    itemMessage.Add(messages[index]);
                                }
                                GoToCallback<T>(itemMessage, userCallback);
                            }
                        }
                    }
                    RemoveChannelRequest(channelName);
                    break;
                case ResponseType.Presence:
                    var presenceMessages = (from item in result
                                select item as object).ToArray();
                    if (presenceMessages != null && presenceMessages.Length > 0)
                    {
                        JArray messageArrary = presenceMessages[0] as JArray;
                        if (messageArrary != null && messageArrary.Count > 0)
                        {
                            foreach (object item in messageArrary)
                            {
                                List<object> itemMessage = new List<object>();
                                itemMessage.Add(item);
                                for (int index = 1; index < presenceMessages.Length; index++)
                                {
                                    if (index == 2)
                                    {
                                        presenceMessages[index] = ((string)presenceMessages[index]).Replace("-pnpres", "");
                                    }
                                    itemMessage.Add(presenceMessages[index]);
                                }
                                GoToCallback<T>(itemMessage, userCallback);
                            }
                        }
                    }
                    RemoveChannelRequest(channelName);
                    break;
                case ResponseType.Publish:
                    if (result != null && result.Count > 0)
                    {
                        GoToCallback<T>(result, userCallback);
                    }
                    break;
                case ResponseType.DetailedHistory:
                    if (result != null && result.Count > 0)
                    {
                        GoToCallback<T>(result, userCallback);
                    }
                    break;
                case ResponseType.Here_Now:
                    if (result != null && result.Count > 0)
                    {
                        GoToCallback<T>(result, userCallback);
                    }
                    break;
                case ResponseType.Time:
                    if (result != null && result.Count > 0)
                    {
                        GoToCallback<T>(result, userCallback);
                    }
                    break;
                case ResponseType.Leave:
                    //No response to callback
                    break;
                default:
                    break;
            }
        }

        private void JsonResponseToCallback<T>(List<object> result, Action<T> callback)
        {
            string callbackJson = "";

            if (typeof(T) == typeof(string))
            {
                callbackJson = JsonConvert.SerializeObject(result);

                Action<string> castCallback = callback as Action<string>;
                castCallback(callbackJson);
            }
        }

        private void RemoveChannelRequest(string channelName)
        {
            if (_channelRequest.ContainsKey(channelName))
            {
                RequestState currentRequest = _channelRequest[channelName];
                currentRequest.request = null;
                currentRequest.response = null;
                bool remove = _channelRequest.TryRemove(channelName, out currentRequest);
                if (!remove)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0} Unable to remove request from dictionary for channel ={1}", DateTime.Now.ToString(), channelName), LoggingMethod.LevelError);
                }
            }
        }

        private void SubscribeExceptionHandler<T>(string channelName, Action<T> userCallback, Action<T> connectCallback, bool reconnectTried)
        {
            if (reconnectTried)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, MAX retries reached. Exiting the subscribe for channel = {1}", DateTime.Now.ToString(), channelName), LoggingMethod.LevelInfo);

                Unsubscribe(channelName, null);

                List<object> errorResult = new List<object>();
                string jsonString = string.Format("[0, \"Unsubscribed after {0} failed retries\"]", pubnubNetworkCheckRetries);
                errorResult = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                errorResult.Add(channelName);

                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Subscribe JSON network error response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);

                if (userCallback != null)
                {
                    GoToCallback<T>(errorResult, userCallback);
                }
            }
            else
            {
                List<object> result = new List<object>();
                result.Add("0");
                List<object> lastResult = null; 
                if (_subscribeMessage.ContainsKey(channelName))
                {
                    lastResult = _subscribeMessage[channelName] as List<object>;
                }
                result.Add((lastResult != null) ? lastResult[1] : "0"); //get last timetoken
                result.Add(channelName); //send channel name

                SubscribeInternalCallback<T>(result, userCallback, connectCallback);
            }
        }

        private void PresenceExceptionHandler<T>(string channelName, Action<T> userCallback, bool reconnectTry)
        {
            if (reconnectTry)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, MAX retries reached. Exiting the presence for channel = {1}", DateTime.Now.ToString(), channelName), LoggingMethod.LevelInfo);

                PresenceUnsubscribe(channelName.Replace("-pnpres", ""), null);

                List<object> errorResult = new List<object>();
                string jsonString = string.Format("[0, \"Presence-unsubscribed after {0} failed retries\"]", pubnubNetworkCheckRetries);
                errorResult = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
                errorResult.Add(channelName.Replace("-pnpres", ""));
                LoggingMethod.WriteToLog(string.Format("DateTime {0}, Presence JSON network error response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
                if (userCallback != null)
                {
                    GoToCallback<T>(errorResult, userCallback);
                }
            }
            else
            {
                List<object> result = new List<object>();
                result.Add("0");
                if (_presenceMessage.ContainsKey(channelName))
                {
                    List<object> lastResult = _presenceMessage[channelName] as List<object>;
                    result.Add((lastResult != null) ? lastResult[1] : "0"); //get last timetoken
                }
                else
                {
                    result.Add("0"); //timetoken
                }
                result.Add(channelName); //send channel name

                PresenceInternalCallback<T>(result, userCallback);
            }
        }

        private void PublishExceptionHandler<T>(string channelName, bool requestTimeout, Action<T> userCallback)
        {
            List<object> result = new List<object>();
            string jsonString = (requestTimeout) ? "[0, \"Operation Timeout\"]" : "[0, \"Network connnect error\"]";
            result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
            result.Add(channelName);
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON publish response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
            GoToCallback<T>(result, userCallback);
        }

        private void HereNowExceptionHandler<T>(string channelName, bool requestTimeout, Action<T> userCallback)
        {
            List<object> result = new List<object>();
            string jsonString = (requestTimeout) ? "[0, \"Operation Timeout\"]" : "[0, \"Network connnect error\"]";
            result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
            result.Add(channelName);
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON here_now response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
            GoToCallback<T>(result, userCallback);
        }

        private void DetailedHistoryExceptionHandler<T>(string channelName, bool requestTimeout, Action<T> userCallback)
        {
            List<object> result = new List<object>();
            string jsonString = (requestTimeout) ? "[0, \"Operation Timeout\"]" : "[0, \"Network connnect error\"]";
            result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
            result.Add(channelName);
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON detailedHistoryExceptionHandler response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
            GoToCallback<T>(result, userCallback);
        }

        private void TimeExceptionHandler<T>(bool requestTimeout, Action<T> userCallback)
        {
            List<object> result = new List<object>();
            string jsonString = (requestTimeout) ? "[0, \"Operation Timeout\"]" : "[0, \"Network connnect error\"]";
            result = (List<object>)JsonConvert.DeserializeObject<List<object>>(jsonString);
            LoggingMethod.WriteToLog(string.Format("DateTime {0}, JSON timeExceptionHandler response={1}", DateTime.Now.ToString(), jsonString), LoggingMethod.LevelInfo);
            GoToCallback<T>(result, userCallback);
        }

        /// <summary>
        /// Gets the result by wrapping the json response based on the request
        /// </summary>
        /// <param name="type"></param>
        /// <param name="jsonString"></param>
        /// <param name="channelName"></param>
        /// <param name="reconnect"></param>
        /// <returns></returns>
        private List<object> WrapResultBasedOnResponseType(ResponseType type, string jsonString, string channelName, bool reconnect)
        {
            List<object> result = new List<object>();

            object deSerializedResult = JsonConvert.DeserializeObject<object>(jsonString);
            List<object> result1 = ((IEnumerable)deSerializedResult).Cast<object>().ToList();

            if (result1 != null && result1.Count > 0)
            {
                result = DecodeMessage(result1, type);
            }


            switch (type)
            {
                case ResponseType.Publish:
                    result.Add(channelName);
                    break;
                case ResponseType.History:
                    if (this.cipherKey.Length > 0)
                    {
                        List<object> historyDecrypted = new List<object>();
                        PubnubCrypto aes = new PubnubCrypto(this.cipherKey);
                        foreach (object message in result)
                        {
                            historyDecrypted.Add(aes.Decrypt(message.ToString()));
                        }
                        History = historyDecrypted;
                    }
                    else
                    {
                        History = result;
                    }
                    break;
                case ResponseType.DetailedHistory:
                    result.Add(channelName);
                    break;
                case ResponseType.Here_Now:
                    Dictionary<string, object> dictionary = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);
                    result = new List<object>();
                    result.Add(dictionary);
                    result.Add(channelName);
                    break;
                case ResponseType.Time:
                    _time = result;
                    break;
                case ResponseType.Subscribe:
                    result.Add(channelName);
                    _subscribeMessage.AddOrUpdate(channelName, result, (key, oldValue) =>
                    {
                        if (reconnect)
                        {
                            List<object> oldResult = oldValue as List<object>;
                            if (oldResult != null)
                            {
                                result[1] = oldResult[1];
                            }
                            return result;
                        }
                        else
                        {
                            return result;
                        }
                    });
                    break;
                case ResponseType.Presence:
                    result.Add(channelName);
                    _presenceMessage.AddOrUpdate(channelName, result, (key, oldValue) =>
                    {
                        if (reconnect)
                        {
                            List<object> oldResult = oldValue as List<object>;
                            if (oldResult != null)
                            {
                                result[1] = oldResult[1];
                            }
                            return result;
                        }
                        else
                        {
                            return result;
                        }
                    });

                    break;
                case ResponseType.Leave:
                    result.Add(channelName);
                    break;
                default:
                    break;
            };//switch stmt end

            return result;
        }

        private void GoToCallback<T>(List<object> result, Action<T> Callback)
        {
            if (Callback != null)
            {
                if (typeof(T) == typeof(string))
                {
                    JsonResponseToCallback(result, Callback);
                }
                else
                {
                    Callback((T)(IList)result.AsReadOnly());
                }
            }
        }

        /// <summary>
        /// Retrieves the channel name from the url components
        /// </summary>
        /// <param name="urlComponents"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        private string GetChannelName(List<string> urlComponents, ResponseType type)
        {
            string channelName = "";
            switch (type)
            {
                case ResponseType.Subscribe:
                    channelName = urlComponents[2];
                    break;
                case ResponseType.Publish:
                    channelName = urlComponents[4];
                    break;
                case ResponseType.Presence:
                    channelName = urlComponents[2];
                    break;
                case ResponseType.DetailedHistory:
                    channelName = urlComponents[5];
                    break;
                case ResponseType.Here_Now:
                    channelName = urlComponents[5];
                    break;
                case ResponseType.Leave:
                    channelName = urlComponents[5];
                    break;
                default:
                    break;
            };
            return channelName;
        }

        /// <summary>
        /// Serialize the given object into JSON string
        /// </summary>
        /// <param name="objectToSerialize"></param>
        /// <returns></returns>
        public static string SerializeToJsonString(object objectToSerialize)
        {
            return JsonConvert.SerializeObject(objectToSerialize);
        }

        /// <summary>
        /// Deserialize JSON string into List of Objects
        /// </summary>
        /// <param name="jsonString"></param>
        /// <returns></returns>
        public static List<object> DeserializeToListOfObject(string jsonString)
        {
            return JsonConvert.DeserializeObject<List<object>>(jsonString);
        }

        private string EncodeUricomponent(string s, ResponseType type)
        {
            string encodedUri = "";
            StringBuilder o = new StringBuilder();
            foreach (char ch in s.ToCharArray())
            {
                if (IsUnsafe(ch))
                {
                    o.Append('%');
                    o.Append(ToHex(ch / 16));
                    o.Append(ToHex(ch % 16));
                }
                else o.Append(ch);
            }
            encodedUri = o.ToString();
            if (type == ResponseType.Here_Now || type == ResponseType.DetailedHistory || type == ResponseType.Leave)
            {
                encodedUri = encodedUri.Replace("%2F", "%252F");
            }

            return encodedUri;
        }

        private char ToHex(int ch)
        {
            return (char)(ch < 10 ? '0' + ch : 'A' + ch - 10);
        }

        private bool IsUnsafe(char ch)
        {
            return " ~`!@#$%^&*()+=[]\\{}|;':\",./<>?".IndexOf(ch) >= 0;
        }


        public Guid GenerateGuid()
        {
            return Guid.NewGuid();
        }

        private static string Md5(string text)
        {
            MD5 md5 = new MD5CryptoServiceProvider();
            byte[] data = Encoding.Unicode.GetBytes(text);
            byte[] hash = md5.ComputeHash(data);
            string hexaHash = "";
            foreach (byte b in hash) hexaHash += String.Format("{0:x2}", b);
            return hexaHash;
        }

        /// <summary>
        /// Convert the UTC/GMT DateTime to Unix Nano Seconds format
        /// </summary>
        /// <param name="dotNetUTCDateTime"></param>
        /// <returns></returns>
        public static long TranslateDateTimeToPubnubUnixNanoSeconds(DateTime dotNetUTCDateTime)
        {
            TimeSpan timeSpan = dotNetUTCDateTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long timeStamp = Convert.ToInt64(timeSpan.TotalSeconds) * 10000000;
            return timeStamp;
        }

        /// <summary>
        /// Convert the Unix Nano Seconds format time to UTC/GMT DateTime
        /// </summary>
        /// <param name="unixNanoSecondTime"></param>
        /// <returns></returns>
        public static DateTime TranslatePubnubUnixNanoSecondsToDateTime(long unixNanoSecondTime)
        {
            double timeStamp = unixNanoSecondTime / 10000000;
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timeStamp);
            return dateTime;
        }

        public void EndPendingRequests()
        {
            TerminatePendingWebRequest();
            TerminateHeartbeatTimer();
            TerminateReconnectTimer();
        }
    }

    /// <summary>
    /// MD5 Service provider
    /// </summary>
    internal class MD5CryptoServiceProvider : MD5
    {
        public MD5CryptoServiceProvider()
            : base()
        {
        }
    }
    /// <summary>
    /// MD5 messaging-digest algorithm is a widely used cryptographic hash function that produces 128-bit hash value.
    /// </summary>
    internal class MD5 : IDisposable
    {
        static public MD5 Create(string hashName)
        {
            if (hashName == "MD5")
                return new MD5();
            else
                throw new NotSupportedException();
        }

        static public String GetMd5String(String source)
        {
            MD5 md = MD5CryptoServiceProvider.Create();
            byte[] hash;

            //Create a new instance of ASCIIEncoding to 
            //convert the string into an array of Unicode bytes.
            UTF8Encoding enc = new UTF8Encoding();

            //Convert the string into an array of bytes.
            byte[] buffer = enc.GetBytes(source);

            //Create the hash value from the array of bytes.
            hash = md.ComputeHash(buffer);

            StringBuilder sb = new StringBuilder();
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        static public MD5 Create()
        {
            return new MD5();
        }

        #region base implementation of the MD5
        #region constants
        private const byte S11 = 7;
        private const byte S12 = 12;
        private const byte S13 = 17;
        private const byte S14 = 22;
        private const byte S21 = 5;
        private const byte S22 = 9;
        private const byte S23 = 14;
        private const byte S24 = 20;
        private const byte S31 = 4;
        private const byte S32 = 11;
        private const byte S33 = 16;
        private const byte S34 = 23;
        private const byte S41 = 6;
        private const byte S42 = 10;
        private const byte S43 = 15;
        private const byte S44 = 21;
        static private byte[] PADDING = new byte[] {
														0x80, 0, 0, 0, 0, 0, 
0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
														0, 0, 0, 0, 0, 0, 0, 
0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
														0, 0, 0, 0, 0, 0, 0, 
0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
													};
        #endregion

        #region F, G, H and I are basic MD5 functions.
        static private uint F(uint x, uint y, uint z)
        {
            return (((x) & (y)) | ((~x) & (z)));
        }
        static private uint G(uint x, uint y, uint z)
        {
            return (((x) & (z)) | ((y) & (~z)));
        }
        static private uint H(uint x, uint y, uint z)
        {
            return ((x) ^ (y) ^ (z));
        }
        static private uint I(uint x, uint y, uint z)
        {
            return ((y) ^ ((x) | (~z)));
        }
        #endregion

        #region rotates x left n bits.
        /// <summary>
        /// rotates x left n bits.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="n"></param>
        /// <returns></returns>
        static private uint ROTATE_LEFT(uint x, byte n)
        {
            return (((x) << (n)) | ((x) >> (32 - (n))));
        }
        #endregion

        #region FF, GG, HH, and II transformations
        /// FF, GG, HH, and II transformations 
        /// for rounds 1, 2, 3, and 4.
        /// Rotation is separate from addition to prevent re-computation.
        static private void FF(ref uint a, uint b, uint c, uint d, uint x, byte s, uint ac)
        {
            (a) += F((b), (c), (d)) + (x) + (uint)(ac);
            (a) = ROTATE_LEFT((a), (s));
            (a) += (b);
        }
        static private void GG(ref uint a, uint b, uint c, uint d, uint x, byte s, uint ac)
        {
            (a) += G((b), (c), (d)) + (x) + (uint)(ac);
            (a) = ROTATE_LEFT((a), (s));
            (a) += (b);
        }
        static private void HH(ref uint a, uint b, uint c, uint d, uint x, byte s, uint ac)
        {
            (a) += H((b), (c), (d)) + (x) + (uint)(ac);
            (a) = ROTATE_LEFT((a), (s));
            (a) += (b);
        }
        static private void II(ref uint a, uint b, uint c, uint d, uint x, byte s, uint ac)
        {
            (a) += I((b), (c), (d)) + (x) + (uint)(ac);
            (a) = ROTATE_LEFT((a), (s));
            (a) += (b);
        }
        #endregion

        #region context info
        /// <summary>
        /// state (ABCD)
        /// </summary>
        uint[] state = new uint[4];

        /// <summary>
        /// number of bits, modulo 2^64 (LSB first)
        /// </summary>
        uint[] count = new uint[2];

        /// <summary>
        /// input buffer
        /// </summary>
        byte[] buffer = new byte[64];
        #endregion

        internal MD5()
        {
            Initialize();
        }

        /// <summary>
        /// MD5 initialization. Begins an MD5 operation, writing a new context.
        /// </summary>
        /// <remarks>
        /// The RFC named it "MD5Init"
        /// </remarks>
        public virtual void Initialize()
        {
            count[0] = count[1] = 0;

            // Load magic initialization constants.
            state[0] = 0x67452301;
            state[1] = 0xefcdab89;
            state[2] = 0x98badcfe;
            state[3] = 0x10325476;
        }

        /// <summary>
        /// MD5 block update operation. Continues an MD5 message-digest
        /// operation, processing another message block, and updating the
        /// context.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <remarks>The RFC Named it MD5Update</remarks>
        protected virtual void HashCore(byte[] input, int offset, int count)
        {
            int i;
            int index;
            int partLen;

            // Compute number of bytes mod 64
            index = (int)((this.count[0] >> 3) & 0x3F);

            // Update number of bits
            if ((this.count[0] += (uint)((uint)count << 3)) < ((uint)count << 3))
                this.count[1]++;
            this.count[1] += ((uint)count >> 29);

            partLen = 64 - index;

            // Transform as many times as possible.
            if (count >= partLen)
            {
                Buffer.BlockCopy(input, offset, this.buffer, index, partLen);
                Transform(this.buffer, 0);

                for (i = partLen; i + 63 < count; i += 64)
                    Transform(input, offset + i);

                index = 0;
            }
            else
                i = 0;

            // Buffer remaining input 
            Buffer.BlockCopy(input, offset + i, this.buffer, index, count - i);
        }

        /// <summary>
        /// MD5 finalization. Ends an MD5 message-digest operation, writing the
        /// the message digest and zeroizing the context.
        /// </summary>
        /// <returns>message digest</returns>
        /// <remarks>The RFC named it MD5Final</remarks>
        protected virtual byte[] HashFinal()
        {
            byte[] digest = new byte[16];
            byte[] bits = new byte[8];
            int index, padLen;

            // Save number of bits
            Encode(bits, 0, this.count, 0, 8);

            // Pad out to 56 mod 64.
            index = (int)((uint)(this.count[0] >> 3) & 0x3f);
            padLen = (index < 56) ? (56 - index) : (120 - index);
            HashCore(PADDING, 0, padLen);

            // Append length (before padding)
            HashCore(bits, 0, 8);

            // Store state in digest 
            Encode(digest, 0, state, 0, 16);

            // Zeroize sensitive information.
            count[0] = count[1] = 0;
            state[0] = 0;
            state[1] = 0;
            state[2] = 0;
            state[3] = 0;

            // initialize again, to be ready to use
            Initialize();

            return digest;
        }

        /// <summary>
        /// MD5 basic transformation. Transforms state based on 64 bytes block.
        /// </summary>
        /// <param name="block"></param>
        /// <param name="offset"></param>
        private void Transform(byte[] block, int offset)
        {
            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint[] x = new uint[16];
            Decode(x, 0, block, offset, 64);

            // Round 1
            FF(ref a, b, c, d, x[0], S11, 0xd76aa478); /* 1 */
            FF(ref d, a, b, c, x[1], S12, 0xe8c7b756); /* 2 */
            FF(ref c, d, a, b, x[2], S13, 0x242070db); /* 3 */
            FF(ref b, c, d, a, x[3], S14, 0xc1bdceee); /* 4 */
            FF(ref a, b, c, d, x[4], S11, 0xf57c0faf); /* 5 */
            FF(ref d, a, b, c, x[5], S12, 0x4787c62a); /* 6 */
            FF(ref c, d, a, b, x[6], S13, 0xa8304613); /* 7 */
            FF(ref b, c, d, a, x[7], S14, 0xfd469501); /* 8 */
            FF(ref a, b, c, d, x[8], S11, 0x698098d8); /* 9 */
            FF(ref d, a, b, c, x[9], S12, 0x8b44f7af); /* 10 */
            FF(ref c, d, a, b, x[10], S13, 0xffff5bb1); /* 11 */
            FF(ref b, c, d, a, x[11], S14, 0x895cd7be); /* 12 */
            FF(ref a, b, c, d, x[12], S11, 0x6b901122); /* 13 */
            FF(ref d, a, b, c, x[13], S12, 0xfd987193); /* 14 */
            FF(ref c, d, a, b, x[14], S13, 0xa679438e); /* 15 */
            FF(ref b, c, d, a, x[15], S14, 0x49b40821); /* 16 */

            // Round 2
            GG(ref a, b, c, d, x[1], S21, 0xf61e2562); /* 17 */
            GG(ref d, a, b, c, x[6], S22, 0xc040b340); /* 18 */
            GG(ref c, d, a, b, x[11], S23, 0x265e5a51); /* 19 */
            GG(ref b, c, d, a, x[0], S24, 0xe9b6c7aa); /* 20 */
            GG(ref a, b, c, d, x[5], S21, 0xd62f105d); /* 21 */
            GG(ref d, a, b, c, x[10], S22, 0x2441453); /* 22 */
            GG(ref c, d, a, b, x[15], S23, 0xd8a1e681); /* 23 */
            GG(ref b, c, d, a, x[4], S24, 0xe7d3fbc8); /* 24 */
            GG(ref a, b, c, d, x[9], S21, 0x21e1cde6); /* 25 */
            GG(ref d, a, b, c, x[14], S22, 0xc33707d6); /* 26 */
            GG(ref c, d, a, b, x[3], S23, 0xf4d50d87); /* 27 */
            GG(ref b, c, d, a, x[8], S24, 0x455a14ed); /* 28 */
            GG(ref a, b, c, d, x[13], S21, 0xa9e3e905); /* 29 */
            GG(ref d, a, b, c, x[2], S22, 0xfcefa3f8); /* 30 */
            GG(ref c, d, a, b, x[7], S23, 0x676f02d9); /* 31 */
            GG(ref b, c, d, a, x[12], S24, 0x8d2a4c8a); /* 32 */

            // Round 3
            HH(ref a, b, c, d, x[5], S31, 0xfffa3942); /* 33 */
            HH(ref d, a, b, c, x[8], S32, 0x8771f681); /* 34 */
            HH(ref c, d, a, b, x[11], S33, 0x6d9d6122); /* 35 */
            HH(ref b, c, d, a, x[14], S34, 0xfde5380c); /* 36 */
            HH(ref a, b, c, d, x[1], S31, 0xa4beea44); /* 37 */
            HH(ref d, a, b, c, x[4], S32, 0x4bdecfa9); /* 38 */
            HH(ref c, d, a, b, x[7], S33, 0xf6bb4b60); /* 39 */
            HH(ref b, c, d, a, x[10], S34, 0xbebfbc70); /* 40 */
            HH(ref a, b, c, d, x[13], S31, 0x289b7ec6); /* 41 */
            HH(ref d, a, b, c, x[0], S32, 0xeaa127fa); /* 42 */
            HH(ref c, d, a, b, x[3], S33, 0xd4ef3085); /* 43 */
            HH(ref b, c, d, a, x[6], S34, 0x4881d05); /* 44 */
            HH(ref a, b, c, d, x[9], S31, 0xd9d4d039); /* 45 */
            HH(ref d, a, b, c, x[12], S32, 0xe6db99e5); /* 46 */
            HH(ref c, d, a, b, x[15], S33, 0x1fa27cf8); /* 47 */
            HH(ref b, c, d, a, x[2], S34, 0xc4ac5665); /* 48 */

            // Round 4
            II(ref a, b, c, d, x[0], S41, 0xf4292244); /* 49 */
            II(ref d, a, b, c, x[7], S42, 0x432aff97); /* 50 */
            II(ref c, d, a, b, x[14], S43, 0xab9423a7); /* 51 */
            II(ref b, c, d, a, x[5], S44, 0xfc93a039); /* 52 */
            II(ref a, b, c, d, x[12], S41, 0x655b59c3); /* 53 */
            II(ref d, a, b, c, x[3], S42, 0x8f0ccc92); /* 54 */
            II(ref c, d, a, b, x[10], S43, 0xffeff47d); /* 55 */
            II(ref b, c, d, a, x[1], S44, 0x85845dd1); /* 56 */
            II(ref a, b, c, d, x[8], S41, 0x6fa87e4f); /* 57 */
            II(ref d, a, b, c, x[15], S42, 0xfe2ce6e0); /* 58 */
            II(ref c, d, a, b, x[6], S43, 0xa3014314); /* 59 */
            II(ref b, c, d, a, x[13], S44, 0x4e0811a1); /* 60 */
            II(ref a, b, c, d, x[4], S41, 0xf7537e82); /* 61 */
            II(ref d, a, b, c, x[11], S42, 0xbd3af235); /* 62 */
            II(ref c, d, a, b, x[2], S43, 0x2ad7d2bb); /* 63 */
            II(ref b, c, d, a, x[9], S44, 0xeb86d391); /* 64 */

            state[0] += a;
            state[1] += b;
            state[2] += c;
            state[3] += d;

            // Zeroize sensitive information.
            for (int i = 0; i < x.Length; i++)
                x[i] = 0;
        }

        /// <summary>
        /// Encodes input (uint) into output (byte). Assumes len is
        ///  multiple of 4.
        /// </summary>
        /// <param name="output"></param>
        /// <param name="outputOffset"></param>
        /// <param name="input"></param>
        /// <param name="inputOffset"></param>
        /// <param name="count"></param>
        private static void Encode(byte[] output, int outputOffset, uint[] input, int inputOffset, int count)
        {
            int i, j;
            int end = outputOffset + count;
            for (i = inputOffset, j = outputOffset; j < end; i++, j += 4)
            {
                output[j] = (byte)(input[i] & 0xff);
                output[j + 1] = (byte)((input[i] >> 8) & 0xff);
                output[j + 2] = (byte)((input[i] >> 16) & 0xff);
                output[j + 3] = (byte)((input[i] >> 24) & 0xff);
            }
        }

        /// <summary>
        /// Decodes input (byte) into output (uint). Assumes len is
        /// a multiple of 4.
        /// </summary>
        /// <param name="output"></param>
        /// <param name="outputOffset"></param>
        /// <param name="input"></param>
        /// <param name="inputOffset"></param>
        /// <param name="count"></param>
        static private void Decode(uint[] output, int outputOffset, byte[] input, int inputOffset, int count)
        {
            int i, j;
            int end = inputOffset + count;
            for (i = outputOffset, j = inputOffset; j < end; i++, j += 4)
                output[i] = ((uint)input[j]) | (((uint)input[j + 1]) << 8) | (((uint)input[j + 2]) << 16) | (((uint)input[j + 3]) <<
24);
        }
        #endregion

        #region expose the same interface as the regular MD5 object

        protected byte[] HashValue;
        protected int State;
        public virtual bool CanReuseTransform
        {
            get
            {
                return true;
            }
        }

        public virtual bool CanTransformMultipleBlocks
        {
            get
            {
                return true;
            }
        }
        public virtual byte[] Hash
        {
            get
            {
                if (this.State != 0)
                    throw new InvalidOperationException();
                return (byte[])HashValue.Clone();
            }
        }
        public virtual int HashSize
        {
            get
            {
                return HashSizeValue;
            }
        }
        protected int HashSizeValue = 128;

        public virtual int InputBlockSize
        {
            get
            {
                return 1;
            }
        }
        public virtual int OutputBlockSize
        {
            get
            {
                return 1;
            }
        }

        public void Clear()
        {
            Dispose(true);
        }

        public byte[] ComputeHash(byte[] buffer)
        {
            return ComputeHash(buffer, 0, buffer.Length);
        }
        public byte[] ComputeHash(byte[] buffer, int offset, int count)
        {
            Initialize();
            HashCore(buffer, offset, count);
            HashValue = HashFinal();
            return (byte[])HashValue.Clone();
        }

        public byte[] ComputeHash(Stream inputStream)
        {
            Initialize();
            int count;
            byte[] buffer = new byte[4096];
            while (0 < (count = inputStream.Read(buffer, 0, 4096)))
            {
                HashCore(buffer, 0, count);
            }
            HashValue = HashFinal();
            return (byte[])HashValue.Clone();
        }

        public int TransformBlock(
            byte[] inputBuffer,
            int inputOffset,
            int inputCount,
            byte[] outputBuffer,
            int outputOffset
            )
        {
            if (inputBuffer == null)
            {
                throw new ArgumentNullException("inputBuffer");
            }
            if (inputOffset < 0)
            {
                throw new ArgumentOutOfRangeException("inputOffset");
            }
            if ((inputCount < 0) || (inputCount > inputBuffer.Length))
            {
                throw new ArgumentException("inputCount");
            }
            if ((inputBuffer.Length - inputCount) < inputOffset)
            {
                throw new ArgumentOutOfRangeException("inputOffset");
            }
            if (this.State == 0)
            {
                Initialize();
                this.State = 1;
            }

            HashCore(inputBuffer, inputOffset, inputCount);
            if ((inputBuffer != outputBuffer) || (inputOffset != outputOffset))
            {
                Buffer.BlockCopy(inputBuffer, inputOffset, outputBuffer, outputOffset, inputCount);
            }
            return inputCount;
        }
        public byte[] TransformFinalBlock(
            byte[] inputBuffer,
            int inputOffset,
            int inputCount
            )
        {
            if (inputBuffer == null)
            {
                throw new ArgumentNullException("inputBuffer");
            }
            if (inputOffset < 0)
            {
                throw new ArgumentOutOfRangeException("inputOffset");
            }
            if ((inputCount < 0) || (inputCount > inputBuffer.Length))
            {
                throw new ArgumentException("inputCount");
            }
            if ((inputBuffer.Length - inputCount) < inputOffset)
            {
                throw new ArgumentOutOfRangeException("inputOffset");
            }
            if (this.State == 0)
            {
                Initialize();
            }
            HashCore(inputBuffer, inputOffset, inputCount);
            HashValue = HashFinal();
            byte[] buffer = new byte[inputCount];
            Buffer.BlockCopy(inputBuffer, inputOffset, buffer, 0, inputCount);
            this.State = 0;
            return buffer;
        }
        #endregion

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                Initialize();
        }
        public void Dispose()
        {
            Dispose(true);
        }
    }

    public class PubnubCrypto
    {
        private string cipherKey = "";
        public PubnubCrypto(string cipher_key)
        {
            this.cipherKey = cipher_key;
        }

        /// <summary>
        /// Computes the hash using the specified algo
        /// </summary>
        /// <returns>
        /// The hash.
        /// </returns>
        /// <param name='input'>
        /// Input string
        /// </param>
        /// <param name='algorithm'>
        /// Algorithm to use for Hashing
        /// </param>
        private static string ComputeHash(string input, HashAlgorithm algorithm)
        {
#if (SILVERLIGHT || WINDOWS_PHONE)
            Byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
#else
            Byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
#endif
            Byte[] hashedBytes = algorithm.ComputeHash(inputBytes);
            return BitConverter.ToString(hashedBytes);
        }

        private string GetEncryptionKey()
        {
            //Compute Hash using the SHA256 
#if (SILVERLIGHT || WINDOWS_PHONE || MONOTOUCH || MONODROID)
            string strKeySHA256HashRaw = ComputeHash(this.cipherKey, new System.Security.Cryptography.SHA256Managed());
#else
            string strKeySHA256HashRaw = ComputeHash(this.cipherKey, new SHA256CryptoServiceProvider());
#endif
            //delete the "-" that appear after every 2 chars
            string strKeySHA256Hash = (strKeySHA256HashRaw.Replace("-", "")).Substring(0, 32);
            //convert to lower case
            return strKeySHA256Hash.ToLower();
        }

        /**
         * EncryptOrDecrypt
         * 
         * Basic function for encrypt or decrypt a string
         * for encrypt type = true
         * for decrypt type = false
         */
        private string EncryptOrDecrypt(bool type, string plainStr)
        {
#if (SILVERLIGHT || WINDOWS_PHONE)
            AesManaged aesEncryption = new AesManaged();
            aesEncryption.KeySize = 256;
            aesEncryption.BlockSize = 128;
            //get ASCII bytes of the string
            aesEncryption.IV = System.Text.Encoding.UTF8.GetBytes("0123456789012345");
            aesEncryption.Key = System.Text.Encoding.UTF8.GetBytes(GetEncryptionKey());
#else
            RijndaelManaged aesEncryption = new RijndaelManaged();
            aesEncryption.KeySize = 256;
            aesEncryption.BlockSize = 128;
            //Mode CBC
            aesEncryption.Mode = CipherMode.CBC;
            //padding
            aesEncryption.Padding = PaddingMode.PKCS7;
            //get ASCII bytes of the string
            aesEncryption.IV = System.Text.Encoding.ASCII.GetBytes("0123456789012345");
            aesEncryption.Key = System.Text.Encoding.ASCII.GetBytes(GetEncryptionKey());
#endif

            if (type)
            {
                ICryptoTransform crypto = aesEncryption.CreateEncryptor();
                plainStr = EncodeNonAsciiCharacters(plainStr);
#if (SILVERLIGHT || WINDOWS_PHONE)
                byte[] plainText = Encoding.UTF8.GetBytes(plainStr);
#else
                byte[] plainText = Encoding.ASCII.GetBytes(plainStr);
#endif

                //encrypt
                byte[] cipherText = crypto.TransformFinalBlock(plainText, 0, plainText.Length);
                return Convert.ToBase64String(cipherText);
            }
            else
            {
                try
                {
                    ICryptoTransform decrypto = aesEncryption.CreateDecryptor();
                    //decode
                    byte[] decryptedBytes = Convert.FromBase64CharArray(plainStr.ToCharArray(), 0, plainStr.Length);

                    //decrypt
#if (SILVERLIGHT || WINDOWS_PHONE)
                    var data = decrypto.TransformFinalBlock(decryptedBytes, 0, decryptedBytes.Length);
                    string decrypted = Encoding.UTF8.GetString(data, 0, data.Length);
#else
                    string decrypted = System.Text.Encoding.ASCII.GetString(decrypto.TransformFinalBlock(decryptedBytes, 0, decryptedBytes.Length));
#endif

                    return decrypted;
                }
                catch (Exception ex)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0} Decrypt Error. {1}", DateTime.Now.ToString(), ex.ToString()), LoggingMethod.LevelVerbose);
                    return "**DECRYPT ERROR**";
                }
            }
        }

        // encrypt string
        public string Encrypt(string plainText)
        {
            if (plainText == null || plainText.Length <= 0) throw new ArgumentNullException("plainText");

            return EncryptOrDecrypt(true, plainText);
        }

        // decrypt string
        public string Decrypt(string cipherText)
        {
            if (cipherText == null) throw new ArgumentNullException("cipherText");

            return EncryptOrDecrypt(false, cipherText);
        }

        //md5 used for AES encryption key
        private static byte[] Md5(string cipherKey)
        {
            MD5 obj = new MD5CryptoServiceProvider();
#if (SILVERLIGHT || WINDOWS_PHONE)
            byte[] data = Encoding.UTF8.GetBytes(cipherKey);
#else
            byte[] data = Encoding.Default.GetBytes(cipherKey);
#endif
            return obj.ComputeHash(data);
        }
        /// <summary>
        /// Encodes the non ASCII characters.
        /// </summary>
        /// <returns>
        /// The non ASCII characters.
        /// </returns>
        /// <param name='value'>
        /// Value.
        /// </param>
        private string EncodeNonAsciiCharacters(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in value)
            {
                if (c > 127)
                {
                    // This character is too big for ASCII
                    string encodedValue = "\\u" + ((int)c).ToString("x4");
                    sb.Append(encodedValue);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

    }

    internal enum ResponseType
    {
        Publish,
        History,
        Time,
        Subscribe,
        Presence,
        Here_Now,
        DetailedHistory,
        Leave
    }

    internal class ReconnectState<T>
    {
        public string channel;
        public ResponseType type;
        public Action<T> callback;
        public Action<T> connectCallback;
        public object timetoken;

        public ReconnectState()
        {
            channel = "";
            callback = null;
            connectCallback = null;
            timetoken = null;
        }
    }

    internal class RequestState
    {
        public PubnubWebRequest request;
        public PubnubWebResponse response;
        public ResponseType type;
        public string channel;
        public bool timeout;

        public RequestState()
        {
            request = null;
            response = null;
            channel = "";
        }
    }

    internal class InternetState
    {
        public Action<bool> callback;

        public InternetState()
        {
            callback = null;
        }
    }

    internal class ClientNetworkStatus
    {
        private static bool _status = true;

#if (SILVERLIGHT  || WINDOWS_PHONE)
        private static ManualResetEvent mres = new ManualResetEvent(false);
        private static ManualResetEvent mreSocketAsync = new ManualResetEvent(false);
#else
        private static ManualResetEventSlim mres = new ManualResetEventSlim(false);
#endif
        internal static void CheckInternetStatus(bool systemActive, Action<bool> Callback)
        {
            if (Callback != null)
            {
                try
                {
                    if (systemActive)
                    {
                        CheckClientNetworkAvailability(Callback);
                    }
                    else
                    {
                        Callback(false);
                    }
                }
                catch (Exception ex)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0} checkInternetStatus Error. {1}", DateTime.Now.ToString(), ex.ToString()), LoggingMethod.LevelError);
                }
            }
        }

        internal static bool CheckInternetStatus(bool systemActive)
        {
            CheckClientNetworkAvailability(CallbackClientNetworkStatus);
            return _status;
        }


        private static void CallbackClientNetworkStatus(bool status)
        {
            _status = status;
        }

        private static void CheckClientNetworkAvailability(Action<bool> callback)
        {
            InternetState state = new InternetState();
            state.callback = callback;
            ThreadPool.QueueUserWorkItem(CheckSocketConnect, state);
#if (SILVERLIGHT || WINDOWS_PHONE)
            mres.WaitOne();
#else
            mres.Wait();
#endif
        }

        private static void CheckSocketConnect(object internetState)
        {
            InternetState state = internetState as InternetState;
            Action<bool> callback = state.callback;
            try
            {
#if (SILVERLIGHT || WINDOWS_PHONE)
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    SocketAsyncEventArgs sae = new SocketAsyncEventArgs();
                    sae.UserToken = state;
                    sae.RemoteEndPoint = new DnsEndPoint("pubsub.pubnub.com", 80);
                    sae.Completed += new EventHandler<SocketAsyncEventArgs>(socketAsync_Completed);
                    bool test = socket.ConnectAsync(sae);

                    mreSocketAsync.WaitOne(1000);
                    sae.Completed -= new EventHandler<SocketAsyncEventArgs>(socketAsync_Completed);
                    socket.Close();
                }
#else
                using (UdpClient udp = new UdpClient("pubsub.pubnub.com", 80))
                {
                    IPAddress localAddress = ((IPEndPoint)udp.Client.LocalEndPoint).Address;
                    udp.Close();

                    LoggingMethod.WriteToLog(string.Format("DateTime {0} checkInternetStatus LocalIP: {1}", DateTime.Now.ToString(), localAddress.ToString()), LoggingMethod.LevelVerbose);
                    callback(true);
                }
#endif
            }
            catch (Exception ex)
            {
                LoggingMethod.WriteToLog(string.Format("DateTime {0} checkInternetStatus Error. {1}", DateTime.Now.ToString(), ex.ToString()), LoggingMethod.LevelError);
                callback(false);
            }
            mres.Set();
        }

#if (SILVERLIGHT || WINDOWS_PHONE)
        static void socketAsync_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (e.LastOperation == SocketAsyncOperation.Connect)
            {
                Socket skt = sender as Socket;
                InternetState state = e.UserToken as InternetState;
                if (state != null)
                {
                    LoggingMethod.WriteToLog(string.Format("DateTime {0} socketAsync_Completed.", DateTime.Now.ToString()), LoggingMethod.LevelInfo);
                    state.callback(true);
                }
                mreSocketAsync.Set();
            }
        }
#endif

    }

    internal class LoggingMethod
    {
        private static int logLevel = 0;
        public static Level LogLevel
        {
            get
            {
                return (Level)logLevel;
            }
            set
            {
                logLevel = (int)value;
            }
        }
        public enum Level
        {
            Off,
            Error,
            Info,
            Verbose,
            Warning
        }

        public static bool LevelError
        {
            get
            {
                return (int)LogLevel >= 1;
            }
        }

        public static bool LevelInfo
        {
            get
            {
                return (int)LogLevel >= 2;
            }
        }

        public static bool LevelVerbose
        {
            get
            {
                return (int)LogLevel >= 3;
            }
        }

        public static bool LevelWarning
        {
            get
            {
                return (int)LogLevel >= 4;
            }
        }

        public static void WriteToLog(string logText, bool writeToLog)
        {
            if (writeToLog)
            {
#if (SILVERLIGHT || WINDOWS_PHONE || MONOTOUCH || MONODROID)
                Debug.WriteLine(logText);
#else
                Trace.WriteLine(logText);
#endif
            }
        }
    }

    public interface IPubnubUnitTest
    {
        bool EnableStubTest
        {
            get;
            set;
        }

        string TestClassName
        {
            get;
            set;
        }

        string TestCaseName
        {
            get;
            set;
        }

        string GetStubResponse(Uri request);
    }

    internal class PubnubWebRequestCreator : IWebRequestCreate
    {
        private IPubnubUnitTest pubnubUnitTest = null;
        public PubnubWebRequestCreator()
        {
        }

        public PubnubWebRequestCreator(IPubnubUnitTest pubnubUnitTest)
        {
            this.pubnubUnitTest = pubnubUnitTest;
        }

        public WebRequest Create(Uri uri)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
            if (this.pubnubUnitTest is IPubnubUnitTest)
            {
                return new PubnubWebRequest(req, pubnubUnitTest);
            }
            else
            {
                return new PubnubWebRequest(req);
            }
        }
    }

    internal class PubnubWebRequest : WebRequest
    {
        private IPubnubUnitTest pubnubUnitTest = null;

        HttpWebRequest request;

#if (!SILVERLIGHT && !WINDOWS_PHONE)
        private int _timeout;
        public override int Timeout
        {
            get
            {
                return _timeout;
            }
            set
            {
                _timeout = value;
                if (request != null)
                {
                    request.Timeout = _timeout;
                }
            }
        }

        public override IWebProxy Proxy
        {
            get
            {
                return request.Proxy;
            }
            set
            {
                request.Proxy = value;
            }
        }

        public override bool PreAuthenticate
        {
            get
            {
                return request.PreAuthenticate;
            }
            set
            {
                request.PreAuthenticate = value;
            }
        }
#endif

#if ((!__MonoCS__) && (!SILVERLIGHT) && !WINDOWS_PHONE)
        public ServicePoint ServicePoint;
#endif

        public PubnubWebRequest(HttpWebRequest request)
        {
            this.request = request;
#if ((!__MonoCS__) && (!SILVERLIGHT) && !WINDOWS_PHONE)
            this.ServicePoint = this.request.ServicePoint;
#endif
        }
        public PubnubWebRequest(HttpWebRequest request, IPubnubUnitTest pubnubUnitTest)
        {
            this.request = request;
            this.pubnubUnitTest = pubnubUnitTest;
#if ((!__MonoCS__) && (!SILVERLIGHT) && !WINDOWS_PHONE)
            this.ServicePoint = this.request.ServicePoint;
#endif
        }

        public override void Abort()
        {
            if (request != null)
            {
                request.Abort();
            }
        }

        public override WebHeaderCollection Headers
        {
            get
            {
                return request.Headers;
            }
            set
            {
                request.Headers = value;
            }
        }

        public override string Method
        {
            get
            {
                return request.Method;
            }
            set
            {
                request.Method = value;
            }
        }

        public override string ContentType
        {
            get
            {
                return request.ContentType;
            }
            set
            {
                request.ContentType = value;
            }
        }

        public override ICredentials Credentials
        {
            get
            {
                return request.Credentials;
            }
            set
            {
                request.Credentials = value;
            }
        }

        public override IAsyncResult BeginGetRequestStream(AsyncCallback callback, object state)
        {
            return request.BeginGetRequestStream(callback, state);
        }

        public override Stream EndGetRequestStream(IAsyncResult asyncResult)
        {
            return request.EndGetRequestStream(asyncResult);
        }

        public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state)
        {
            if (pubnubUnitTest is IPubnubUnitTest && pubnubUnitTest.EnableStubTest)
            {
                return new PubnubWebAsyncResult(callback, state);
            }
            else
            {
                return request.BeginGetResponse(callback, state);
            }
        }

        public override WebResponse EndGetResponse(IAsyncResult asyncResult)
        {
            if (pubnubUnitTest is IPubnubUnitTest && pubnubUnitTest.EnableStubTest)
            {
                string stubResponse = pubnubUnitTest.GetStubResponse(request.RequestUri);
                return new PubnubWebResponse(new MemoryStream(Encoding.UTF8.GetBytes(stubResponse)));
            }
            else
            {
                return new PubnubWebResponse(request.EndGetResponse(asyncResult));
            }
        }

        public override Uri RequestUri
        {
            get
            {
                return request.RequestUri;
            }
        }
    }

    internal class PubnubWebResponse : WebResponse
    {
        WebResponse response;
        readonly Stream _responseStream;

        public PubnubWebResponse(WebResponse response)
        {
            this.response = response;
        }

        public PubnubWebResponse(Stream responseStream)
        {
            _responseStream = responseStream;
        }

        public override Stream GetResponseStream()
        {
            if (response != null)
                return response.GetResponseStream();
            else
                return _responseStream;
        }

        public override void Close()
        {
            if (response != null)
            {
                response.Close();
            }
        }

        public override long ContentLength
        {
            get
            {
                return response.ContentLength;
            }
        }

        public override string ContentType
        {
            get
            {
                return response.ContentType;
            }
        }

        public override Uri ResponseUri
        {
            get
            {
                return response.ResponseUri;
            }
        }
    }

    internal class PubnubWebAsyncResult : IAsyncResult
    {
        private const int pubnubDefaultLatencyInMilliSeconds = 1; //PubnubDefaultLatencyInMilliSeconds
        private readonly AsyncCallback _callback;
        private readonly object _state;
        private readonly ManualResetEvent _waitHandle;
        private readonly Timer _timer;
        
        public bool IsCompleted 
        { 
            get; private set; 
        }

        public WaitHandle AsyncWaitHandle
        {
            get { return _waitHandle; }
        }

        public object AsyncState
        {
            get { return _state; }
        }

        public bool CompletedSynchronously
        {
            get { return IsCompleted; }
        }

        public PubnubWebAsyncResult(AsyncCallback callback, object state)
            : this(callback, state, TimeSpan.FromMilliseconds(pubnubDefaultLatencyInMilliSeconds))
        {
        }

        public PubnubWebAsyncResult(AsyncCallback callback, object state, TimeSpan latency)
        {
            IsCompleted = false;
            _callback = callback;
            _state = state;
            _waitHandle = new ManualResetEvent(false);
            _timer = new Timer(onTimer => NotifyComplete(), null, latency, TimeSpan.FromMilliseconds(-1));
        }

        public void Abort()
        {
            _timer.Dispose();
            NotifyComplete();
        }

        private void NotifyComplete()
        {
            IsCompleted = true;
            _waitHandle.Set();
            if (_callback != null)
                _callback(this);
        }
    }

    public class PubnubProxy
    {
        string proxyServer;
        int proxyPort;
        string proxyUserName;
        string proxyPassword;

        public string ProxyServer
        {
            get
            {
                return proxyServer;
            }
            set
            {
                proxyServer = value;
            }
        }

        public int ProxyPort
        {
            get
            {
                return proxyPort;
            }
            set
            {
                proxyPort = value;
            }
        }

        public string ProxyUserName
        {
            get
            {
                return proxyUserName;
            }
            set
            {
                proxyUserName = value;
            }
        }

        public string ProxyPassword
        {
            get
            {
                return proxyPassword;
            }
            set
            {
                proxyPassword = value;
            }
        }
    }

}
