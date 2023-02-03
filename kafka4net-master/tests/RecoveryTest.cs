﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using kafka4net;
using kafka4net.ConsumerImpl;
using kafka4net.Utils;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using Logger = kafka4net.Logger;

namespace tests
{
    [Target("Kafka4netEtwTarget")]
    public class Kafka4netEtwTarget : TargetWithLayout
    {
        protected override void Write(LogEventInfo logEvent)
        {
            kafka4net.Tracing.EtwTrace.Marker(logEvent.FormattedMessage);
        }
    }

    [TestFixture]
    class RecoveryTest
    {
        Random _rnd = new Random();
        string _seed2Addresses = "192.168.56.10,192.168.56.20";
        string _seed3Addresses = "192.168.56.10,192.168.56.20,192.168.56.30";
        private const string _seed1Addresses = "192.168.56.10";
        static readonly NLog.Logger _log = LogManager.GetCurrentClassLogger();

        [SetUp]
        public void Setup()
        {
            // NLog
            var config = new LoggingConfiguration();
            var consoleTarget = new ColoredConsoleTarget();
            config.AddTarget("console", consoleTarget);
            var fileTarget = new FileTarget();
            config.AddTarget("file", fileTarget);
            
            var etwTargt = new Kafka4netEtwTarget();
            config.AddTarget("etw", etwTargt);
            config.LoggingRules.Add(new LoggingRule("tests.*", LogLevel.Debug, etwTargt));

            consoleTarget.Layout = "${date:format=HH\\:mm\\:ss.fff} ${level} [${threadname}:${threadid}] ${logger} ${message} ${exception:format=tostring}";
            fileTarget.FileName = "${basedir}../../../log.txt";
            fileTarget.Layout = "${longdate} ${level} [${threadname}:${threadid}] ${logger:shortName=true} ${message} ${exception:format=tostring,stacktrace:innerFormat=tostring,stacktrace}";

            var rule = new LoggingRule("*", LogLevel.Info, consoleTarget);
            rule.Targets.Add(fileTarget);
            //rule.Final = true;


            //var r1 = new LoggingRule("kafka4net.Internal.PartitionRecoveryMonitor", LogLevel.Info, fileTarget) { Final = true };
            //r1.Targets.Add(consoleTarget);
            //config.LoggingRules.Add(r1);

            rule = new LoggingRule("*", LogLevel.Debug, fileTarget);
            rule.ChildRules.Add(new LoggingRule("tests.*", LogLevel.Debug, consoleTarget));
            rule.ChildRules.Add(new LoggingRule("*", LogLevel.Info, consoleTarget));
            config.LoggingRules.Add(rule);

            // disable Transport noise
            // config.LoggingRules.Add(new LoggingRule("kafka4net.Protocol.Transport", LogLevel.Info, fileTarget) { Final = true});
            //
            //config.LoggingRules.Add(new LoggingRule("tests.*", LogLevel.Debug, fileTarget));
            //config.LoggingRules.Add(new LoggingRule("kafka4net.Internal.PartitionRecoveryMonitor", LogLevel.Error, fileTarget) { Final = true });
            //config.LoggingRules.Add(new LoggingRule("kafka4net.Connection", LogLevel.Error, fileTarget) { Final = true });
            //config.LoggingRules.Add(new LoggingRule("kafka4net.Protocols.Protocol", LogLevel.Error, fileTarget) { Final = true });
            //

            LogManager.Configuration = config;

            _log.Debug("=============== Starting =================");

            // set nlog logger in kafka
            Logger.SetupNLog();

            //
            // log4net
            //
            //var app1 = new log4net.Appender.FileAppender() { File = @"C:\projects\kafka4net\log2.txt" };
            //var coll = log4net.Config.BasicConfigurator.Configure();
            //var logger = log4net.LogManager.GetLogger("Main");
            //logger.Info("=============== Starting =================");
            //Logger.SetupLog4Net();

            TaskScheduler.UnobservedTaskException += (sender, args) => _log.Error("Unhandled task exception", (Exception)args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => _log.Error("Unhandled exception", (Exception)args.ExceptionObject);

            // make sure brokers are up from last run
            //VagrantBrokerUtil.RestartBrokers();
        }

        [TearDown]
        public void RestartBrokers()
        {
            VagrantBrokerUtil.RestartBrokers();
        }

        [TestFixtureSetUp]
        public void BeforeEveryTest()
        {
            VagrantBrokerUtil.RestartBrokers();
        }

        // If failed to connect, messages are errored

        // if topic does not exists, it is created when producer connects
        [Test]
        public async Task TopicIsAutocreatedByProducer()
        {
            kafka4net.Tracing.EtwTrace.Marker("TopicIsAutocreatedByProducer");

            var topic ="autocreate.test." + _rnd.Next();
            const int producedCount = 10;
            var lala = Encoding.UTF8.GetBytes("la-la-la");
            // TODO: set wait to 5sec

            //
            // Produce
            // In order to make sure that topic was created by producer, send and wait for producer
            // completion before performing validation read.
            //
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));

            await producer.ConnectAsync();

            _log.Debug("Producing...");
            await Observable.Interval(TimeSpan.FromSeconds(1)).
                Take(producedCount).
                Do(_ => producer.Send(new Message { Value = lala })).
                ToTask();
            await producer.CloseAsync(TimeSpan.FromSeconds(10));

