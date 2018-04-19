﻿using System;
using System.IO;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Ruya.EventBus.RabbitMQ
{
    public class DefaultRabbitMQPersistentConnection : IRabbitMQPersistentConnection
    {
        private readonly IConnectionFactory _connectionFactory;
        private readonly ILogger<DefaultRabbitMQPersistentConnection> _logger;

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly EventBusSetting _options;
        private readonly int _retryCount;

        private readonly object _syncRoot = new object();
        private IConnection _connection;
        private bool _disposed;

        // ReSharper disable once SuggestBaseTypeForParameter
        public DefaultRabbitMQPersistentConnection(ILogger<DefaultRabbitMQPersistentConnection> logger, IOptionsSnapshot<EventBusSetting> options)
        {
            _logger = logger;
            _options = options.Value;
            _connectionFactory = new ConnectionFactory
                                 {
                                     HostName = _options.Connection
                                   , UserName = _options.UserName
                                   , Password = _options.Password
            };
            _retryCount = _options.RetryCount;
        }

        public bool IsConnected => _connection?.IsOpen == true && !_disposed;

        public IModel CreateModel()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("No RabbitMQ connections are available to perform this action");
            }
            return _connection.CreateModel();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _connection.Dispose();
            }
            catch (IOException ex)
            {
                _logger.LogCritical(ex.ToString());
            }
        }

        public bool TryConnect()
        {
            lock (_syncRoot)
            {
                RetryPolicy policy = Policy.Handle<SocketException>()
                                           .Or<BrokerUnreachableException>()
                                           .WaitAndRetry(_retryCount
                                                       , retryAttempt =>
                                                         {
                                                             _logger.LogDebug("RabbitMQ Client is retrying to connect. Attempt {retryAttempt}", retryAttempt);
                                                             return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                                                         }
                                                       , (ex, time) => { _logger.LogWarning(ex.ToString()); });
                policy.Execute(() =>
                               {
                                   _logger.LogInformation("RabbitMQ Client is trying to connect {MessageQueueServer}", _connectionFactory.Uri);
                                   _connection = _connectionFactory.CreateConnection();
                               });

                if (IsConnected)
                {
                    _connection.ConnectionShutdown += OnConnectionShutdown;
                    _connection.CallbackException += OnCallbackException;
                    _connection.ConnectionBlocked += OnConnectionBlocked;
                    _logger.LogInformation($"RabbitMQ persistent connection acquired a connection {_connection.Endpoint.HostName} and is subscribed to failure events");
                    return true;
                }

                _logger.LogCritical("FATAL ERROR: RabbitMQ connections could not be created and opened");
                return false;
            }
        }

        private void OnConnectionBlocked(object sender, ConnectionBlockedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }
            _logger.LogWarning("A RabbitMQ connection is shutdown. Trying to re-connect...");
            TryConnect();
        }

        private void OnCallbackException(object sender, CallbackExceptionEventArgs e)
        {
            if (_disposed)
            {
                return;
            }
            _logger.LogWarning("A RabbitMQ connection throw exception. Trying to re-connect...");
            TryConnect();
        }

        private void OnConnectionShutdown(object sender, ShutdownEventArgs reason)
        {
            if (_disposed)
            {
                return;
            }
            _logger.LogWarning("A RabbitMQ connection is on shutdown. Trying to re-connect...");
            TryConnect();
        }
    }
}
