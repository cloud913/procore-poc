using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Worker
{
    /// <summary>
    /// Example implementation of a worker.
    /// </summary>
    public class Worker : BackgroundService
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<Worker> logger;

        /// <summary>
        /// The SQS client.
        /// </summary>
        private readonly IAmazonSQS sqsClient;

        /// <summary>
        /// The queue URL.
        /// </summary>
        private readonly string queueUrl;

        /// <summary>
        /// Initializes a new instance of the <see cref="Worker" /> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="sqsClient">The SQS client.</param>
        /// <param name="configuration">The configuration.</param>
        public Worker(ILogger<Worker> logger, IAmazonSQS sqsClient, WorkerConfiguration configuration)
        {
            this.logger = logger;
            this.sqsClient = sqsClient;
            this.queueUrl = configuration.QueueUrl;
        }

        /// <summary>
        ///     This method is called when the Microsoft.Extensions.Hosting.IHostedService starts.
        ///     The implementation should return a task that represents the lifetime of the long
        ///     running operation(s) being performed.
        /// </summary>
        /// <param name="stoppingToken">A cancellation token that indicates a request to stop.</param>
        /// <returns>A task.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // every loop should check to see if there was a cancellation request
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Worker running at: {Time}", DateTimeOffset.Now);

                // request a batch of messages. the wait time allows you to wait for messages to show up if there aren't any.
                // this reduces the "receive count" when there isn't much volume.
                var response = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    MaxNumberOfMessages = 10,
                    QueueUrl = queueUrl,
                    WaitTimeSeconds = 20
                });

                logger.LogInformation("Received {MessageCount} message(s) from SQS", response.Messages.Count);

                // loop over the messages
                foreach (var message in response.Messages)
                {
                    // the message body will be a JSON string of the original request
                    var salesforceMessage = JsonSerializer.Deserialize<SalesforceMessage>(message.Body);

                    // this log is special. the {@SalesforceMessage} will cause the object to be serialized into the log (when using structured logging).
                    // you should use the @ sparingly when logging, as serialization can be expensive
                    logger.LogInformation("Processing salesforce message. {@SalesforceMessage}", salesforceMessage);

                    // THIS IS WHERE YOU DO YOUR WORK
                }

                // when we are done we will delete all the messages from SQS. we may want to do this message by message, or track successes with a try/catch and only delete the successes
                if (response.Messages.Count != 0)
                {
                    await sqsClient.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                    {
                        Entries = response.Messages.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString("00"), ReceiptHandle = m.ReceiptHandle }).ToList(),
                        QueueUrl = queueUrl
                    });
                }

                // we may not really need to do this delay, since there is a 20 second delay on the SQS call (if there are no messages), but it shows how you would do a delay if you need/want it
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    /// <summary>
    /// This is a fake representation of what the Salesforce message would be.
    /// This should have any relevant data that you'll get from Salesforce in the initial request.
    /// </summary>
    public class SalesforceMessage
    {
        public string MyField { get; set; }
    }
}
