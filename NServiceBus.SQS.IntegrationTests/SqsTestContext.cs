﻿using Amazon.SQS.Model;
using NServiceBus.ObjectBuilder;
using NServiceBus.Pipeline;
using NServiceBus.Settings;
using NServiceBus.Transports.SQS;
using NServiceBus.Unicast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.ObjectBuilder.Common;
using System.Configuration;

namespace NServiceBus.SQS.IntegrationTests
{
    class SqsTestContext : IDisposable
    {
        public static readonly string QueueName = "testQueue";

        public static readonly string MachineName = "testMachine";

        public SqsConnectionConfiguration ConnectionConfiguration { get; private set; }

		public IAwsClientFactory ClientFactory { get; private set; }

		public SqsQueueUrlCache QueueUrlCache { get; private set; }

        public IObservable<TransportMessage> ReceivedMessages 
        {
            get { return _receivedMessages; }
        }

        public IObservable<Exception> ExceptionsThrownByReceiver 
        { 
            get { return _exceptionsThrownByReceiver; } 
        }

        public SqsDequeueStrategy DequeueStrategy { get; private set; }

        public SqsQueueSender Sender { get; private set; }

		public SqsQueueCreator Creator { get; private set; }

        public Address Address { get; private set; }

        private Subject<TransportMessage> _receivedMessages;
        private Subject<Exception> _exceptionsThrownByReceiver;
        
        public SqsTestContext()
        {
            Address = new Address(QueueName, MachineName);
			ConnectionConfiguration = 
				SqsConnectionStringParser.Parse(ConfigurationManager.AppSettings["TestConnectionString"]);

			ClientFactory = new AwsClientFactory();
			Creator = new SqsQueueCreator();
			Creator.ConnectionConfiguration = ConnectionConfiguration;
			Creator.ClientFactory = ClientFactory;
			Creator.CreateQueueIfNecessary(Address, "");

            _receivedMessages = new Subject<TransportMessage>();
            _exceptionsThrownByReceiver = new Subject<Exception>();

			QueueUrlCache = new SqsQueueUrlCache();
			QueueUrlCache.ClientFactory = ClientFactory;
			QueueUrlCache.ConnectionConfiguration = ConnectionConfiguration;
		
            Sender = new SqsQueueSender();
            Sender.ConnectionConfiguration = ConnectionConfiguration;
			Sender.ClientFactory = ClientFactory;
			Sender.QueueUrlCache = QueueUrlCache;

            DequeueStrategy = new SqsDequeueStrategy();
            DequeueStrategy.ConnectionConfiguration = ConnectionConfiguration;
			DequeueStrategy.ClientFactory = ClientFactory;
			DequeueStrategy.PurgeOnStartup = true;
            DequeueStrategy.Init(Address,
                null,
                m =>
                {
                    _receivedMessages.OnNext(m);
                    return true;
                },
                (m, e) => 
                {
                    if (e != null)
                        _exceptionsThrownByReceiver.OnNext(e);
                });

            DequeueStrategy.Start(1);
        }

        public TransportMessage SendRawAndReceiveMessage(string rawMessageString)
        {
            return SendAndReceiveCore(() =>
                {
					using (var c = ClientFactory.CreateSqsClient(ConnectionConfiguration))
                    {
                        c.SendMessage(QueueUrlCache.GetQueueUrl(Address), rawMessageString);
                    }
                });
        }

        private TransportMessage SendAndReceiveCore(Action doSend)
        {
            // Definitely not thread safe; none of the integration tests that use
            // a single SqsTestContext instance can run in parallel. 

            TransportMessage lastReceivedMessage = null;
            Exception lastThrownException = null;

            int retryCount = 0;

            using (var receiveSubscription = ReceivedMessages.Subscribe(m => lastReceivedMessage = m))
            using (var exceptionSubscription = ExceptionsThrownByReceiver.Subscribe(e => lastThrownException = e))
            {
                doSend();

                while (lastReceivedMessage == null && lastThrownException == null && retryCount < 100)
                {
                    retryCount++;
                    Thread.Sleep(50);
                }
            }

            if (retryCount >= 100)
                throw new TimeoutException("Receiving a message timed out.");

            if (lastThrownException == null)
                return lastReceivedMessage;
            else
                throw lastThrownException;
        }

        public TransportMessage SendAndReceiveMessage(TransportMessage messageToSend)
        {
			return SendAndReceiveCore(() => Sender.Send(messageToSend, new Address(SqsTestContext.QueueName, SqsTestContext.MachineName)));
        }

        public void Dispose()
        {
            DequeueStrategy.Stop();
        }
    }
}