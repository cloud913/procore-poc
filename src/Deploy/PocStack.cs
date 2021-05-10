using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.APIGateway;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.SQS;

namespace Poc
{
    public class PocStack : Stack
    {
        internal PocStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            var domainName = $"cloud913.xyz";

            // this is the root hosted zone for the domain. it is not created in this stack so that you can manually configure the NS records.
            var hostedZone = HostedZone.FromHostedZoneAttributes(this, "HostedZone", new HostedZoneAttributes
            {
                HostedZoneId = "Z07740723FGRE53M7ML1Q",
                ZoneName = domainName
            });

            // this is a wildcard certificate for the domain. the certificate will be validated automatically via DNS
            var cert = new Certificate(this, "PocWildcardCert", new CertificateProps
            {
                DomainName = $"*.{domainName}",
                Validation = CertificateValidation.FromDns(hostedZone)
            });


            // this is the API Gateway REST API that will be the endpoint for all your connectors and other services
            // this is a regional endpoint, which means that it only exists in the region you deploy it to
            var api = new RestApi(this, "Api", new RestApiProps
            {
                Deploy = true,
                RestApiName = "PocApi",
                Description = "This is a proof of concept",
                DomainName = new DomainNameOptions
                {
                    Certificate = cert,
                    DomainName = $"api.{domainName}",
                    EndpointType = EndpointType.REGIONAL
                },
                DeployOptions = new StageOptions
                {
                    LoggingLevel = MethodLoggingLevel.ERROR
                }
            });

            new RecordSet(this, "ApiRecordSet", new RecordSetProps
            {
                RecordName = $"api.{domainName}",
                Target = new RecordTarget(aliasTarget: new ApiGatewayDomain(api.DomainName)),
                Zone = hostedZone
            });

            var workerQueue = CreateAndConnectWorkerSqsQueue(this, api);

            var (vpc, cluster) = CreateVpcAndCluster(this);

            // this creates a service in the cluster. the code for this service will be from the code in the Worker project.
            // this is a Fargate service, so you don't need to manage any services, just the service definition and the number of workers.
            var workerService = new Amazon.CDK.AWS.ECS.Patterns.QueueProcessingFargateService(this, "WorkerService", new QueueProcessingFargateServiceProps
            {
                Cluster = cluster,
                Image = ContainerImage.FromAsset("./src/Worker/"),
                Cpu = 512,
                MaxReceiveCount = 3,
                MemoryLimitMiB = 1024,
                LogDriver = LogDriver.AwsLogs(new AwsLogDriverProps
                {
                    LogRetention = Amazon.CDK.AWS.Logs.RetentionDays.TWO_WEEKS,
                    StreamPrefix = "worker"
                }),
                MaxHealthyPercent = 200,
                MinHealthyPercent = 100,
                MaxScalingCapacity = 4,
                MinScalingCapacity = 2,
                Queue = workerQueue,
                ServiceName = "Worker",
                CircuitBreaker = new DeploymentCircuitBreaker
                {
                    Rollback = true
                },
                DeploymentController = new DeploymentController
                {
                    Type = DeploymentControllerType.ECS
                },
                EnableLogging = true,

                Environment = new Dictionary<string, string>
                {
                    { "QueueUrl", workerQueue.QueueUrl }
                },
            });
        }

