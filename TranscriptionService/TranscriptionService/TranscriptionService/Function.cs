using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Newtonsoft.Json;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializerAttribute(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace TranscriptionService
{
    public class Functions
    {
        IDynamoDBContext DDBContext { get; set; }

        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        public Functions()
        {
            AWSConfigsDynamoDB.Context.TypeMappings[typeof(Transcript)] = new Amazon.Util.TypeMapping(typeof(Transcript), "Transcripts");            

            var config = new DynamoDBContextConfig { Conversion = DynamoDBEntryConversion.V2 };
            DDBContext = new DynamoDBContext(new AmazonDynamoDBClient(), config);
        }


        /// <summary>
        /// A Lambda function to respond to HTTP Get methods from API Gateway
        /// </summary>
        /// <param name="request"></param>
        /// <returns>The list of blogs</returns>
        public APIGatewayProxyResponse Get(APIGatewayProxyRequest request, ILambdaContext context)
        {
            context.Logger.LogLine("Get Request\n");

            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = "Hello AWS Serverless",
                Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
            };

            return response;
        }

        public async Task<APIGatewayProxyResponse> ProcessTranscripts(APIGatewayProxyRequest request, ILambdaContext context)
        {
            context.Logger.LogLine("Getting blogs");
            var search = DDBContext.ScanAsync<Transcript>(new[] {new ScanCondition("VociRequestId", ScanOperator.IsNull)});
            var transcripts = await search.GetNextSetAsync();
            context.Logger.LogLine($"Found {transcripts.Count} transcripts");

            foreach (var transcript in transcripts)
            {
                await SendTranscriptToVoci(transcript);
            }
            //Save transcripts in DynamoDb

            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = JsonConvert.SerializeObject(transcripts),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };

            return response;
        }

        private async Task SendTranscriptToVoci(Transcript transcript)
        {
            HttpClient client = new HttpClient();

            var url = @"https://vcloud.vocitec.com/transcribe";

            var token = "";//get from env variables (you can find it in LastPass)
            var tokenContent = new StringContent(token);

            //get file from transcript.FileUrl in line below and make zip file
            var fileContent = new StreamContent(new FileStream(@"C:\Temp\file.zip", FileMode.Open));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            var content = new MultipartFormDataContent
            {
                {tokenContent, "token"},
                {fileContent, "file", "file.zip"}
            };

            var result = await client.PostAsync(url, content);
            var response = await result.Content.ReadAsStringAsync();
            //response is in format "{\"requestid\":\"0366be4a-bd34-429e-a69a-6cb1afb85c64\"}"
            //save 
        }

        /* CODE FOR RETRIEVING TRANSCRIPT
         * var requestId = "0366be4a-bd34-429e-a69a-6cb1afb85c64";
           var urlResult =
               $"https://vcloud.vocitec.com/transcribe/result?token={token}&requestid={requestId}";
           var result2 = client.GetAsync(urlResult).Result;
           Console.WriteLine(result2.Content.ReadAsStringAsync().Result);*/
    }
}
