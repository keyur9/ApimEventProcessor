﻿using Microsoft.ServiceBus.Messaging;
using Runscope.Links;
using Runscope.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace ApimEventProcessor
{
    /// <summary>
    ///  Allows the EventProcessor instances to have services injected into the constructor
    /// </summary>
    public class ApimHttpEventProcessorFactory : IEventProcessorFactory
    {
        private IHttpMessageProcessor _HttpMessageProcessor;
        private ILogger _Logger;

        public ApimHttpEventProcessorFactory(IHttpMessageProcessor httpMessageProcessor, ILogger logger)
        {
            _HttpMessageProcessor = httpMessageProcessor;
            _Logger = logger;
        }

        public IEventProcessor CreateEventProcessor(PartitionContext context)
        {
            return new ApimEventProcessor(_HttpMessageProcessor, _Logger);
        }
    }

  
  
    /// <summary>
    /// Accepts EventData from EventHubs, converts to a HttpMessage instances and forwards it to a IHttpMessageProcessor
    /// </summary>
    public class ApimEventProcessor : IEventProcessor
    {
        Stopwatch checkpointStopWatch;
        private ConcurrentQueue<string> _Queue = new ConcurrentQueue<string>();
        private Task _DequeueTask;
        private bool _running = true;
        private ILogger _Logger;
        private IHttpMessageProcessor _MessageContentProcessor;

        public ApimEventProcessor(IHttpMessageProcessor messageContentProcessor, ILogger logger)
        {
            _MessageContentProcessor = messageContentProcessor;
            _Logger = logger;
            _DequeueTask = Task.Factory.StartNew(() => ProcessQueue(), TaskCreationOptions.LongRunning);
        }

  
        async Task IEventProcessor.ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> messages)
        {

            foreach (EventData eventData in messages)
            {
                string message = Encoding.UTF8.GetString(eventData.GetBytes());
                _Queue.Enqueue(message);

                _Logger.LogInfo(string.Format("Event received from partition: '{0}'", context.Lease.PartitionId));
            }

            //Call checkpoint every 5 minutes, so that worker can resume processing from the 5 minutes back if it restarts.
            if (this.checkpointStopWatch.Elapsed > TimeSpan.FromMinutes(5))
            {
                _Logger.LogInfo("Checkpointing");
               await context.CheckpointAsync();
                this.checkpointStopWatch.Restart();
            }
        }

        // Method to drain the Queue of received messages
        // This is done in a long running task, to avoid the ProcessAsync method from taking a long time to execute
        private void ProcessQueue()
        {
            while (_running)
            {
                if (_Queue.Count > 0)
                {
                    string message;
                    if (_Queue.TryDequeue(out message))
                    {
                        ProcessEvent(message).Wait();
                    }
                }
                else
                {
                    // If no messages are in the queue, wait for 5 secs to see if some arrive
                    _Logger.LogDebug("Waiting for events");
                    Task.Delay(5000).Wait();
                }
            }
        }

        /// <summary>
        /// Process Queued Message
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task ProcessEvent(string data)
        {
            _Logger.LogDebug("Processing Event");

            HttpMessage httpMessage;

            try {

                httpMessage = HttpMessage.Parse(data);

            } catch(ArgumentException ex)
            {
                _Logger.LogError(ex.Message);
                return;
            }
            
            await _MessageContentProcessor.ProcessHttpMessage(httpMessage);

        }


        async Task IEventProcessor.CloseAsync(PartitionContext context, CloseReason reason)
        {
            _running = false;
            _Logger.LogInfo("Processor Shutting Down. Partition '{0}', Reason: '{1}'.", context.Lease.PartitionId, reason);
            if (reason == CloseReason.Shutdown)
            {
                await context.CheckpointAsync();
            }
        }

        Task IEventProcessor.OpenAsync(PartitionContext context)
        {
            _Logger.LogInfo("SimpleEventProcessor initialized.  Partition: '{0}', Offset: '{1}'", context.Lease.PartitionId, context.Lease.Offset);
            this.checkpointStopWatch = new Stopwatch();
            this.checkpointStopWatch.Start();
            return Task.FromResult<object>(null);
        }

    }



}