        /// <summary>
        /// Creates the worker SQS queue and connects it to the API gateway.
        /// </summary>
        /// <param name="stack">The stack.</param>
        /// <param name="restApi">The rest API.</param>
        /// <returns>The queue.</returns>
        private static Queue CreateAndConnectWorkerSqsQueue(Stack stack, RestApi restApi)
        {
            // this is an SQS queue that will hold dead-letters from the following queue
            var workerQueueDlq = new Queue(stack, "WorkerDeadLetterQueue");

            // this is an SQS queue that will queue up the messages that come in for the "Worker". it will allow up to 5 minutes to process a
            // message before considering it a failure (this time should be adjusted to a value that is as low as possible without risking processing a
            // message that is still in progress). once a message fails 3 times the message will be sent to the above DLQ
            var workerQueue = new Queue(stack, "WorkerQueue", new QueueProps
            {
                VisibilityTimeout = Duration.Minutes(5),
                DeadLetterQueue = new DeadLetterQueue
                {
                    MaxReceiveCount = 3,
                    Queue = workerQueueDlq
                }
            });

            // this role is used by the API Gateway when it calls other services. you may want this to be unique to each action you take.
            var apiRole = new Amazon.CDK.AWS.IAM.Role(stack, "ApiRole", new RoleProps
            {
                AssumedBy = new ServicePrincipal("apigateway.amazonaws.com"),
            });

            // this allows the role above to send messages to the queue
            workerQueue.GrantSendMessages(apiRole);

            // this is the SQS integration configuration that will be hooked to API Gateway. there is a lot going on here, so I'll add comments inline
            var sqsIntegration = new AwsIntegration(new AwsIntegrationProps
            {
                // the service you are integrating with
                Service = "sqs",
                // the path for SQS is the account Id/queue name
                Path = $"{Aws.ACCOUNT_ID}/{workerQueue.QueueName}",
                Options = new IntegrationOptions
                {
                    // this role can be assumed by API Gateway and has been granted permission to send messages to the SQS queue
                    CredentialsRole = apiRole,
                    // this is never because we don't want to default to anything. if we haven't specified it we don't want to support it
                    PassthroughBehavior = PassthroughBehavior.NEVER,
                    // this is a set of parameters that will be sent to SQS. the only thing we need to specify is the content-type
                    RequestParameters = new Dictionary<string, string>
                    {
                        { "integration.request.header.Content-Type", "'application/x-www-form-urlencoded'" }
                    },
                    // this is what content-types we will support inbound, and how we will map that request to send it to SQS.
                    // notice that the request to SQS has an "Action" of "SendMessage" and the "MessageBody" is set to the body of the input (what the API Gateway receives)
                    RequestTemplates = new Dictionary<string, string>
                    {
                        { "application/json", "Action=SendMessage&MessageBody=$util.urlEncode(\"$input.body\")" }
                    },
                    IntegrationResponses = new IIntegrationResponse[]
                                {
                        // this allows you to return different responses
                        new IntegrationResponse
                        {
                            StatusCode= "202",
                            ResponseTemplates= new Dictionary<string, string>
                            {
                                // this is what will be sent to the caller. this can be whatever we need it to be
                                { "application/json" , "{ \"done\": true}" },
                            },
                        }
                    }
                },

            });

            // this creates a "resource" in your API Gateway that will be reachable at /worker.
            var workerResource = restApi.Root.AddResource("worker");

            // we will only support "POST"s to this resource, but we can add any we want to support
            workerResource.AddMethod("POST", sqsIntegration, new MethodOptions
            {
                MethodResponses = new IMethodResponse[]
                {
                    new MethodResponse
                    {
                        StatusCode = "202"
                    }
                }
            });

            return workerQueue;
        }

        /// <summary>
        /// Creates a VPC and ECS Cluster.
        /// </summary>
        /// <returns>The VPC and the Cluster.</returns>
        private static (Vpc Vpc, Cluster Cluster) CreateVpcAndCluster(Stack stack)
        {
            // a VPC with two availability zones
            var vpc = new Vpc(stack, "Vpc", new VpcProps
            {
                MaxAzs = 2,
                SubnetConfiguration = new ISubnetConfiguration[]
                {
                    new SubnetConfiguration
                    {
                        SubnetType = SubnetType.PUBLIC,
                        Name = "Public"
                    },
                    new SubnetConfiguration
                    {
                        SubnetType = SubnetType.PRIVATE,
                        Name = "Private"
                    }
                },
                NatGateways = 2
            });

            // an ECS cluster that lives in the above VPC
            var cluster = new Cluster(stack, "Cluster", new ClusterProps
            {
                Vpc = vpc,
            });

            return (vpc, cluster);
        }
    }
}
