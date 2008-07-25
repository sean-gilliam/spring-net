#region License

/*
 * Copyright � 2002-2008 the original author or authors.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#endregion

using System;
using Apache.NMS;
using Common.Logging;
using Spring.Messaging.Nms;
using Spring.Objects.Factory;
using Spring.Util;

namespace Spring.Messaging.Nms.Connections
{
    /// <summary>
    /// A ConnectionFactory adapter that returns the same Connection
    /// from all CreateConnection() calls, and ignores calls to
    /// Connection.Close().  According to the JMS Connection
    /// model, this is perfectly thread-safe, check your vendor implmenetation for
    /// details.
    /// </summary>
    /// <remarks>
    /// You can either pass in a specific Connection directly or let this
    /// factory lazily create a Connection via a given target ConnectionFactory.
    /// <para>Useful in order to keep using the same Connection for multiple
    /// <see cref="NmsTemplate"/> calls, without having a pooling ConnectionFactory 
    /// underneath. This may span any number of transactions, even concurrently executing transactions.
    /// </para>
    /// <para>
    /// Note that Spring's message listener containers support the use of
    /// a shared Connection within each listener container instance. Using
    /// SingleConnectionFactory with a MessageListenerContainer only really makes sense for
    /// sharing a single Connection across multiple listener containers.
    /// </para>
    /// </remarks>
    /// <author>Juergen Hoeller</author>
    /// <author>Mark Pollack</author>
    /// <author>Mark Pollack (.NET)</author>
    public class SingleConnectionFactory : IConnectionFactory, IExceptionListener, IInitializingObject, IDisposable
    {
        #region Logging Definition

        private static readonly ILog LOG = LogManager.GetLogger(typeof (SingleConnectionFactory));

        #endregion

        #region Fields

        private IConnectionFactory targetConnectionFactory;

        private string clientId;

        private IExceptionListener exceptionListener;

        private bool reconnectOnException = false;

        /// <summary>
        /// Wrapped Connection
        /// </summary>
        private IConnection target;

        /// <summary>
        /// Proxy Connection
        /// </summary>
        private IConnection connection;

        /// <summary>
        /// Synchronization monitor for the shared Connection
        /// </summary>
        private object connectionMonitor = new object();

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleConnectionFactory"/> class.
        /// </summary>
        public SingleConnectionFactory()
        {
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="SingleConnectionFactory"/> class
        /// that alwasy returns the given Connection.
        /// </summary>
        /// <param name="target">The single Connection.</param>
        public SingleConnectionFactory(IConnection target)
        {
            AssertUtils.ArgumentNotNull(target, "connection", "TargetSession Connection must not be null");
            this.target = target;
            connection = GetSharedConnection(target);
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="SingleConnectionFactory"/> class
        /// that alwasy returns a single Connection.
        /// </summary>
        /// <param name="targetConnectionFactory">The target connection factory.</param>
        public SingleConnectionFactory(IConnectionFactory targetConnectionFactory)
        {
            AssertUtils.ArgumentNotNull(targetConnectionFactory, "targetConnectionFactory",
                                        "TargetSession ConnectionFactory must not be null");
            this.targetConnectionFactory = targetConnectionFactory;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the target connection factory which will be used to create a single
        /// connection.
        /// </summary>
        /// <value>The target connection factory.</value>
        public IConnectionFactory TargetConnectionFactory
        {
            get { return targetConnectionFactory; }
            set { targetConnectionFactory = value; }
        }


        /// <summary>
        /// Gets or sets the client id for the single Connection created and exposed by
        /// this ConnectionFactory.
        /// </summary>
        /// <remarks>Note that the client IDs need to be unique among all active
        /// Connections of teh underlying provider.  Furthermore, a client ID can only
        /// be assigned if the original ConnectionFactory hasn't already assigned one.</remarks>
        /// <value>The client id.</value>
        public string ClientId
        {
            get { return clientId; }
            set { clientId = value; }
        }


        /// <summary>
        /// Gets or sets the exception listener implementation that should be registered
	    /// with with the single Connection created by this factory, if any.
        /// </summary>
        /// <value>The exception listener.</value>
        public IExceptionListener ExceptionListener
        {
            get { return exceptionListener; }
            set { exceptionListener = value; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the single Connection
        /// should be reset (to be subsequently renewed) when a NMSException
        /// is reported by the underlying Connection.
        /// </summary>
        /// <remarks>
        /// Default is <code>false</code>.  Switch this to <code>true</code>
        /// to automatically trigger recover based on your messaging provider's
        /// exception notifications.
        /// <para>
        /// Internally, this will lead to a special ExceptionListener (this
        /// SingleConnectionFactory itself) being registered with the underlying
        /// Connection.  This can also be combined with a user-specified
        /// ExceptionListener, if desired.
        /// </para>
        /// </remarks>
        /// <value>
        /// 	<c>true</c> if [reconnect on exception]; otherwise, <c>false</c>.
        /// </value>
        public bool ReconnectOnException
        {
            get { return reconnectOnException; }
            set { reconnectOnException = value; }
        }

        #endregion

        #region IConnectionFactory Members

        /// <summary>
        /// Creates the connection.
        /// </summary>
        /// <returns>A single shared connection</returns>
        public IConnection CreateConnection()
        {
            lock (connectionMonitor)
            {
                if (connection == null)
                {
                    InitConnection();
                }
                return connection;
            }
        }

        /// <summary>
        /// Creates the connection.
        /// </summary>
        /// <param name="userName">Name of the user.</param>
        /// <param name="password">The password.</param>
        /// <returns></returns>
        public IConnection CreateConnection(string userName, string password)
        {
            throw new InvalidOperationException("SingleConnectionFactory does not support custom username and password.");
        }

        #endregion

        /// <summary>
        /// Initialize the underlying shared Connection. Closes and reinitializes the Connection if an underlying
	    /// Connection is present already.
        /// </summary>
        public void InitConnection()
        {
            if (TargetConnectionFactory == null)
            {
                throw new ArgumentException(
                    "'TargetConnectionFactory' is required for lazily initializing a Connection");
            }
            lock (connectionMonitor)
            {
                if (this.target != null)
                {
                    CloseConnection(this.target);
                }
                this.target = DoCreateConnection();
                PrepareConnection(this.target);
                if (LOG.IsDebugEnabled)
                {
                    LOG.Info("Established shared NMS Connection: " + this.target);
                }
                this.connection = GetSharedConnection(target);
            }
        }

        /// <summary>
        /// Exception listener callback that renews the underlying single Connection.
        /// </summary>
        /// <param name="exception">The exception from the messaging infrastructure.</param>
        public void OnException(Exception exception)
        {
            ResetConnection();
        }

        /// <summary>
        /// Prepares the connection before it is exposed.
	    /// The default implementation applies ExceptionListener and client id.
	    /// Can be overridden in subclasses.
        /// </summary>
        /// <param name="con">The Connection to prepare.</param>
        /// <exception cref="NMSException">if thrown by any NMS API methods.</exception>
        protected virtual void PrepareConnection(IConnection con)
        {
            if (ClientId != null)
            {
                con.ClientId = ClientId;
            }
            if (ExceptionListener != null || ReconnectOnException)
            {
                IExceptionListener listenerToUse = ExceptionListener;
                if (ReconnectOnException)
                {
                    InternalChainedExceptionListener chained = new InternalChainedExceptionListener(this, listenerToUse);
                    con.ExceptionListener += chained.OnException;
                }
                else
                {
                    if (ExceptionListener != null)
                    {
                        con.ExceptionListener += ExceptionListener.OnException;                                     
                    }
                }
            }
        }

        /// <summary>
        /// Template method for obtaining a (potentially cached) Session.
        /// </summary>
        /// <param name="con">The connection to operate on.</param>
        /// <param name="mode">The session ack mode.</param>
        /// <returns>the Session to use, or <code>null</code> to indicate
	    /// creation of a default Session</returns>  
        public virtual ISession GetSession(IConnection con, AcknowledgementMode mode)
        {
            return null;
        }

        /// <summary>
        /// reate a JMS Connection via this template's ConnectionFactory.
        /// </summary>
        /// <returns></returns>
        protected virtual IConnection DoCreateConnection()
        {
            return TargetConnectionFactory.CreateConnection();
        }

        /// <summary>
        /// Closes the given connection.
        /// </summary>
        /// <param name="con">The connection.</param>
        protected virtual void CloseConnection(IConnection con)
        {
            try
            {
                try
                {
                    con.Stop();
                } finally
                {
                    con.Close();
                }
            } catch (Exception ex)
            {
                LOG.Warn("Could not close shared NMS connection.", ex);
            }
        }

        #region IInitializingObject Members

        /// <summary>
        /// Ensure that the connection or TargetConnectionFactory are specified.
        /// </summary>
        public void AfterPropertiesSet()
        {
            if (connection == null && TargetConnectionFactory == null)
            {
                throw new ArgumentException("Connection or 'TargetConnectionFactory' is required.");
            }
        }

        #endregion


        /// <summary>
        /// Close the underlying shared connection. The provider of this ConnectionFactory needs to care for proper shutdown.
        /// As this object implements <see cref="IDisposable"/> an application context will automatically 
        /// invoke this on distruction o
        /// </summary>
        public void Dispose()
        {
            ResetConnection();
        }

        /// <summary>
        /// Resets the underlying shared Connection, to be reinitialized on next access.
        /// </summary>
        public virtual void ResetConnection()
        {
            lock (connectionMonitor)
            {
                if (this.target != null)
                {
                    CloseConnection(this.target);
                }
                this.target = null;
                this.connection = null;
            }
        }

        /// <summary>
        /// Wrap the given Connection with a proxy that delegates every method call to it
	    /// but suppresses close calls. This is useful for allowing application code to
	    /// handle a special framework Connection just like an ordinary Connection from a
	    /// ConnectionFactory.
        /// </summary>
        /// <param name="target">The original connection to wrap.</param>
        /// <returns>the wrapped connection</returns>
        protected virtual IConnection GetSharedConnection(IConnection target)
        {
            lock (connectionMonitor)
            {
                return new CloseSupressingConnection(this, target);
            }
        }
    }

    internal class InternalChainedExceptionListener : ChainedExceptionListener, IExceptionListener
    {
        private IExceptionListener userListener;
        public InternalChainedExceptionListener(IExceptionListener internalListener, IExceptionListener userListener)
        {
            AddListener(internalListener);
            if (userListener != null)
            {
                AddListener(userListener);
                this.userListener = userListener;
            }                    
        }

        public IExceptionListener UserListener
        {
            get { return userListener; }
        }
    }

    internal class CloseSupressingConnection : IConnection
    {
        private IConnection target;
        private SingleConnectionFactory singleConnectionFactory;

        public CloseSupressingConnection(SingleConnectionFactory singleConnectionFactory, IConnection target)
        {
            this.target = target;
            this.singleConnectionFactory = singleConnectionFactory;
        }

        public string ClientId
        {
            get { return target.ClientId; }
            set
            {
                string currentClientId = target.ClientId;
                if (currentClientId != null && currentClientId.Equals(value))
                {
                    //ok, the values are consistent.
                }
                else
                {
                    throw new ArgumentException(
                        "Setting of 'ClientID' property not supported on wrapper for shared Connection." +
                        "Set the 'ClientId' property on the SingleConnectionFactory instead.");    
                }
                
            }
        }

        public void Close()
        {
            // don't pass the call to the target.
        }

        public void Stop()
        {
            //don't pass the call to the target.
        }

        public ISession CreateSession()
        {
            return CreateSession(AcknowledgementMode.AutoAcknowledge);
        }

        public ISession CreateSession(AcknowledgementMode acknowledgementMode)
        {
            ISession session = singleConnectionFactory.GetSession(target, acknowledgementMode);
            if (session != null)
            {
                return session;
            }
            return target.CreateSession();
        }

        #region Pass through implementations to the target connection


        public event ExceptionListener ExceptionListener
        {
            add
            {
                target.ExceptionListener += value;
            }
            remove { target.ExceptionListener -= value; }
        }


        public AcknowledgementMode AcknowledgementMode
        {
            get { return target.AcknowledgementMode; }
            set { target.AcknowledgementMode = value; }
        }

        public void Dispose()
        {
            target.Dispose();
        }

        public void Start()
        {
            target.Start();
        }

        public bool IsStarted
        {
            get { return target.IsStarted; }
        }
        #endregion

    }
}