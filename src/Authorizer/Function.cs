using System.Collections.Generic;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Authorizer
{
    public class Function
    {
        public APIGatewayCustomAuthorizerResponse FunctionHandler(APIGatewayCustomAuthorizerRequest request, ILambdaContext context)
        {
            context.Logger.LogLine($"Authorizing for {JsonSerializer.Serialize(request)}");

            // any logic that needs to be applied to the authorization can be added here. the request includes the information from
            // the HTTP request, as well as some API gateway specific information
            var response = new APIGatewayCustomAuthorizerResponse
            {
                PolicyDocument = new APIGatewayCustomAuthorizerPolicy
                {
                    Statement = new List<APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement>
                    {
                        // the policy grants access to the API gateway resource
                        new APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement
                        {
                            Action = new HashSet<string>
                            {
                                "execute-api:Invoke"
                            },
                            Effect = "Allow",
                            Resource = new HashSet<string>
                            {
                                // this is the full ARN of the resource being requested
                                request.MethodArn
                            }
                        }
                    },
                    Version = "2012-10-17"
                },
            };

            return response;
        }
    }

}