            //
            // Validate by reading published messages
            //
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart(), maxWaitTimeMs: 1000, minBytesPerFetch: 1));
            var msgs = consumer.OnMessageArrived.Publish().RefCount();
            var receivedTxt = new List<string>();
            var consumerSubscription = msgs.
                Select(m => Encoding.UTF8.GetString(m.Value)).
                Synchronize(). // protect receivedTxt
                Do(m => _log.Info("Received {0}", m)). 
                Do(receivedTxt.Add).
                Subscribe();
            await consumer.IsConnected;

            _log.Debug("Waiting for consumer");
            await msgs.Take(producedCount).TakeUntil(DateTimeOffset.Now.AddSeconds(5)).LastOrDefaultAsync().ToTask();

            Assert.AreEqual(producedCount, receivedTxt.Count, "Did not received all messages");
            Assert.IsTrue(receivedTxt.All(m => m == "la-la-la"), "Unexpected message content");

            consumerSubscription.Dispose();
            consumer.Dispose();

            kafka4net.Tracing.EtwTrace.Marker("/TopicIsAutocreatedByProducer");
        }

        //[Test]
        //public void MultithreadingSend()
        //{
        //    // for 10 minutes, 15 threads, each to its own topic, 100ms interval, send messages.
        //    // In addition, 5 topics are produced by 
        //    // Consumer must validate that messages were sent in order and all were received
        //}

        //[Test]
        //public void Miltithreading2()
        //{
        //    // subjects are produced by 15, 7, 3, 3, 2, 2, 2, 2, producers/threads
        //    // Consumer must validate that messages were sent in order and all were received
        //}

        // consumer sees the same messages as producer

        // if leader goes down, messages keep being accepted 
        // and are committed (can be read) within (5sec?)
        // Also, order of messages is preserved
        [Test]
        public async Task LeaderDownProducerAndConsumerRecovery()
        {
            kafka4net.Tracing.EtwTrace.Marker("LeaderDownProducerAndConsumerRecovery");
            string topic = "part32." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 3, 2);

            var sent = new List<string>();
            var confirmedSent1 = new List<string>();

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            producer.OnSuccess += msgs =>
            {
                msgs.ForEach(msg => confirmedSent1.Add(Encoding.UTF8.GetString(msg.Value)));
                _log.Debug("Sent {0} messages", msgs.Length);
            };
            await producer.ConnectAsync();

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicEnd()));

            const int postCount = 100;
            const int postCount2 = 50;

            //
            // Read messages
            //
            var received = new List<ReceivedMessage>();
            var receivedEvents = new ReplaySubject<ReceivedMessage>();
            var consumerSubscription = consumer.OnMessageArrived.
                Synchronize().
                Subscribe(msg =>
                {
                    received.Add(msg);
                    receivedEvents.OnNext(msg);
                    _log.Debug("Received {0}/{1}", Encoding.UTF8.GetString(msg.Value), received.Count);
                });
            await consumer.IsConnected;

            //
            // Send #1
            //
            _log.Info("Start sender");
            Observable.Interval(TimeSpan.FromMilliseconds(200)).
                Take(postCount).
                Subscribe(
                    i => {
                        var msg = "msg " + i;
                        producer.Send(new Message { Value = Encoding.UTF8.GetBytes(msg) });
                        sent.Add("msg " + i);
                    },
                    () => _log.Info("Producer complete")
                );

            // wait for first 50 messages to arrive
            _log.Info("Waiting for first {0} messages to arrive", postCount2);
            await receivedEvents.Take(postCount2).Count().ToTask();
            Assert.AreEqual(postCount2, received.Count);

            _log.Info("Stopping broker");
            var stoppedBroker = VagrantBrokerUtil.StopBrokerLeaderForPartition(producer.Cluster, topic, 0);
            _log.Debug("Stopped broker {0}", stoppedBroker);

            // post another 50 messages
            _log.Info("Sending another {0} messages", postCount2);
            var sender2 = Observable.Interval(TimeSpan.FromMilliseconds(200)).
                Take(postCount2).
                Publish().RefCount();

            //
            // Send #2
            //
            sender2.Subscribe(
                    i => {
                        var msg = "msg #2 " + i;
                        producer.Send(new Message { Value = Encoding.UTF8.GetBytes(msg) });
                        sent.Add(msg);
                        _log.Debug("Sent msg #2 {0}", i);
                    },
                    () => _log.Info("Producer #2 complete")
                );

            _log.Info("Waiting for #2 sender to complete");
            await sender2.ToTask();
            _log.Info("Waiting for producer.Close");
            await producer.CloseAsync(TimeSpan.FromSeconds(60));

            _log.Info("Waiting 4sec for remaining messages");
            await Task.Delay(TimeSpan.FromSeconds(4)); // if unexpected messages arrive, let them in to detect failure

            _log.Info("Waiting for consumer.CloseAsync");
            consumer.Dispose();
            consumerSubscription.Dispose();

            if (postCount + postCount2 != received.Count)
            {
                var receivedStr = received.Select(m => Encoding.UTF8.GetString(m.Value)).ToArray();

                var diff = sent.Except(received.Select(m => Encoding.UTF8.GetString(m.Value))).OrderBy(s => s);
                _log.Info("Not received {0}: \n {1}", diff.Count(), string.Join("\n ", diff));

                var diff2 = sent.Except(confirmedSent1).OrderBy(s => s);
                _log.Info("Not confirmed {0}: \n {1}", diff2.Count(), string.Join("\n ", diff2));

                var diff3 = received.Select(m => Encoding.UTF8.GetString(m.Value)).Except(sent).OrderBy(s => s);
                _log.Info("Received extra: {0}: \n {1}", diff3.Count(), string.Join("\n ", diff3));

                var diff4 = confirmedSent1.Except(sent).OrderBy(s => s);
                _log.Info("Confirmed extra {0}: \n {1}", diff4.Count(), string.Join("\n ", diff4));

                var dups = receivedStr.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => string.Format("{0}: {1}", g.Count(), g.Key));
                _log.Info("Receved dups: \n {0}", string.Join("\n ", dups));

                _log.Debug("Received: \n{0}", string.Join("\n ", received.Select(m => Encoding.UTF8.GetString(m.Value))));
            }
            Assert.AreEqual(postCount + postCount2, received.Count, "Received.Count");

            _log.Info("Done");
            kafka4net.Tracing.EtwTrace.Marker("/LeaderDownProducerAndConsumerRecovery");
        }

        [Test]
        public async Task ListenerOnNonExistentTopicWaitsForTopicCreation()
        {
            kafka4net.Tracing.EtwTrace.Marker("ListenerOnNonExistentTopicWaitsForTopicCreation");
            const int numMessages = 400;
            var topic = "topic." + _rnd.Next();
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart()));
            var cancelSubject = new Subject<bool>();
            var receivedValuesTask = consumer.OnMessageArrived
                .Select(msg=>BitConverter.ToInt32(msg.Value, 0))
                .Do(val=>_log.Info("Received: {0}", val))
                .Take(numMessages)
                .TakeUntil(cancelSubject)
                .ToList().ToTask();
            //receivedValuesTask.Start();
            await consumer.IsConnected;

            // wait a couple seconds for things to "stabilize"
            await Task.Delay(TimeSpan.FromSeconds(4));

            // now produce 400 messages
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            await producer.ConnectAsync();
            Enumerable.Range(1, numMessages).
                Select(i => new Message { Value = BitConverter.GetBytes(i) }).
                ForEach(producer.Send);

            await Task.Delay(TimeSpan.FromSeconds(2));
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            // wait another little while, and stop the producer.
            await Task.Delay(TimeSpan.FromSeconds(2));

            cancelSubject.OnNext(true);

            var receivedValues = await receivedValuesTask;
            Assert.AreEqual(numMessages,receivedValues.Count);
            
            kafka4net.Tracing.EtwTrace.Marker("/ListenerOnNonExistentTopicWaitsForTopicCreation");
        }


        /// <summary>
        /// Test listener and producer recovery together
        /// </summary>
        [Test]
        public async Task ProducerAndListenerRecoveryTest()
        {
            kafka4net.Tracing.EtwTrace.Marker("ProducerAndListenerRecoveryTest");
            const int count = 200;
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic,6,3);

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));

            _log.Debug("Connecting");
            await producer.ConnectAsync();

            _log.Debug("Filling out {0}", topic);
            var sentList = new List<int>(200);
            Observable.Interval(TimeSpan.FromMilliseconds(100))
                .Select(l => (int) l)
                .Select(i => new Message {Value = BitConverter.GetBytes(i)})
                .Take(count)
                .Subscribe(msg=> { producer.Send(msg); sentList.Add(BitConverter.ToInt32(msg.Value, 0)); });

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart(), maxBytesPerFetch: 4 * 8));
            var current =0;
            var received = new ReplaySubject<ReceivedMessage>();
            Task brokerStopped = null;
            var consumerSubscription = consumer.OnMessageArrived.
                Subscribe(msg => {
                    current++;
                    if (current == 18)
                    {
                        brokerStopped = Task.Factory.StartNew(() => VagrantBrokerUtil.StopBrokerLeaderForPartition(consumer.Cluster, consumer.Topic, msg.Partition), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                        _log.Info("Stopped Broker Leader {0}",brokerStopped);
                    }
                    received.OnNext(msg);
                    _log.Info("Got: {0}", BitConverter.ToInt32(msg.Value, 0));
                });
            await consumer.IsConnected;

            _log.Info("Waiting for receiver complete");
            var receivedList = await received.Select(msg => BitConverter.ToInt32(msg.Value, 0)).Take(count).TakeUntil(DateTime.Now.AddSeconds(60)).ToList().ToTask();

            await brokerStopped.TimeoutAfter(TimeSpan.FromSeconds(10));

            // get the offsets for comparison later
            var heads = await consumer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            var tails = await consumer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            _log.Info("Done waiting for receiver. Closing producer.");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Info("Producer closed, disposing consumer subscription.");
            consumerSubscription.Dispose();
            _log.Info("Consumer subscription disposed. Closing consumer.");
            consumer.Dispose();
            _log.Info("Consumer closed.");

            if (sentList.Count != receivedList.Count)
            {
                // log some debug info.
                _log.Error("Did not receive all messages. Messages received: {0}",string.Join(",",receivedList.OrderBy(i=>i)));
                _log.Error("Did not receive all messages. Messages sent but NOT received: {0}", string.Join(",", sentList.Except(receivedList).OrderBy(i => i)));

                _log.Error("Sum of offsets fetched: {0}", tails.MessagesSince(heads));
                _log.Error("Offsets fetched: [{0}]", string.Join(",", tails.Partitions.Select(p => string.Format("{0}:{1}",p,tails.NextOffset(p)))));
            }

            Assert.AreEqual(sentList.Count, receivedList.Count);
            kafka4net.Tracing.EtwTrace.Marker("/ProducerAndListenerRecoveryTest");
        }

        /// <summary>
        /// Test producer recovery isolated.
        /// 
        /// Create queue and produce 200 messages. On message 30, shutdown broker#2
        /// Check that result summ of offsets (legth of the topic) is equal to 200
        /// Check confirmed sent messasges count is sequal to intendent sent.
        /// </summary>
        [Test]
        public async Task ProducerRecoveryTest()
        {
            kafka4net.Tracing.EtwTrace.Marker("ProducerRecoveryTest");

            const int count = 200;
            var topic = "part62." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 6, 2);

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));

            _log.Debug("Connecting");
            await producer.ConnectAsync();

            _log.Debug("Filling out {0}", topic);
            // when we get a confirm back, add to list actually sent.
            var actuallySentList = new List<int>(count);
            producer.OnSuccess += msgs => actuallySentList.AddRange(msgs.Select(msg => BitConverter.ToInt32(msg.Value, 0)));

            Task stopBrokerTask = null;
            var sentList = await Observable.Interval(TimeSpan.FromMilliseconds(100))
                .Select(l => (int)l)
                .Do(l => { if (l == 20) stopBrokerTask = Task.Factory.StartNew(() => VagrantBrokerUtil.StopBroker("broker2"), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default); })
                .Select(i => new Message { Value = BitConverter.GetBytes(i) })
                .Take(count)
                .Do(producer.Send)
                .Select(msg => BitConverter.ToInt32(msg.Value, 0))
                .ToList();


            _log.Info("Done waiting for sending. Closing producer.");
            await producer.CloseAsync(TimeSpan.FromSeconds(30));
            _log.Info("Producer closed.");

            if (stopBrokerTask != null)
                await stopBrokerTask.TimeoutAfter(TimeSpan.FromSeconds(10));

            //
            // Check length of result topic
            //
            var c2 = new Cluster(_seed2Addresses);
            await c2.ConnectAsync();
            var heads = await c2.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            var tails = await c2.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            _log.Info("Sum of offsets: {0}", tails.MessagesSince(heads));
            _log.Info("Offsets: [{0}]", string.Join(",", tails.Partitions.Select(p => string.Format("{0}:{1}", p, tails.NextOffset(p)))));

            //
            if (sentList.Count != actuallySentList.Count)
            {
                // log some debug info.
                _log.Error("Did not send all messages. Messages sent but NOT acknowledged: {0}", string.Join(",", sentList.Except(actuallySentList).OrderBy(i => i)));
            }

            Assert.AreEqual(sentList.Count, actuallySentList.Count, "Actually sent");
            Assert.AreEqual(sentList.Count, tails.MessagesSince(heads), "Offsets");
            
            kafka4net.Tracing.EtwTrace.Marker("/ProducerRecoveryTest");
        }


        /// <summary>
        /// We should be able to create a new cluster connection, and fetch topic offsets even while a broker is down.
        /// </summary>
        [Test]
        public async Task CanConnectToClusterAndFetchOffsetsWithBrokerDown()
        {
            kafka4net.Tracing.EtwTrace.Marker("CanConnectToClusterAndFetchOffsetsWithBrokerDown");
            const int count = 10000;
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 6, 3);

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            _log.Debug("Connecting");
            await producer.ConnectAsync();

            _log.Debug("Filling out {0} with {1} messages", topic, count);
            var sentList = await Enumerable.Range(0, count)
                .Select(i => new Message { Value = BitConverter.GetBytes(i) })
                .ToObservable()
                .Do(producer.Send)
                .Select(msg => BitConverter.ToInt32(msg.Value, 0))
                .ToList();

            await Task.Delay(TimeSpan.FromSeconds(5));

            var heads = await producer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            var tails = await producer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            _log.Info("Done sending messages.");
            _log.Info("Stopping Broker Leader for partition 0");
            VagrantBrokerUtil.StopBrokerLeaderForPartition(producer.Cluster, topic, 0);

            _log.Info("Closing producer.");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Info("Producer closed.");

            var messagesInTopic = (int)tails.MessagesSince(heads);
            _log.Info("Topic offsets indicate producer sent {0} messages.", messagesInTopic);


            _log.Info("Opening new Cluster connection");
            var c2 = new Cluster(_seed2Addresses);
            await c2.ConnectAsync();
            heads = await c2.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            tails = await c2.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            _log.Info("Total messages in topic from new Cluster connection: {0}", tails.MessagesSince(heads));
            _log.Info("Offsets: [{0}]", string.Join(",", tails.Partitions.Select(p => string.Format("{0}:{1}", p, tails.NextOffset(p)))));

            await c2.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Info("Closed cluster.");

            Assert.AreEqual(messagesInTopic, tails.MessagesSince(heads));
            kafka4net.Tracing.EtwTrace.Marker("/CanConnectToClusterAndFetchOffsetsWithBrokerDown");
        }

        /// <summary>
        /// Test just listener recovery isolated from producer
        /// </summary>
        [Test]
        public async Task ListenerRecoveryTest()
        {
            kafka4net.Tracing.EtwTrace.Marker("ListenerRecoveryTest");
            const int count = 10000;
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 6, 3);

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            _log.Debug("Connecting");
            await producer.ConnectAsync();

            _log.Debug("Filling out {0} with {1} messages", topic, count);
            var sentList = await Enumerable.Range(0, count)
                .Select(i => new Message { Value = BitConverter.GetBytes(i) })
                .ToObservable()
                .Do(producer.Send)
                .Select(msg => BitConverter.ToInt32(msg.Value, 0))
                .ToList();

            await Task.Delay(TimeSpan.FromSeconds(1));

            var heads = await producer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            var tails = await producer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            _log.Info("Done sending messages. Closing producer.");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Info("Producer closed, starting consumer subscription.");

            var messagesInTopic = (int)tails.MessagesSince(heads);
            _log.Info("Topic offsets indicate producer sent {0} messages.", messagesInTopic);


            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart(), maxBytesPerFetch: 4 * 8));
            var current = 0;
            var received = new ReplaySubject<ReceivedMessage>();
            Task stopBrokerTask = null;
            var consumerSubscription = consumer.OnMessageArrived.
                Subscribe(async msg =>
                {
                    current++;
                    if (current == 18)
                    {
                        stopBrokerTask = Task.Factory.StartNew(() => VagrantBrokerUtil.StopBrokerLeaderForPartition(consumer.Cluster, consumer.Topic, msg.Partition), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                    }
                    received.OnNext(msg);
                    //_log.Info("Got: {0}", BitConverter.ToInt32(msg.Value, 0));
                });
            await consumer.IsConnected;

            _log.Info("Waiting for receiver complete");
            var receivedList = await received.Select(msg => BitConverter.ToInt32(msg.Value, 0)).Take(messagesInTopic).
                TakeUntil(DateTime.Now.AddSeconds(60)).ToList().ToTask();

            if (stopBrokerTask != null)
                await stopBrokerTask.TimeoutAfter(TimeSpan.FromSeconds(10));

            tails = await consumer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            _log.Info("Receiver complete. Disposing Subscription");
            consumerSubscription.Dispose();
            _log.Info("Consumer subscription disposed. Closing consumer.");
            consumer.Dispose();
            _log.Info("Consumer closed.");

            _log.Info("Sum of offsets: {0}", tails.MessagesSince(heads));
            _log.Info("Offsets: [{0}]", string.Join(",", tails.Partitions.Select(p => string.Format("{0}:{1}", p, tails.NextOffset(p)))));

            if (messagesInTopic != receivedList.Count)
            {
                // log some debug info.
                _log.Error("Did not receive all messages. Messages sent but NOT received: {0}", string.Join(",", sentList.Except(receivedList).OrderBy(i => i)));

            }

            Assert.AreEqual(messagesInTopic, receivedList.Count);
            kafka4net.Tracing.EtwTrace.Marker("/ListenerRecoveryTest");
        }

        /// <summary>
        /// Set long batching period for producer (20 sec) and make sure that shutdown flushes
        /// buffered data.
        /// </summary>
        [Test]
        public async Task CleanShutdownTest()
        {
            kafka4net.Tracing.EtwTrace.Marker("CleanShutdownTest");
            const string topic = "shutdown.test";

            // set producer long batching period, 20 sec
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic, TimeSpan.FromSeconds(20), int.MaxValue));

            _log.Debug("Connecting");
            await producer.ConnectAsync();

            // start listener at the end of queue and accumulate received messages
            var received = new HashSet<string>();
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicEnd(), maxWaitTimeMs: 30 * 1000));
            _log.Info("Subscribing to consumer");
            var consumerSubscription = consumer.OnMessageArrived
                                        .Select(msg => Encoding.UTF8.GetString(msg.Value))
                                        .Subscribe(m => received.Add(m));
            _log.Info("Connecting consumer");
            await consumer.IsConnected;
            _log.Info("Subscribed to consumer");

            _log.Info("Starting sender");
            // send data, 5 msg/sec, for 5 seconds
            var sent = new HashSet<string>();
            var sender = Observable.Interval(TimeSpan.FromSeconds(1.0 / 5)).
                Select(i => string.Format("msg {0} {1}", i, Guid.NewGuid())).
                Synchronize().
                Do(m => sent.Add(m)).
                Select(msg => new Message
                {
                    Value = Encoding.UTF8.GetBytes(msg)
                }).
                TakeUntil(DateTimeOffset.Now.AddSeconds(5)).
                Publish().RefCount();
            sender.Subscribe(producer.Send);

            _log.Debug("Waiting for sender");
            await sender;
            _log.Debug("Waiting for producer complete");
            await producer.CloseAsync(TimeSpan.FromSeconds(4));

            // how to make sure nothing is sent after shutdown? listen to logger?  have connection events?

            // wait for 5sec for receiver to get all the messages
            _log.Info("Waiting for consumer to fetch");
            await Task.Delay(5000);
            _log.Info("Disposing consumer subscription");
            consumerSubscription.Dispose();
            _log.Info("Closing consumer");
            consumer.Dispose();
            _log.Info("Closed consumer");

            // assert we received all the messages

            Assert.AreEqual(sent.Count, received.Count, string.Format("Sent and Receved size differs. Sent: {0} Recevied: {1}", sent.Count, received.Count));
            // compare sets and not lists, because of 2 partitions, send order and receive orser are not the same
            Assert.True(received.SetEquals(sent), "Sent and Received set differs");

            kafka4net.Tracing.EtwTrace.Marker("/CleanShutdownTest");
        }

        //[Test]
        //public void DirtyShutdownTest()
        //{

        //}

        /// <summary>
        /// Shut down 2 brokers which will forse broker1 to become a leader.
        /// Publish 25K messages.
        /// Calculate sent message count as difference between heads and tails
        /// Start consumer and on 18th message start rebalance.
        /// Wait for consuming all messages but with 120sec timeout.
        /// 
        /// </summary>
        [Ignore("")]
        public async Task ConsumerFollowsRebalancingPartitions()
        {
            kafka4net.Tracing.EtwTrace.Marker("ConsumerFollowsRebalancingPartitions");

            // create a topic
            var topic = "topic33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic,11,3);

            // Stop two brokers to let leadership shift to broker1.
            VagrantBrokerUtil.StopBroker("broker2");
            VagrantBrokerUtil.StopBroker("broker3");

            await Task.Delay(TimeSpan.FromSeconds(5));

            // now start back up
            VagrantBrokerUtil.StartBroker("broker2");
            VagrantBrokerUtil.StartBroker("broker3");

            // wait a little for everything to start
            await Task.Delay(TimeSpan.FromSeconds(5));

            // we should have all of them with leader 1
            var cluster = new Cluster(_seed2Addresses);
            await cluster.ConnectAsync();
            var partitionMeta = await cluster.GetOrFetchMetaForTopicAsync(topic);

            // make sure they're all on a single leader
            Assert.AreEqual(1, partitionMeta.GroupBy(p=>p.Leader).Count());

            // now publish messages
            const int count = 25000;
            var producer = new Producer(cluster, new ProducerConfiguration(topic));
            _log.Debug("Connecting");
            await producer.ConnectAsync();

            _log.Debug("Filling out {0} with {1} messages", topic, count);
            var sentList = await Enumerable.Range(0, count)
                .Select(i => new Message { Value = BitConverter.GetBytes(i) })
                .ToObservable()
                .Do(producer.Send)
                .Select(msg => BitConverter.ToInt32(msg.Value, 0))
                .ToList();

            await Task.Delay(TimeSpan.FromSeconds(1));

            _log.Info("Done sending messages. Closing producer.");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Info("Producer closed, starting consumer subscription.");

            await Task.Delay(TimeSpan.FromSeconds(1));
            var heads = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            var tails = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            var messagesInTopic = (int)tails.MessagesSince(heads);
            _log.Info("Topic offsets indicate producer sent {0} messages.", messagesInTopic);


            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart(), maxBytesPerFetch: 4 * 8));
            var current = 0;
            var received = new ReplaySubject<ReceivedMessage>();
            Task rebalanceTask = null;
            var consumerSubscription = consumer.OnMessageArrived.
                Subscribe(async msg =>
                {
                    current++;
                    if (current == 18)
                    {
                        rebalanceTask = Task.Factory.StartNew(VagrantBrokerUtil.RebalanceLeadership, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                    }
                    received.OnNext(msg);
                    //_log.Info("Got: {0}", BitConverter.ToInt32(msg.Value, 0));
                });
            await consumer.IsConnected;

            _log.Info("Waiting for receiver complete");
            var receivedList = await received.Select(msg => BitConverter.ToInt32(msg.Value, 0)).
                Take(messagesInTopic).
                TakeUntil(DateTime.Now.AddMinutes(3)).
                ToList().
                ToTask();
            if (rebalanceTask != null)
            {
                _log.Info("Waiting for rebalance complete");
                await rebalanceTask;//.TimeoutAfter(TimeSpan.FromSeconds(10));
                _log.Info("Rebalance complete");
            }

            _log.Info("Receiver complete. Disposing Subscription");
            consumerSubscription.Dispose();
            _log.Info("Consumer subscription disposed. Closing consumer.");
            consumer.Dispose();
            _log.Info("Consumer closed.");

            tails = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            await cluster.CloseAsync(TimeSpan.FromSeconds(5));

            _log.Info("Sum of offsets: {0}", tails.MessagesSince(heads));
            _log.Info("Offsets: [{0}]", string.Join(",", tails.Partitions.Select(p => string.Format("{0}:{1}", p, tails.NextOffset(p)))));

            if (messagesInTopic != receivedList.Count)
            {
                // log some debug info.
                _log.Error("Did not receive all messages. Messages sent but NOT received: {0}", string.Join(",", sentList.Except(receivedList).OrderBy(i => i)));

            }

            Assert.AreEqual(messagesInTopic, receivedList.Count);


            kafka4net.Tracing.EtwTrace.Marker("/ConsumerFollowsRebalancingPartitions");
        }

        /// <summary>
        /// Send and listen for 100sec. Than wait for receiver to catch up for 10sec. 
        /// Compare sent and received message count.
        /// Make sure that messages were received in the same order as they were sent.
        /// </summary>
        [Test]
        public async Task KeyedMessagesPreserveOrder()
        {
            kafka4net.Tracing.EtwTrace.Marker("KeyedMessagesPreserveOrder");
            // create a topic with 3 partitions
            var topicName = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topicName, 3, 3);
            
            // create listener in a separate connection/broker
            var receivedMsgs = new List<ReceivedMessage>();
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topicName, new StartPositionTopicEnd()));
            var consumerSubscription = consumer.OnMessageArrived.Synchronize().Subscribe(msg =>
            {
                lock (receivedMsgs)
                {
                    receivedMsgs.Add(msg);
                }
            });
            await consumer.IsConnected;

            // sender is configured with 50ms batch period
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topicName, TimeSpan.FromMilliseconds(50)));
            await producer.ConnectAsync();

            //
            // generate messages with 100ms interval in 10 threads
            //
            var sentMsgs = new List<Message>();
            _log.Info("Start sending");
            var senders = Enumerable.Range(1, 1).
                Select(thread => Observable.
                    Interval(TimeSpan.FromMilliseconds(10)).
                    Synchronize(). // protect adding to sentMsgs
                    Select(i =>
                    {
                        var str = "msg " + i + " thread " + thread + " " + Guid.NewGuid();
                        var bin = Encoding.UTF8.GetBytes(str);
                        var msg = new Message
                        {
                            Key = BitConverter.GetBytes((int)(i + thread) % 10),
                            Value = bin
                        };
                        return Tuple.Create(msg, i, str);
                    }).
                    Subscribe(msg =>
                    {
                        lock (sentMsgs)
                        {
                            producer.Send(msg.Item1);
                            sentMsgs.Add(msg.Item1);
                            Assert.AreEqual(msg.Item2, sentMsgs.Count-1);
                        }
                    })
                ).
                ToArray();

            // wait for around 10K messages (10K/(10*10) = 100sec) and close producer
            _log.Info("Waiting for producer to produce enough...");
            await Task.Delay(100*1000);
            _log.Info("Closing senders intervals");
            senders.ForEach(s => s.Dispose());
            _log.Info("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            _log.Info("Waiting for additional 10sec");
            await Task.Delay(10*1000);

            _log.Info("Disposing consumer");
            consumerSubscription.Dispose();
            _log.Info("Closing consumer");
            consumer.Dispose();
            _log.Info("Done with networking");

            // compare sent and received messages
            // TODO: for some reason preformance is not what I'd expect it to be and only 6K is generated.
            Assert.GreaterOrEqual(sentMsgs.Count, 4000, "Expected around 10K messages to be sent");

            if (sentMsgs.Count != receivedMsgs.Count)
            {
                var sentStr = sentMsgs.Select(m => Encoding.UTF8.GetString(m.Value)).ToArray();
                var receivedStr = receivedMsgs.Select(m => Encoding.UTF8.GetString(m.Value)).ToArray();
                sentStr.Except(receivedStr).
                    ForEach(m => _log.Error("Not received: '{0}'", m));
                receivedStr.Except(sentStr).
                    ForEach(m => _log.Error("Not sent but received: '{0}'", m));
            }
            Assert.AreEqual(sentMsgs.Count, receivedMsgs.Count, "Sent and received messages count differs");
            
            //
            // group messages by key and compare lists in each key to be the same (order should be preserved within key)
            //
            var keysSent = sentMsgs.GroupBy(m => BitConverter.ToInt32(m.Key, 0), m => Encoding.UTF8.GetString(m.Value), (i, mm) => new { Key = i, Msgs = mm.ToArray() }).ToArray();
            var keysReceived = receivedMsgs.GroupBy(m => BitConverter.ToInt32(m.Key, 0), m => Encoding.UTF8.GetString(m.Value), (i, mm) => new { Key = i, Msgs = mm.ToArray() }).ToArray();
            Assert.AreEqual(10, keysSent.Count(), "Expected 10 unique keys 0-9");
            Assert.AreEqual(keysSent.Count(), keysReceived.Count(), "Keys count does not match");
            // compare order within each key
            var notInOrder = keysSent
                .OrderBy(k => k.Key)
                .Zip(keysReceived.OrderBy(k => k.Key), (s, r) => new { s, r, ok = s.Msgs.SequenceEqual(r.Msgs) }).Where(_ => !_.ok).ToArray();

            if (notInOrder.Any())
            {
                _log.Error("{0} keys are out of order", notInOrder.Count());
                notInOrder.ForEach(_ => _log.Error("Failed order in:\n{0}", 
                    string.Join(" \n", DumpOutOfOrder(_.s.Msgs, _.r.Msgs))));
            }
            Assert.IsTrue(!notInOrder.Any(), "Detected out of order messages");

            kafka4net.Tracing.EtwTrace.Marker("/KeyedMessagesPreserveOrder");
        }

        // take 100 items starting from the ones which differ
        static IEnumerable<Tuple<string, string>> DumpOutOfOrder(IEnumerable<string> l1, IEnumerable<string> l2)
        {
            return l1.Zip(l2, (l, r) => new {l, r}).
                SkipWhile(_ => _.l == _.r).Take(100).
                Select(_ => Tuple.Create(_.l, _.r));
        }

        [Test]
        public async Task ProducerSendBufferGrowsAutomatically()
        {
            kafka4net.Tracing.EtwTrace.Marker("ProducerSendBufferGrowsAutomatically");

            // now publish messages
            const int count2 = 25000;
            var topic = "part13." + _rnd.Next();
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            var sizeEvents = new List<Producer.QueueResizeInfo>();
            producer.QueueSizeEvents.Subscribe(_ =>
            {
                sizeEvents.Add(_);
                _log.Info($"Resized partition queue: {_}");
            });
            _log.Debug("Connecting");
            await producer.ConnectAsync();

            _log.Debug("Filling out {0} with {1} messages", topic, count2);
            var sentList = await Enumerable.Range(0, count2)
                .Select(i => new Message { Value = BitConverter.GetBytes(i) })
                .ToObservable()
                .Do(producer.Send)
                .Select(msg => BitConverter.ToInt32(msg.Value, 0))
                .ToList();

            await Task.Delay(TimeSpan.FromSeconds(1));

            _log.Info("Done sending messages. Closing producer.");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Info("Producer closed, starting consumer subscription.");

            Assert.GreaterOrEqual(sizeEvents.Count, 10);
            
            // create a topic with 3 partitions
            var topicName = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topicName, 3, 3);

            // sender is configured with 50ms batch period
            var receivedSubject = new ReplaySubject<Message>();
            producer = new Producer(_seed2Addresses,
                new ProducerConfiguration(topicName, TimeSpan.FromMilliseconds(50), sendBuffersInitialSize: 1));
            producer.OnSuccess += ms => ms.ForEach(receivedSubject.OnNext);
            await producer.ConnectAsync();

            // send 1000 messages
            const int count = 1000;
            await Observable.Interval(TimeSpan.FromMilliseconds(10))
                .Do(l => producer.Send(new Message() {Value = BitConverter.GetBytes((int) l)}))
                .Take(count);

            var receivedMessages = await receivedSubject.Take(count).TakeUntil(DateTime.Now.AddSeconds(2)).ToArray();

            Assert.AreEqual(count,receivedMessages.Length);

            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            
            kafka4net.Tracing.EtwTrace.Marker("/ProducerSendBufferGrowsAutomatically");
        }

        // explicit offset works
        [Test]
        public async Task ExplicitOffset()
        {
            kafka4net.Tracing.EtwTrace.Marker("ExplicitOffset");
            // create new topic with 3 partitions
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic,3,3);

            // fill it out with 10K messages
            const int count = 10*1000;
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            await producer.ConnectAsync();

            var sentMessagesObservable = Observable.FromEvent<Message[]>(evtHandler => producer.OnSuccess += evtHandler, evtHandler => { })
                .SelectMany(msgs=>msgs)
                .Take(count)
                .TakeUntil(DateTime.Now.AddSeconds(10))
                .ToList();

            _log.Info("Sending data");
            Enumerable.Range(1, count).
                Select(i => new Message { Value = BitConverter.GetBytes(i) }).
                ForEach(producer.Send);

            var sentMsgs = await sentMessagesObservable;
            _log.Info("Producer sent {0} messages.", sentMsgs.Count);

            _log.Debug("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Debug("Closed producer");

            var offsetFetchCluster = new Cluster(_seed2Addresses);
            await offsetFetchCluster.ConnectAsync();

            // consume tail-300 for each partition
            await Task.Delay(TimeSpan.FromSeconds(1));
            var offsets = new TopicPartitionOffsets(
                                topic, (await offsetFetchCluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd))
                                        .GetPartitionsOffset.Select(kv=>new KeyValuePair<int,long>(kv.Key,kv.Value-300)));
            _log.Info("Sum of offsets {0}. Raw: {1}",offsets.Partitions.Sum(p=>offsets.NextOffset(p)), offsets);
            
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, offsets));
            var messages = consumer.OnMessageArrived.
                GroupBy(m => m.Partition).Replay();
            messages.Connect();
            await consumer.IsConnected;

            var consumerSubscription = messages.Subscribe(p => p.Take(10).Subscribe(
                m => _log.Debug($"Got message {m.Partition}/{BitConverter.ToInt32(m.Value, 0)} offset: {m.Offset}"),
                e => _log.Error("Error", e),
                () => _log.Debug("Complete part {0}", p.Key)
            ));

            // wait for 3 partitions to arrrive and every partition to read at least 100 messages
            await messages.Select(g => g.Take(100)).Take(3).ToTask();

            _log.Debug("Unsubscribing from consumer");
            consumerSubscription.Dispose();
            _log.Debug("Closing consumer");
            await consumer.CloseAsync();

            kafka4net.Tracing.EtwTrace.Marker("/ExplicitOffset");
        }

        [Test]
        public async Task StopAtExplicitOffset()
        {
            kafka4net.Tracing.EtwTrace.Marker("StopAtExplicitOffset");
            // create new topic with 3 partitions
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 3, 3);

            // fill it out with 10K messages
            const int count = 10 * 1000;
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            await producer.ConnectAsync();

            var sentMessagesObservable = Observable.FromEvent<Message[]>(evtHandler => producer.OnSuccess += evtHandler, evtHandler => { })
                .SelectMany(msgs => msgs)
                .Take(count)
                .TakeUntil(DateTime.Now.AddMinutes(2))
                .ToList();

            _log.Info("Sending data");
            Enumerable.Range(1, count).
                Select(i => new Message { Value = BitConverter.GetBytes(i) }).
                ForEach(producer.Send);

            var sentMsgs = await sentMessagesObservable;
            _log.Info("Producer sent {0} messages.", sentMsgs.Count);
            Assert.AreEqual(count, sentMsgs.Count, "Sent  message count");

            _log.Debug("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(30));

            var offsetFetchCluster = new Cluster(_seed2Addresses);
            await offsetFetchCluster.ConnectAsync();

            await Task.Delay(TimeSpan.FromSeconds(1));
            var offsets = (await offsetFetchCluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd));
            _log.Info("Sum of offsets {0}. Raw: {1}", offsets.Partitions.Sum(p => offsets.NextOffset(p)), offsets);

            // consume first 300 for each partition
            var offsetStops = new TopicPartitionOffsets(topic, offsets.GetPartitionsOffset.ToDictionary(pair => pair.Key, pair => pair.Value > 300 ? 300 : pair.Value));
            var numMessages = offsetStops.Partitions.Sum(p => offsetStops.NextOffset(p));
            _log.Info("Attempting to consume {0} messages and stop at {1}", numMessages, offsetStops);

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart(), stopPosition : offsetStops));
            var messages = await consumer.OnMessageArrived.ToList();
            
            consumer.Dispose();

            Assert.AreEqual(numMessages, messages.Count);

            kafka4net.Tracing.EtwTrace.Marker("/StopAtExplicitOffset");
        }

        [Test]
        public async Task StartAndStopAtExplicitOffset()
        {
            kafka4net.Tracing.EtwTrace.Marker("StartAndStopAtExplicitOffset");
            // create new topic with 3 partitions
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 3, 3);

            // fill it out with 10K messages
            const int count = 10 * 1000;
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            await producer.ConnectAsync();

            var sentMessagesObservable = Observable.FromEvent<Message[]>(evtHandler => producer.OnSuccess += evtHandler, evtHandler => { })
                .SelectMany(msgs => msgs)
                .Take(count)
                .TakeUntil(DateTime.Now.AddSeconds(10))
                .ToList();

            _log.Info("Sending data");
            Enumerable.Range(1, count).
                Select(i => new Message { Value = BitConverter.GetBytes(i) }).
                ForEach(producer.Send);

            var sentMsgs = await sentMessagesObservable;
            _log.Info("Producer sent {0} messages.", sentMsgs.Count);

            _log.Debug("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            var offsetFetchCluster = new Cluster(_seed2Addresses);
            await offsetFetchCluster.ConnectAsync();

            await Task.Delay(TimeSpan.FromSeconds(1));
            var offsets = (await offsetFetchCluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd));
            _log.Info("Sum of offsets {0}. Raw: {1}", offsets.Partitions.Sum(p => offsets.NextOffset(p)), offsets);

            // consume first 300 for each partition
            var offsetStarts = new TopicPartitionOffsets(topic, offsets.GetPartitionsOffset.ToDictionary(pair => pair.Key, pair => pair.Value > 300 ? 300 : pair.Value));
            var offsetStops = new TopicPartitionOffsets(topic, offsets.GetPartitionsOffset.ToDictionary(pair => pair.Key, pair => pair.Value > 600 ? 600 : pair.Value));
            var numMessages = offsetStops.MessagesSince(offsetStarts);
            var startStopProvider = new StartAndStopAtExplicitOffsets(offsetStarts, offsetStops);
            _log.Info("Attempting to consume {0} messages and stop at {1}", numMessages, offsetStops);

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, startStopProvider, stopPosition: startStopProvider));
            var messages = await consumer.OnMessageArrived.ToList();

            consumer.Dispose();

            Assert.AreEqual(numMessages, messages.Count);

            kafka4net.Tracing.EtwTrace.Marker("/StartAndStopAtExplicitOffset");
        }

        [Test]
        public async Task StopAtExplicitOffsetOnEmptyTopic()
        {
            kafka4net.Tracing.EtwTrace.Marker("StopAtExplicitOffsetOnEmptyTopic");
            // create new topic with 3 partitions
            var topic = "part33." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 3, 3);

            var offsetFetchCluster = new Cluster(_seed2Addresses);
            await offsetFetchCluster.ConnectAsync();

            await Task.Delay(TimeSpan.FromSeconds(1));
            var offsets = (await offsetFetchCluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd));
            _log.Info("Sum of offsets {0}. Raw: {1}", offsets.Partitions.Sum(p => offsets.NextOffset(p)), offsets);

            var startStopProvider = new StartAndStopAtExplicitOffsets(offsets, offsets);

            _log.Info("Attempting to consume {0} messages and stop at {1}", 0, offsets);

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, startStopProvider, stopPosition: startStopProvider));
            var startTime = DateTime.Now;
            var timeout = startTime.AddSeconds(30);
            var messages = await consumer.OnMessageArrived.TakeUntil(timeout).ToList();
            _log.Info("Finished");
            Assert.IsTrue(DateTime.Now < timeout);
            Assert.AreEqual(0, messages.Count);
            consumer.Dispose();

            kafka4net.Tracing.EtwTrace.Marker("/StopAtExplicitOffsetOnEmptyTopic");
        }

        // can read from the head of queue
        [Test]
        public async Task ReadFromHead()
        {
            kafka4net.Tracing.EtwTrace.Marker("ReadFromHead");

            const int count = 100;
            var topic = "part32." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic,3,2);

            // fill it out with 100 messages
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            await producer.ConnectAsync();

            _log.Info("Sending data");
            Enumerable.Range(1, count).
                Select(i => new Message { Value = BitConverter.GetBytes(i) }).
                ForEach(producer.Send);

            _log.Debug("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            // read starting from the head
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart()));
            var count2 = await consumer.OnMessageArrived.TakeUntil(DateTimeOffset.Now.AddSeconds(5))
                //.Do(val=>_log.Info("received value {0}", BitConverter.ToInt32(val.Value,0)))
                .Count().ToTask();
            //await consumer.IsConnected;
            Assert.AreEqual(count, count2);

            kafka4net.Tracing.EtwTrace.Marker("/ReadFromHead");
        }

        // if attempt to fetch from offset out of range, excption is thrown
        //[Test]
        //public void OutOfRangeOffsetThrows()
        //{

        //}

        //// implicit offset is defaulted to fetching from the end
        //[Test]
        //public void DefaultPositionToTheTail()
        //{

        //}

        [Test]
        public void TopicPartitionOffsetsSerializeAndDeSerialize()
        {
            kafka4net.Tracing.EtwTrace.Marker("TopicPartitionOffsetsSerializeAndDeSerialize");

            var offsets1 = new TopicPartitionOffsets("test");

            for (int i = 0; i < 50; i++)
            {
                offsets1.UpdateOffset(i,_rnd.Next());
            }

            // save bytes
            var offsetBytes = offsets1.WriteOffsets();

            var offsets2 = new TopicPartitionOffsets(offsetBytes);

            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(offsets1.NextOffset(i),offsets2.NextOffset(i));
            }

            kafka4net.Tracing.EtwTrace.Marker("/TopicPartitionOffsetsSerializeAndDeSerialize");
        }

        [Test]
        public async Task SaveOffsetsAndResumeConsuming()
        {
            kafka4net.Tracing.EtwTrace.Marker("SaveOffsetsAndResumeConsuming");

            var sentEvents = new Subject<Message>();
            var topic = "part12." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 5, 2);

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            producer.OnSuccess += e => e.ForEach(sentEvents.OnNext);
            await producer.ConnectAsync();

            // send 100 messages
            Enumerable.Range(1, 100).
                Select(i => new Message { Value = BitConverter.GetBytes(i) }).
                ForEach(producer.Send);
            _log.Info("Waiting for 100 sent messages");
            sentEvents.Subscribe(msg => _log.Debug("Sent {0}", BitConverter.ToInt32(msg.Value, 0)));
            await sentEvents.Take(100).ToTask();


            var offsets1 = await producer.Cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);

            _log.Info("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            // now consume the "first" 50. Stop, save offsets, and restart.
            var consumer1 = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, offsets1));
            var receivedEvents = new List<int>(100);

            _log.Info("Consuming first half of messages.");

            await consumer1.OnMessageArrived
                .Do(msg =>
                {
                    var value = BitConverter.ToInt32(msg.Value, 0);
                    _log.Info("Consumer1 Received value {0} from partition {1} at offset {2}", value, msg.Partition, msg.Offset);
                    receivedEvents.Add(value);
                    offsets1.UpdateOffset(msg.Partition, msg.Offset);
                })
                .Take(50);
            //await consumer1.IsConnected;

            _log.Info("Closing first consumer");
            consumer1.Dispose();

            // now serialize the offsets.
            var offsetBytes = offsets1.WriteOffsets();

            // load a new set of offsets, and a new consumer
            var offsets2 = new TopicPartitionOffsets(offsetBytes);

            var consumer2 = new Consumer(new ConsumerConfiguration(_seed2Addresses, offsets2.Topic, offsets2));

            await consumer2.OnMessageArrived
                .Do(msg =>
                {
                    var value = BitConverter.ToInt32(msg.Value, 0);
                    _log.Info("Consumer2 Received value {0} from partition {1} at offset {2}", value, msg.Partition, msg.Offset);
                    receivedEvents.Add(value);
                    offsets2.UpdateOffset(msg.Partition, msg.Offset);
                })
                .Take(50);
            //await consumer2.IsConnected;

            _log.Info("Closing second consumer");
            consumer2.Dispose();

            Assert.AreEqual(100, receivedEvents.Distinct().Count());
            Assert.AreEqual(100, receivedEvents.Count);

            kafka4net.Tracing.EtwTrace.Marker("/SaveOffsetsAndResumeConsuming");
        }

        // Create a new 1-partition topic and sent 100 messages.
        // Read offsets, they should be [0, 100]
        [Test]
        public async Task ReadOffsets()
        {
            kafka4net.Tracing.EtwTrace.Marker("ReadOffsets");

            var sentEvents = new Subject<Message>();
            var topic = "part12." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic,1,1);

            var cluster = new Cluster(_seed2Addresses);
            await cluster.ConnectAsync();
            var producer = new Producer(cluster, new ProducerConfiguration(topic, maxMessageSetSizeInBytes: 1024*1024));
            producer.OnSuccess += e => e.ForEach(sentEvents.OnNext);

            await producer.ConnectAsync();

            // read offsets of empty queue
            var heads = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            var tails = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);
            Assert.AreEqual(1, heads.Partitions.Count(), "Expected just one head partition");
            Assert.AreEqual(1, tails.Partitions.Count(), "Expected just one tail partition");
            Assert.AreEqual(0L, heads.NextOffset(heads.Partitions.First()), "Expected start at 0");
            Assert.AreEqual(0L, tails.NextOffset(tails.Partitions.First()), "Expected end at 0");

            // log the broker selected as master
            var brokerMeta = cluster.FindBrokerMetaForPartitionId(topic, heads.Partitions.First());
            _log.Info("Partition Leader is {0}", brokerMeta);


            // saw some inconsistency, so run this a few times.
            const int count = 1100;
            const int loops = 10;
            for (int i = 0; i < loops; i++)
            {
                // NOTE that the configuration for the test machines through vagrant are set to 1MB rolling file segments
                // so we need to generate large messages to force multiple segments to be created.

                // send count messages
                var t = sentEvents.Take(count).ToTask();
                Enumerable.Range(1, count).
                    Select(_ => new Message { Value = new byte[1024] }).
                    ForEach(producer.Send);
                _log.Info("Waiting for {0} sent messages", count);
                await t;

                // re-read offsets after messages published
                await Task.Delay(TimeSpan.FromSeconds(2)); // NOTE: There seems to be a race condition on the Kafka broker that the offsets are not immediately available after getting a successful produce response 
                tails = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);
                _log.Info("2:After loop {0} of {1} messages, Next Offset is {2}", i + 1, count, tails.NextOffset(tails.Partitions.First()));
                Assert.AreEqual(count * (i + 1), tails.NextOffset(tails.Partitions.First()), "Expected end at " + count * (i + 1));

            }

            _log.Info("Closing producer");
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            await Task.Delay(TimeSpan.FromSeconds(1));

            // re-read offsets after messages published
            heads = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            tails = await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicEnd);

            Assert.AreEqual(1, heads.Partitions.Count(), "Expected just one head partition");
            Assert.AreEqual(1, tails.Partitions.Count(), "Expected just one tail partition");
            Assert.AreEqual(0L, heads.NextOffset(heads.Partitions.First()), "Expected start at 0");
            Assert.AreEqual(count*loops, tails.NextOffset(tails.Partitions.First()), "Expected end at " + count);

            kafka4net.Tracing.EtwTrace.Marker("/ReadOffsets");
        }

        [Test]
        public async Task TwoConsumerSubscribersOneBroker()
        {
            kafka4net.Tracing.EtwTrace.Marker("TwoConsumerSubscribersOneBroker");

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, "part33", new StartPositionTopicEnd()));
            var msgs = consumer.OnMessageArrived.Publish().RefCount();
            var t1 = msgs.TakeUntil(DateTimeOffset.Now.AddSeconds(5)).LastOrDefaultAsync().ToTask();
            var t2 = msgs.TakeUntil(DateTimeOffset.Now.AddSeconds(6)).LastOrDefaultAsync().ToTask();
            //await consumer.IsConnected;
            await Task.WhenAll(new[] { t1, t2 });
            consumer.Dispose();

            kafka4net.Tracing.EtwTrace.Marker("/TwoConsumerSubscribersOneBroker");
        }

        [Test]
        public async Task MultipleProducersOneCluster()
        {
            kafka4net.Tracing.EtwTrace.Marker("MultipleProducersOneCluster");

            var cluster = new Cluster(_seed2Addresses);
            var topic1 = "topic." + _rnd.Next();
            var topic2 = "topic." + _rnd.Next();

            VagrantBrokerUtil.CreateTopic(topic1, 6, 3);
            VagrantBrokerUtil.CreateTopic(topic2, 6, 3);

            // declare two producers
            var producer1 = new Producer(cluster, new ProducerConfiguration(topic1));
            await producer1.ConnectAsync();

            var producer2 = new Producer(cluster, new ProducerConfiguration(topic2));
            await producer2.ConnectAsync();

            // run them both for a little while (~10 seconds)
            var msgs = await Observable.Interval(TimeSpan.FromMilliseconds(100))
                .Do(l =>
            {
                producer1.Send(new Message {Value = BitConverter.GetBytes(l)});
                producer2.Send(new Message {Value = BitConverter.GetBytes(l)});

            }).Take(100);

            _log.Info("Done Sending, await on producer close.");

            // now stop them.
            await Task.WhenAll(new [] { producer1.CloseAsync(TimeSpan.FromSeconds(5)), producer2.CloseAsync(TimeSpan.FromSeconds(5)) });

            await Task.Delay(TimeSpan.FromSeconds(2));

            // check we got all 100 on each topic.
            _log.Info("Closed Producers. Checking Offsets");
            var topic1Heads = await cluster.FetchPartitionOffsetsAsync(topic1, ConsumerLocation.TopicStart);
            var topic2Heads = await cluster.FetchPartitionOffsetsAsync(topic2, ConsumerLocation.TopicStart);
            var topic1Tails = await cluster.FetchPartitionOffsetsAsync(topic1, ConsumerLocation.TopicEnd);
            var topic2Tails = await cluster.FetchPartitionOffsetsAsync(topic2, ConsumerLocation.TopicEnd);

            Assert.AreEqual(100, topic1Tails.MessagesSince(topic1Heads));
            Assert.AreEqual(100, topic2Tails.MessagesSince(topic2Heads));
            
            kafka4net.Tracing.EtwTrace.Marker("/MultipleProducersOneCluster");
        }

        [Test]
        public async Task SchedulerThreadIsIsolatedFromUserCode()
        {
            kafka4net.Tracing.EtwTrace.Marker("SchedulerThreadIsIsolatedFromUserCode");

            //const string threadName = "kafka-scheduler";
            _log.Info("Test Runner is using thread {0}", Thread.CurrentThread.Name);

            var topic = "topic." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic,6,3);

            var cluster = new Cluster(_seed2Addresses);
            await cluster.ConnectAsync();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            await cluster.FetchPartitionOffsetsAsync(topic, ConsumerLocation.TopicStart);
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            var topics = await cluster.GetAllTopicsAsync();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            // now create a producer
            var producer = new Producer(cluster, new ProducerConfiguration(topic));
            await producer.ConnectAsync();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            // create a producer that also creates a cluster
            var producerWithCluster = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
            await producerWithCluster.ConnectAsync();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            // TODO: Subscribe and check thread on notification observables!

            // run them both for a little while (~5 seconds)
            var msgs = await Observable.Interval(TimeSpan.FromMilliseconds(100))
                .Do(l =>
                {
                    producer.Send(new Message { Value = BitConverter.GetBytes(l) });
                    producerWithCluster.Send(new Message { Value = BitConverter.GetBytes(l) });
                    _log.Debug("After Producer Send using thread {0}", Thread.CurrentThread.Name);

                }).Take(50).ToArray();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            // now consumer(s)
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart()));
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            var msgsRcv = new List<long>();
            var messageSubscription = consumer.OnMessageArrived
                .Do(
                    msg => Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread),
                    exception => Assert.AreEqual(cluster.CurrentWorkerThread, Thread.CurrentThread), 
                    () => Assert.AreEqual(cluster.CurrentWorkerThread, Thread.CurrentThread))
                .Take(50)
                .TakeUntil(DateTime.Now.AddSeconds(500))
                .ObserveOn(System.Reactive.Concurrency.DefaultScheduler.Instance)
                .Do(
                    msg => Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread), 
                    exception => Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread), 
                    () => Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread))
                .Subscribe(
                    msg=>
                    {
                        msgsRcv.Add(BitConverter.ToInt64(msg.Value,0));
                        Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);
                        _log.Debug("In Consumer Subscribe OnNext using thread {0}", Thread.CurrentThread.Name);
                    }, exception =>
                    {
                        _log.Debug(exception, $"In Consumer Subscribe OnError using thread {Thread.CurrentThread.Name}");
                        throw exception;
                    }, () =>
                    {
                        Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);
                        _log.Debug("In Consumer Subscribe OnComplete using thread {0}", Thread.CurrentThread.Name);
                    });
            
            await consumer.IsConnected;

            _log.Info("Waitng for consumer to read");
            await Task.Delay(TimeSpan.FromSeconds(6));
            _log.Debug("After Consumer Subscribe using thread {0}", Thread.CurrentThread.Name);
            consumer.Dispose();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            Assert.AreEqual(msgs.Length, msgsRcv.Count);

            messageSubscription.Dispose();
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            // now close down
            await producer.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Debug("After Consumer Close using thread {0}", Thread.CurrentThread.Name);
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            await producerWithCluster.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Debug("After Producer Subscribe using thread {0}", Thread.CurrentThread.Name);
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            await cluster.CloseAsync(TimeSpan.FromSeconds(5));
            _log.Debug("After Cluster Close using thread {0}", Thread.CurrentThread.Name);
            Assert.AreNotEqual(cluster.CurrentWorkerThread, Thread.CurrentThread);

            kafka4net.Tracing.EtwTrace.Marker("/SchedulerThreadIsIsolatedFromUserCode");
        }

        [NUnit.Framework.Ignore("")]
        public async Task SlowConsumer()
        {
            //
            // 1. Create a topic with 100K messages.
            // 2. Create slow consumer and start consuming at rate 1msg/sec
            // 3. ???
            //
            var topic = "test32." + _rnd.Next();

            await FillOutQueue(topic, (int)100e3);

            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic,
                new StartPositionTopicStart(),
                maxBytesPerFetch: 1024, 
                lowWatermark:20, highWatermark: 200, useFlowControl: true));

            var readWaiter = new SemaphoreSlim(0, 1);
            var share = consumer.OnMessageArrived.Publish().RefCount();
            var sub1 = share.Subscribe(msg =>
            {
                var str = Encoding.UTF8.GetString(msg.Value);
                _log.Debug("Received msg '{0}'", str);
                Thread.Sleep(100);
                consumer.Ack();
            }, e => _log.Error("Consumer error", e), () => readWaiter.Release());
            
            await consumer.IsConnected;

            // 2nd subscriber to test that Consumer does not count 2x of message delivery rate
            var sub2 = share.Subscribe(_ => { }, e => { throw e; });

            _log.Debug("Waiting for reader");
            await readWaiter.WaitAsync();
            _log.Debug("Reader complete");
        }

        [Test]
        public async Task InvalidOffsetShouldLogErrorAndStopFetching()
        {
            var count = 100;
            var topic = "test11."+_rnd.Next();
            await FillOutQueue(topic, count);

            Assert.ThrowsAsync<PartitionFailedException>(async () =>
            {
                var badPartitionMap = new Dictionary<int, long> { { 0, -1 } };
                var offsets = new TopicPartitionOffsets(topic, badPartitionMap);
                var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, offsets));
                await consumer.OnMessageArrived.Take(count);
                await consumer.IsConnected;
                _log.Info("Done");
            });
        }

        private async Task FillOutQueue(string topic, int count)
        {
            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic, TimeSpan.FromSeconds(20)));

            _log.Debug("Connecting producer");
            await producer.ConnectAsync();

            _log.Info("Starting sender");
            var sender = Observable.Range(1, count).
                Select(i => string.Format("msg {0} {1}", i, Guid.NewGuid())).
                Synchronize().
                Select(msg => new Message
                {
                    Value = Encoding.UTF8.GetBytes(msg)
                }).
                Publish().RefCount();
            sender.Subscribe(producer.Send);

            _log.Debug("Waiting for sender");
            await sender.LastOrDefaultAsync().ToTask();
            _log.Debug("Waiting for producer complete");
            await producer.CloseAsync(TimeSpan.FromSeconds(4));
            _log.Debug("Producer complete");
        }

        [Test]
        [Timeout(10*1000)]
        public void InvalidDnsShouldThrowException()
        {
            Assert.ThrowsAsync<BrokerException>(async () =>
            {
                var consumer = new Consumer(new ConsumerConfiguration("no.such.name.123.org", "some.topic", new StartPositionTopicEnd()));
                consumer.OnMessageArrived.Subscribe(_ => { });
                await consumer.IsConnected;
            });
        }

        [Test]
        [Timeout(10 * 1000)]
        public async Task OneInvalidDnsShouldNotThrow()
        {
            var seed = "no.such.name.123.org,"+_seed2Addresses;
            var consumer = new Consumer(new ConsumerConfiguration(seed, "some.topic", new StartPositionTopicEnd()));
            consumer.OnMessageArrived.Subscribe(_ => { });
            await consumer.IsConnected;
        }

        [Test]
        public async Task SimulateLongBufferedMessageHandling()
        {
            var count = 2000;
            var topic = "topic11." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 1, 1);

            var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic, batchFlushSize: 2));
            await producer.ConnectAsync();

            var src = Observable.Interval(TimeSpan.FromMilliseconds(10)).Take(count).
                Select(i => new Message { Value = BitConverter.GetBytes((int)i) }).Publish().RefCount();
            src.Subscribe(producer.Send);
            await src;

            await Task.Delay(200);
            await producer.CloseAsync(TimeSpan.FromSeconds(5));

            _log.Debug("Start consumer");
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicStart()));
            var stream = consumer.OnMessageArrived.
                Buffer(TimeSpan.FromSeconds(1), count / 4).
                Where(_ => _.Count > 0).
                Select(i =>
                {
                    _log.Debug("Handling batch {0} ...", i.Count);
                    Thread.Sleep(65*1000);
                    _log.Debug("Complete batch");
                    return i.Count;
                }).
                Scan(0, (i, i1) =>
                {
                    _log.Debug("Scnning {0} {1}", i, i1);
                    return i + i1;
                }).
                Do(i => _log.Debug("Do {0}", i)).
                Where(i => i == count).FirstAsync().ToTask();
            await consumer.IsConnected;

            _log.Debug("Awaiting for consumer");
            var count2 = await stream;
            consumer.Dispose();
            Assert.AreEqual(count, count2);
            _log.Info("Complete");
        }

        [Test]
        [Timeout(6*60*1000)]
        public async Task ProducerConnectWhenOneBrokerIsDownAndThanUp()
        {
            // Scenario: 1 broker is down, Producer connects. Broker is brought up and forced to become master.
            // See https://github.com/ntent-ad/kafka4net/issues/14

            var topic = "topic11." + _rnd.Next();

            // Create topic
            VagrantBrokerUtil.CreateTopic(topic, 1, 2);
            var cluster = new Cluster(_seed3Addresses);
            await cluster.ConnectAsync();
            await cluster.GetOrFetchMetaForTopicAsync(topic);
            VagrantBrokerUtil.DescribeTopic(topic);
            // Stop the leader
            //var partitionDown = cluster.PartitionStateChanges.FirstAsync(_ => _.ErrorCode.IsFailure());
            var preferredBroker = VagrantBrokerUtil.StopBrokerLeaderForPartition(cluster, topic, 0);
            //_log.Info("Waiting for partition to be down");
            //await partitionDown;
            await cluster.CloseAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(30 * 1000);
            VagrantBrokerUtil.DescribeTopic(topic);

            // Create new cluster and publisher, while preferred leader is down
            cluster = new Cluster(_seed3Addresses);
            cluster.NewBrokers.Subscribe(b => _log.Info("Discovered new broker: {0}", b));
            _log.Info("Connecting cluster");
            await cluster.ConnectAsync();
            var producer = new Producer(cluster, new ProducerConfiguration(topic));
            _log.Info("Connecting producer");
            await producer.ConnectAsync();

            // Start preferred leader up
            _log.Info("Starting preferred broker");
            VagrantBrokerUtil.StartBroker(preferredBroker);
            _log.Info("Waiting for preferred broker ({0}) to start up", preferredBroker);
            await Task.Delay(30 * 1000);
            //VagrantBrokerUtil.RebalanceLeadership();
            _log.Info("Stopping 2nd leader broker");
            VagrantBrokerUtil.StopBrokerLeaderForPartition(cluster, topic, 0);
            _log.Info("Producer Send data");
            producer.Send(new Message() { Value = new byte[]{0,0,0,0}});
            _log.Info("Waiting for producer to complete");
            await producer.CloseAsync(TimeSpan.FromSeconds(60));

            _log.Info("Done");
        }




        [Test]
        [Timeout(6 * 60 * 1000)]
        public async Task ProducerTestWhenPartitionReassignmentOccurs()
        {
            // Scenario: Broker gives away it's topic to another broker
            // See https://github.com/ntent-ad/kafka4net/issues/27

            var topic = "topic11." + _rnd.Next();

            // Create topic
            VagrantBrokerUtil.CreateTopic(topic, 1, 1);

            // Create cluster and producer
            var cluster = new Cluster(_seed2Addresses);
            //await cluster.ConnectAsync();
            //await cluster.GetOrFetchMetaForTopicAsync(topic);
            VagrantBrokerUtil.DescribeTopic(topic);
            var producer = new Producer(cluster, new ProducerConfiguration(topic, batchFlushSize: 1));
            var ctx = SynchronizationContext.Current;
            producer.OnPermError += (exception, messages) => ctx.Post(d => { throw exception; }, null);
            int successfullySent = 0;
            producer.OnSuccess += messages => successfullySent++;

            _log.Info("Connecting producer");
            await producer.ConnectAsync();

            _log.Info("Producer Send data before reassignment");
            producer.Send(new Message { Value = new byte[] { 0, 0, 0, 0 } });


            // Run the reassignment
            VagrantBrokerUtil.ReassignPartitions(cluster, topic, 0);
            _log.Info("Waiting for reassignment completion");
            await Task.Delay(5 * 1000);

            VagrantBrokerUtil.DescribeTopic(topic);

            _log.Info("Producer Send data after reassignment");
            producer.Send(new Message { Value = new byte[] { 1, 1, 1, 1 } });

            _log.Info("Waiting for producer to complete");
            await producer.CloseAsync(TimeSpan.FromSeconds(60));

            Assert.That(successfullySent, Is.EqualTo(2));
            _log.Info("Done");
        }

        // if last leader is down, all in-buffer messages are errored and the new ones
        // are too.

        // How to test timeout error?

        // shutdown dirty: connection is off while partially saved. Make sure that 
        // message is either committed or errored

        // If connecting async, messages are saved and than sent

        // if connecting async but never can complete, timeout triggers, and messages are errored

        // if one broker refuses connection, another one will connect and function
        [Test]
        [Timeout(60*1000)]
        public async Task IfFirstBrokerIsDownThenNextOneWillConnect()
        {
            var badSeed = "192.168.56.111," + _seed2Addresses;
            var cluster = new Cluster(badSeed);
            await cluster.ConnectAsync();
            await cluster.GetAllTopicsAsync();
            //var producer = new Producer(cluster, new ProducerConfiguration("notopic"));
            //await producer.ConnectAsync();
            await cluster.CloseAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Related to https://github.com/ntent-ad/kafka4net/issues/23
        /// Not a real unit-test, just debug helper for memory issues.
        /// </summary>
        [Ignore("")]
        public async Task Memory()
        {
            var topic = "topic11." + _rnd.Next();
            VagrantBrokerUtil.CreateTopic(topic, 1, 1);

            for (int i = 0; i < 10; ++i)
            {
                var producer = new Producer(_seed2Addresses, new ProducerConfiguration(topic));
                await producer.ConnectAsync();

                for (int j = 0; j < (int)1e6; j++)
                {
                    var msg = new Message
                    {
                        Key = BitConverter.GetBytes(1),
                        Value = Encoding.UTF8.GetBytes($@"SomeLongText - {j}")
                    };

                    producer.Send(msg);
                }

                await producer.CloseAsync(TimeSpan.FromSeconds(60));
            }

            var before = GC.GetTotalMemory(false);
            GC.Collect();
            var after = GC.GetTotalMemory(false);
            await Task.Delay(3000);
            GC.Collect();
            GC.WaitForFullGCComplete();
            _log.Info($"Memory: Before: {before}, after: {after}");
        }

        // if one broker hangs on connect, client will be ready as soon as connected via another broker

        // Short disconnect (within timeout) wont lose any messages and will deliver all of them.
        // Temp error will be triggered

        // Parallel producers send messages to proper topics

        // Test non-keyed messages. What to test?

        // Big message batching does not cause too big payload exception

        // when kafka delete is implemented, test deleting topic cause delete metadata in driver
        // and proper message error

        // Analize correlation example
        // C:\funprojects\rx\Rx.NET\Samples\EventCorrelationSample\EventCorrelationSample\Program.cs 

        // Adaptive timeout and buffer size on fetch?

        // If connection lost, recover. But if too frequent, do not loop infinitely 
        // establishing connection but fail permanently.

        // When one consumer fails and recovers (leader changed), another, inactive one will
        // update its connection mapping just by listening to changes in routing table and not
        // though error and recovery, when it becomes active.

        // The same key sends message to be in the same partition

        // If 2 consumers subscribed, and than unsubscribed, fetcher must stop pooling.

        // Kafka bug? Fetch to broker that has topic but not the partition, returns no error for partition, but [-1,-1,0] offsets

        // Do I need SynchronizationContext in addition to EventLoopScheduler when using async?

        // When server close tcp socket, Fetch will wait until timeout. It would be better to 
        // react to connection closure immediatelly.

        // Sending messages to Producer after shutdown causes error

        // Clean shutdown doe not produce shutdown error callbacks

        // OnSuccess is fired if topic was autocreated, there was no errors, there were 1 or more errors with leaders change

        // For same key, order of the messages is preserved

        //
        // Compression test
        //

        [Category("Compression")]
        [Test(Description = "Make sure it's possible to uncompress Java generated gzip-ed messages")]
        [Repeat(5)]
        public async Task UncompressJavaGeneratedMessage([Values("gzip","lz4","snappy")]string codec)
        {
            var topic = "topic11.java.compressed";
            var hashFileName = AppDomain.CurrentDomain.BaseDirectory + @"..\..\..\vagrant\files\hashes.txt";

            //
            // Start consumer
            //
            var hashes = new List<string>();
            var consumer = new Consumer(new ConsumerConfiguration(_seed2Addresses, topic, new StartPositionTopicEnd(), 
                maxWaitTimeMs: 10*1000, 
                maxBytesPerFetch:10*1024*1024));
            var hash = MD5.Create();
            var sizes = GenerateMessageSizesAndWriteToFile();

            var gotHashesTask = consumer.OnMessageArrived.
                Select(msg => hash.ComputeHash(msg.Value)).
                Select(buff => string.Join("", buff.Select(_ => _.ToString("X2")).ToArray())).
                Take(sizes.Length).
                Timeout(TimeSpan.FromMinutes(3)).
                Do(_ => hashes.Add(_)).
                Do(_ => _log.Debug("Got message")).
                ToList().ToTask();
                //Subscribe(msg => hashes.Add(msg));
            await consumer.IsConnected;

            VagrantBrokerUtil.GenerateMessagesWithJava(codec, topic);

            try {
                var gotHashes = await gotHashesTask;
                var javaHashes = File.ReadAllLines(hashFileName);
                Assert.IsTrue(gotHashes.SequenceEqual(javaHashes), "Hashes mismatch");
            } catch(Exception)
            {
                _log.Info($"Got ${hashes.Count} messages");
                throw;
            }
        }

        readonly string _sizesFileName = AppDomain.CurrentDomain.BaseDirectory + @"..\..\..\vagrant\files\sizes.txt";
        int[] GenerateMessageSizesAndWriteToFile()
        {
            var sizes =
                Enumerable.Range(1, 1024 + 10).
                Concat(
                    from i in new[] { 5, 10, 16, 50, 64, 256, 500 }   // message size in Kb
                    // Create 100 messages of slightly different size (+/-100 bytes)
                    from rnd in Enumerable.Range(1, 100).Select(_ => _rnd.Next(-100, 100))
                    select i * 1024 + rnd
                ).ToArray();

            File.WriteAllLines(_sizesFileName, sizes.Select(_=>_.ToString()));
            return sizes;
        }

        // Test .net compressed can be read by java
        [Category("Compression")]
        [Test(Description = "Java can read .net-compressed messages")]
        public async Task JavaCanReadCompressedMessages([Values("gzip", "lz4", "snappy")]string codec)
        {
            var rnd = new Random(0);
            var topic = "topic31.csharp.compressed-16";
            VagrantBrokerUtil.CreateTopic(topic, 3, 1);
            var server = //"localhost"; 
                        _seed3Addresses;

            var consumedMessages = new List<int>();
            var consumer = new Consumer(new ConsumerConfiguration(server, topic, new StartPositionTopicEnd(), 
                maxBytesPerFetch: 500*1024*1024, maxWaitTimeMs:10*1000));
            consumer.OnMessageArrived.
                //Synchronize().
                Subscribe(msg =>
                {
                    consumedMessages.Add(msg.Value.Length);
                    _log.Info($">>> {consumedMessages.Count}");
                });
            await consumer.IsConnected;

            var sizes = GenerateMessageSizesAndWriteToFile();
            int successCount = 0;
            var producer = new Producer(server, new ProducerConfiguration(topic, maxMessageSetSizeInBytes: 40*1000*1000) {
                CompressionType = (CompressionType)Enum.Parse(typeof(CompressionType), codec, true)
            });
            //var rnd = new Random(1);
            var sharpHashes = new List<string>();
            var md5 = MD5.Create();

            producer.OnPermError += (exception, messages) => {
                _log.Error("Message len: {0} Error: {1}", string.Join(",", messages.Select(_=>_.Value.Length.ToString()).ToArray()), exception.Message );
            };

            producer.OnSuccess += messages =>
            {
                Interlocked.Add(ref successCount, messages.Length);
            };

            var javaConsumer = StartJavaConsumer(topic);
            await Task.Delay(5 * 1000);

            await producer.ConnectAsync();

            var key = new byte[] { 0 };

            _log.Info("Start sending...");
            sizes.Select(size => {
                var buff = new byte[size];
                rnd.NextBytes(buff);
                var hash = string.Join("", md5.ComputeHash(buff).Select(_ => _.ToString("X2")).ToArray());
                sharpHashes.Add(hash);
                return buff;
            }).
            Select(buff => new Message { Value = buff, Key = key}).
            ForEach(msg => {
                producer.Send(msg);
            });

            await producer.CloseAsync(TimeSpan.FromMinutes(1));
            _log.Info("Sent {0} messages", successCount);
            Assert.AreEqual(sizes.Length, successCount, "Message count is not equal to sent count");

            _log.Info("Waiting for java process");
            await javaConsumer;

            _log.Info("Comparing hashes");
            var javaHashes = File.ReadLines(AppDomain.CurrentDomain.BaseDirectory + @"..\..\..\vagrant\files\hashes.txt").ToArray();
            Assert.IsTrue(sharpHashes.SequenceEqual(javaHashes), "Hashes mismatch");
        }

        // Test that .net can decompress what it has compressed (self-compatibility)

        public Task StartJavaConsumer(string topic)
        {
            var path = AppDomain.CurrentDomain.BaseDirectory;
            path = Path.Combine(path, @"..\..\..\vagrant\files\binary-console-all.jar");
            path = Path.GetFullPath(path);
            var process = Process.Start("C:\\Program Files\\Java\\jdk1.8.0_73\\bin\\java.exe", "-jar " + path + $" {topic} gzip consume");
            return Task.Run(() => process.WaitForExit());
        }
    }
}
